using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Packory;

/// <summary>Administrator-only MCP connector lifecycle and explicit tool access.
/// This API is intentionally separate from the inert MCP registry metadata catalog.</summary>
public static class McpConnectorEndpoints
{
    private sealed record CallRequest(string Tool, JsonElement Arguments);

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v1/admin/mcp/connectors");
        group.AddEndpointFilter(async (context, next) =>
        {
            var ctx = context.HttpContext;
            var settings = ctx.RequestServices.GetRequiredService<Settings>();
            ctx.Response.Headers.CacheControl = "no-store";
            if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
            if (settings.InstanceRole == "reader" || settings.RecoveryMaintenance)
                return Results.Json(new { error = "Connector administration requires an active writer." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            var origin = ctx.Request.Headers.Origin.ToString();
            if ((origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                    origin != new Uri(settings.PublicBaseUrl).GetLeftPart(UriPartial.Authority) ||
                    uri.AbsolutePath != "/")) ||
                ctx.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                return Results.StatusCode(403);
            try { return await next(context); }
            catch (McpConnectorKeyringException)
            { return Results.Json(new { error = "Connector credential keyring is unavailable." }, statusCode: 503); }
            catch (CryptographicException)
            { return Results.Json(new { error = "Connector credential cannot be decrypted." }, statusCode: 503); }
            catch (Exception e) when (e is InvalidDataException or ArgumentException or FormatException or JsonException)
            { return Results.BadRequest(new { error = "Invalid connector configuration or arguments." }); }
            catch (InvalidOperationException)
            { return Results.Conflict(new { error = "Connector revision changed or capacity reached." }); }
            catch (UnauthorizedAccessException)
            { return Results.Json(new { error = "Connector or tool is not enabled and allowed." }, statusCode: 403); }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
            { return Results.Json(new { error = "Remote MCP request timed out." }, statusCode: 504); }
            catch (Exception e) when (e is IOException or HttpRequestException or McpException)
            { return Results.Json(new { error = "Remote MCP server connection failed." }, statusCode: 502); }
        });

        group.MapGet("", (McpConnectorStore store, McpConnectorPolicy policy) =>
            Results.Ok(new
            {
                items = store.List().Take(100).Select(c => c.Summary()),
                allowedOrigins = policy.AllowedOrigins,
                egressConfigured = policy.AllowedOrigins.Count > 0,
                qualification = "Administrator-owned remote Streamable HTTP connections; tools must be explicitly allowed"
            }));

        group.MapGet("/{id}", (string id, McpConnectorStore store) =>
        {
            var record = store.Get(id);
            return record is null ? Results.NotFound() : Results.Ok(record.Summary());
        });

        group.MapPost("", async (McpConnectorStore store, HttpContext ctx) =>
        {
            var body = await ReadAsync<McpConnectorChange>(ctx);
            if (body.Revision != 0) return Results.BadRequest(new { error = "Creation requires revision zero." });
            var saved = store.Save(body, Access.Actor(ctx));
            return Results.Json(saved.Summary(), statusCode: 201);
        }).RequireRateLimiting("publish");

        group.MapPut("/{id}", async (string id, McpConnectorStore store, HttpContext ctx) =>
        {
            var body = await ReadAsync<McpConnectorChange>(ctx);
            if (body.Id != id || body.Revision < 1)
                return Results.BadRequest(new { error = "Path, connector ID and revision must agree." });
            var saved = store.Save(body, Access.Actor(ctx));
            return Results.Ok(saved.Summary());
        }).RequireRateLimiting("publish");

        group.MapDelete("/{id}", (string id, long? revision, McpConnectorStore store, HttpContext ctx) =>
        {
            if (revision is null || revision < 1 || !McpConnectorPolicy.ValidId(id))
                return Results.BadRequest();
            return store.Delete(id, revision.Value, Access.Actor(ctx))
                ? Results.NoContent() : Results.Conflict(new { error = "Connector not found or stale revision." });
        }).RequireRateLimiting("publish");

        group.MapPost("/{id}/probe", async (string id, McpConnectorStore store,
            IMcpConnectorRuntime runtime, HttpContext ctx) =>
        {
            var record = store.Get(id);
            if (record is null) return Results.NotFound();
            var discovered = await runtime.DiscoverAsync(record, ctx.RequestAborted);
            return Results.Ok(new { id, tools = discovered, count = discovered.Count });
        }).RequireRateLimiting("publish");

        group.MapPost("/{id}/call", async (string id, McpConnectorStore store,
            IMcpConnectorRuntime runtime, HttpContext ctx) =>
        {
            var body = await ReadAsync<CallRequest>(ctx);
            var record = store.Get(id);
            if (record is null) return Results.NotFound();
            if (!record.Enabled || !McpConnectorPolicy.ValidTool(body.Tool) ||
                !record.AllowedTools.Contains(body.Tool, StringComparer.Ordinal))
                return Results.Json(new { error = "Connector tool must be enabled and explicitly approved." },
                    statusCode: 403);
            store.RecordCall(id, body.Tool, Access.Actor(ctx));
            var result = await runtime.CallAsync(record, body.Tool, body.Arguments, ctx.RequestAborted);
            return Results.Ok(new { id, tool = body.Tool, result });
        }).RequireRateLimiting("publish");
    }

    private static async Task<T> ReadAsync<T>(HttpContext ctx)
    {
        if (!ctx.Request.HasJsonContentType())
            throw new InvalidDataException("JSON body required.");
        if (ctx.Request.ContentLength is > 65536)
            throw new InvalidDataException("Connector request exceeds 64 KiB.");
        var bytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body, 65536, ctx.RequestAborted);
        return SkillAiJson.Read<T>(new UTF8Encoding(false, true).GetString(bytes), 65536);
    }
}

