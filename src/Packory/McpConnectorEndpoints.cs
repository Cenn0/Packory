using System.Text;
using System.Text.Json;

namespace Packory;

/// <summary>
/// Administrator-only connector provisioning. MCP callers never receive these
/// endpoints, connection strings, encrypted envelopes, or keyring material.
/// </summary>
public static class McpConnectorEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/mcp-connectors");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var ctx = invocation.HttpContext;
            var settings = ctx.RequestServices.GetRequiredService<Settings>();
            ctx.Response.Headers.CacheControl = "no-store";
            if (!settings.McpConnectorsEnabled) return Results.NotFound();
            if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
            if (settings.InstanceRole != "writer" || settings.RecoveryMaintenance)
                return Results.Json(new { error = "Connector administration requires a writer." }, statusCode: 503);
            var origin = ctx.Request.Headers.Origin.ToString();
            if ((origin.Length > 0 &&
                 (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) ||
                  parsed.AbsolutePath != "/" ||
                  origin != new Uri(settings.Base).GetLeftPart(UriPartial.Authority))) ||
                ctx.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                return Results.StatusCode(403);
            try { return await next(invocation); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or
                FormatException or ArgumentException or System.Security.Cryptography.CryptographicException)
            { return Results.BadRequest(new { error = "Invalid connector policy or keyring." }); }
            catch (InvalidOperationException)
            { return Results.Conflict(new { error = "Connector revision or policy conflict." }); }
            catch (IOException)
            { return Results.Json(new { error = "Connector storage is unavailable." }, statusCode: 503); }
        });

        group.MapGet("/", (McpConnectorStore store) =>
            Results.Ok(new { items = store.List() }));

        group.MapGet("/{id}", (string id, McpConnectorStore store) =>
        {
            var value = store.Get(id);
            return value is null ? Results.NotFound() :
                Results.Ok(new McpConnectorStore.Public(value.Definition, value.Ciphertext is not null));
        });

        group.MapPut("/{id}", async (string id, McpConnectorStore store, HttpContext context) =>
        {
            var change = await ReadAsync(context);
            await store.PolicyGate.WaitAsync(context.RequestAborted);
            try
            {
                var saved = store.Save(id, change, Access.Actor(context));
                return Results.Ok(saved);
            }
            finally { store.PolicyGate.Release(); }
        }).RequireRateLimiting("publish");

        // Revocation removes the ciphertext, not just the enabled bit.
        group.MapPost("/{id}/revoke", async (string id, McpConnectorStore store, HttpContext context) =>
        {
            await store.PolicyGate.WaitAsync(context.RequestAborted);
            try
            {
                return store.Revoke(id, Access.Actor(context)) ?
                    Results.NoContent() : Results.NotFound();
            }
            finally { store.PolicyGate.Release(); }
        }).RequireRateLimiting("publish");

        group.MapGet("/audit", (McpConnectorStore store) =>
            Results.Ok(new { items = store.History() }));
    }

    private static async Task<McpConnectorChange> ReadAsync(HttpContext context)
    {
        if (!context.Request.HasJsonContentType()) throw new InvalidDataException("JSON is required.");
        var bytes = await BlobStore.ReadBoundedAsync(context.Request.Body, 32768, context.RequestAborted);
        return SkillAiJson.Read<McpConnectorChange>(new UTF8Encoding(false, true).GetString(bytes), 32768);
    }
}
