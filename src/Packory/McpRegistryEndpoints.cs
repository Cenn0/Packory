using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Packory;

/// <summary>Versioned REST discovery and operator-only metadata lifecycle.
/// Nothing here can forward tokens, contact remotes, or execute their tools.</summary>
public static class McpRegistryEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/mcp/servers", (string? q, int? limit, int? offset,
            Settings settings, McpRegistryStore store, HttpContext context) =>
        {
            if (Access.Require(context, settings) is { } denied) return denied;
            if (limit is < 1 or > 50 || offset is < 0 or > 10000 ||
                q is { Length: > 100 } || (q?.Any(char.IsControl) ?? false))
                return Results.BadRequest(new { error = "Invalid MCP catalog query" });
            var size = limit ?? 25;
            var start = offset ?? 0;
            var page = store.List(q, size + 1, start);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new
            {
                apiVersion = "v1", items = page.Take(size).Select(x => x.Summary),
                nextOffset = page.Count > size ? start + size : (int?)null,
                qualification = "operator-reviewed metadata only; publisher ownership and runtime safety unverified"
            });
        });

        app.MapGet("/api/v1/mcp/server", (string? name, string? version,
            Settings settings, McpRegistryStore store, HttpContext context) =>
        {
            if (Access.Require(context, settings) is { } denied) return denied;
            context.Response.Headers.CacheControl = "no-store";
            var entry = name is null || version is null ? null : store.Find(name, version);
            return entry is null ? Results.NotFound() : Results.Json(entry.Detail);
        });

        app.MapGet("/api/v1/admin/mcp/servers", (int? limit, int? offset,
            Settings settings, McpRegistryStore store, HttpContext context) =>
        {
            if (Access.RequireAdmin(context, settings) is { } denied) return denied;
            if (limit is < 1 or > 50 || offset is < 0 or > 10000)
                return Results.BadRequest();
            var size = limit ?? 25;
            var start = offset ?? 0;
            var page = store.List(null, size + 1, start, admin: true);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new
            {
                items = page.Take(size).Select(x => x.Summary),
                nextOffset = page.Count > size ? start + size : (int?)null
            });
        });

        app.MapPost("/api/v1/admin/mcp/servers/stage", async (
            Settings settings, McpRegistryStore store, HttpContext context) =>
        {
            if (Access.RequireAdmin(context, settings) is { } denied) return denied;
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.ContentLength is > 16384)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            byte[] bytes;
            try
            {
                bytes = await BlobStore.ReadBoundedAsync(context.Request.Body,
                    16384, context.RequestAborted);
            }
            catch (PayloadTooLargeException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            McpRegistryManifest manifest;
            try { manifest = McpRegistryManifest.Parse(bytes); }
            catch (InvalidDataException)
            {
                return Results.BadRequest(new { error = "Unsupported or invalid remote-only server.json" });
            }
            if (!store.Stage(manifest))
                return Results.Conflict(new { error = "Immutable name/version already exists" });
            return Results.Json(new
            {
                manifest.Name, manifest.Version, manifest.Sha256,
                state = "PENDING", publisherVerified = false,
                next = "Operator must review ownership, remote risks and exact digest before publishing"
            }, statusCode: StatusCodes.Status202Accepted);
        }).RequireRateLimiting("publish");

        app.MapPost("/api/v1/admin/mcp/servers/decide", async (
            Settings settings, McpRegistryStore store, HttpContext context) =>
        {
            if (Access.RequireAdmin(context, settings) is { } denied) return denied;
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.ContentLength is > 2048)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            byte[] bytes;
            try
            {
                bytes = await BlobStore.ReadBoundedAsync(context.Request.Body,
                    2048, context.RequestAborted);
            }
            catch (PayloadTooLargeException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
            string name, version, sha256, action, reason;
            try
            {
                using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions
                    { MaxDepth = 4, AllowTrailingCommas = false });
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    root.EnumerateObject().Count() != 5 ||
                    root.EnumerateObject().Select(p => p.Name)
                        .Distinct(StringComparer.Ordinal).Count() != 5 ||
                    root.EnumerateObject().Any(p => p.Name is not
                        ("name" or "version" or "sha256" or "action" or "reason") ||
                        p.Value.ValueKind != JsonValueKind.String))
                    return Results.BadRequest(new { error = "Invalid decision payload" });
                name = root.GetProperty("name").GetString()!;
                version = root.GetProperty("version").GetString()!;
                sha256 = root.GetProperty("sha256").GetString()!;
                action = root.GetProperty("action").GetString()!;
                reason = root.GetProperty("reason").GetString()!;
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException)
            {
                return Results.BadRequest(new { error = "Invalid decision JSON" });
            }
            if (!store.Decide(name, version, sha256, action, reason, Access.Actor(context)))
                return Results.Conflict(new { error = "Decision rejected: invalid request, stale digest or state" });
            return Results.Ok(new
            {
                name, version, state = action == "publish" ? "PUBLISHED" : "REVOKED",
                publisherVerified = false
            });
        }).RequireRateLimiting("publish");
    }
}

[McpServerToolType]
public sealed class McpRegistryMcpTools
{
    [McpServerTool(Name = "search_mcp_servers", ReadOnly = true)]
    [Description("Find only operator-published, version-pinned remote MCP server descriptors. No live probing, ownership proof, connection or execution.")]
    public static object SearchMcpServers(string query, McpRegistryStore store, int limit = 20)
    {
        if (query.Length > 100 || query.Any(char.IsControl))
            return new { error = "Invalid query" } as object;
        return new { items = store.List(query, Math.Clamp(limit, 1, 50))
            .Select(x => x.Summary).ToArray(), publisherVerified = false };
    }

    [McpServerTool(Name = "get_mcp_server", ReadOnly = true)]
    [Description("Read a published immutable MCP server.json revision. Metadata has not been independently verified against its publisher.")]
    public static object GetMcpServer(string name, string version, McpRegistryStore store)
    {
        var found = store.Find(name, version);
        return found is null ? new { status = "unavailable" } as object : found.Detail;
    }
}

[McpServerResourceType]
public sealed class McpRegistryMcpResources
{
    [McpServerResource(UriTemplate = "packory://mcp/registry/info",
        Name = "Packory MCP Registry Capability", MimeType = "application/json")]
    [Description("Describe the operator-reviewed, non-executing MCP server metadata registry.")]
    public static string Info() => JsonSerializer.Serialize(new
    {
        schema = McpRegistryManifest.Schema,
        mode = "read-only discovery; HTTPS Streamable HTTP metadata only",
        operations = new[] { "search_mcp_servers", "get_mcp_server" },
        publisherVerified = false, safeToConnect = false,
        note = "Packory does not connect to or run remote servers. Clients enforce independent endpoint, provenance and token policies."
    });
}