/// <summary>Explicit administrative MCP gateway; never exposed to general read-only callers.</summary>
[McpServerToolType]
public sealed class McpConnectorMcpTools(IHttpContextAccessor accessor, Settings settings, McpConnectorStore store,
    IMcpConnectorRuntime runtime)
{
    private static bool IsAdmin(HttpContext? ctx) =>
        ctx is not null && Access.Actor(ctx) is "admin-token" or "oidc-admin";

    private HttpContext Authorized(string connectorId, string? toolName = null)
    {
        var ctx = accessor.HttpContext;
        if (ctx is null || (!IsAdmin(ctx) &&
            !Access.HasConnectorGrant(ctx, settings, connectorId, toolName)))
            throw new McpProtocolException("An explicit connector tool capability is required",
                McpErrorCode.InvalidParams);
        return ctx;
    }

    [McpServerTool(Name = "list_connector_tools", ReadOnly = true)]
    public async Task<object> ListConnectorTools(string connectorId,
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorized(connectorId);
        var record = store.Get(connectorId) ?? throw new McpProtocolException(
            "Unknown connector", McpErrorCode.InvalidParams);
        var tools = await runtime.DiscoverAsync(record, cancellationToken);
        return new { connectorId, tools = IsAdmin(ctx) ? tools : tools.Where(t =>
            t.Allowed && Access.HasConnectorGrant(ctx, settings, connectorId, t.Name)).ToArray() };
    }

    [McpServerTool(Name = "call_connector_tool", ReadOnly = false)]
    public async Task<object> CallConnectorTool(string connectorId, string toolName,
        string argumentsJson, CancellationToken cancellationToken = default)
    {
        var ctx = Authorized(connectorId, toolName);
        var record = store.Get(connectorId) ?? throw new McpProtocolException(
            "Unknown connector", McpErrorCode.InvalidParams);
        if (!record.Enabled || !McpConnectorPolicy.ValidTool(toolName) ||
            !record.AllowedTools.Contains(toolName, StringComparer.Ordinal))
            throw new McpProtocolException("Connector tool is not approved",
                McpErrorCode.InvalidParams);
        var args = SkillAiJson.Read<JsonElement>(argumentsJson, 65536);
        if (args.ValueKind != JsonValueKind.Object)
            throw new McpProtocolException("Tool arguments must be a JSON object",
                McpErrorCode.InvalidParams);
        store.RecordCall(connectorId, toolName, Access.Actor(ctx));
        var output = await runtime.CallAsync(record, toolName, args, cancellationToken);
        return new { connectorId, tool = toolName, result = output };
    }
}
