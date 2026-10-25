using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Packory;

/// <summary>Versioned REST discovery and operator-only metadata lifecycle.
/// Nothing here can forward tokens, contact remotes, or execute their tools.</summary>
public static class McpRegistryEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/v0.1/servers", (Settings settings, McpRegistryStore store,
            HttpContext context) =>
        {
            if (RequireRead(context, settings) is { } denied) return denied;
            context.Response.Headers.CacheControl = "no-store";
            if (ReadDiscoveryQuery(context, false, out var query) is { } invalid) return invalid;
            try
            {
                var page = store.ListPublishedPage(query.Search, query.Limit, query.Cursor,
                    version: query.Version, cancellationToken: context.RequestAborted);
                return Results.Json(McpRegistryDiscovery.Response(page, query.Version == "latest"));
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { error = "Invalid registry query or cursor filters" });
            }
        });

        app.MapGet("/v0.1/servers/{serverName}/versions", (string serverName,
            Settings settings, McpRegistryStore store, HttpContext context) =>
        {
            if (RequireRead(context, settings) is { } denied) return denied;
            context.Response.Headers.CacheControl = "no-store";
            if (ReadDiscoveryQuery(context, true, out var query) is { } invalid) return invalid;
            var name = Uri.UnescapeDataString(serverName);
            if (!McpRegistryManifest.ValidName(name))
                return Results.BadRequest(new { error = "Invalid registry server name" });
            try
            {
                var page = store.ListPublishedPage(limit: query.Limit, cursor: query.Cursor,
                    name: name, cancellationToken: context.RequestAborted);
                return page.Items.Count == 0 && page.NextCursor is null && query.Cursor is null
                    ? Results.NotFound(new { error = "Server not found" })
                    : Results.Json(McpRegistryDiscovery.Response(page));
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { error = "Invalid registry query or cursor filters" });
            }
        });

        app.MapGet("/v0.1/servers/{serverName}/versions/{version}", (string serverName,
            string version, Settings settings, McpRegistryStore store, HttpContext context) =>
        {
            if (RequireRead(context, settings) is { } denied) return denied;
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Query.Any(x => x.Key != "include_deleted" || x.Value.Count != 1) ||
                (context.Request.Query.TryGetValue("include_deleted", out var deleted) &&
                    (!bool.TryParse(deleted, out var include) || include)))
                return Results.BadRequest(new { error = "Only published registry records are supported" });
            var name = Uri.UnescapeDataString(serverName);
            var requestedVersion = Uri.UnescapeDataString(version);
            if (!McpRegistryManifest.ValidName(name) ||
                (requestedVersion != "latest" && !McpRegistryManifest.ValidVersion(requestedVersion)))
                return Results.BadRequest(new { error = "Invalid registry server name or version" });
            var entry = requestedVersion == "latest"
                ? store.FindLatest(name, context.RequestAborted) : store.Find(name, requestedVersion);
            return entry is null ? Results.NotFound(new { error = "Server not found" })
                : Results.Json(McpRegistryDiscovery.Response(entry, requestedVersion == "latest"));
        });

        app.MapGet("/api/v1/mcp/servers", (string? q, int? limit, int? offset, string? cursor,
            Settings settings, McpRegistryStore store, HttpContext context) =>
        {
            if (RequireRead(context, settings) is { } denied) return denied;
            if (limit is < 1 or > 50 || offset is < 0 or > 10000 ||
                (cursor is not null && offset is not null) ||
                q is { Length: > 100 } || (q?.Any(char.IsControl) ?? false))
                return Results.BadRequest(new { error = "Invalid MCP catalog query" });
            var size = limit ?? 25;
            var start = offset ?? 0;
            context.Response.Headers.CacheControl = "no-store";
            if (offset is null)
            {
                try
                {
                    var cursorPage = store.ListPublishedPage(q, size, cursor,
                        searchTitles: true, cancellationToken: context.RequestAborted);
                    return Results.Json(new
                    {
                        apiVersion = "v1", items = cursorPage.Items.Select(x => x.Summary),
                        nextCursor = cursorPage.NextCursor,
                        // Retain the old first-page continuation for clients
                        // using offsets. New consumers use nextCursor only.
                        nextOffset = cursor is null && cursorPage.NextCursor is not null ? size : (int?)null,
                        qualification = "operator-reviewed metadata only; publisher ownership and runtime safety unverified"
                    });
                }
                catch (ArgumentException)
                {
                    return Results.BadRequest(new { error = "Invalid MCP catalog query or cursor" });
                }
            }
            var page = store.List(q, size + 1, start);
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
            if (RequireRead(context, settings) is { } denied) return denied;
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

        app.MapGet("/api/v1/admin/mcp/server", (string? name, string? version,
            Settings settings, McpRegistryStore store, HttpContext context) =>
        {
            if (Access.RequireAdmin(context, settings) is { } denied) return denied;
            context.Response.Headers.CacheControl = "no-store";
            if (name is null || version is null || !McpRegistryManifest.ValidName(name) ||
                !McpRegistryManifest.ValidVersion(version)) return Results.BadRequest();
            var entry = store.Find(name, version, admin: true);
            return entry is null ? Results.NotFound()
                : Results.Ok(new { entry = entry.Detail, history = store.History(name, version) });
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
                return Results.BadRequest(new { error = "Unsupported or invalid inert server.json" });
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

    // A protected global registry requires a global reader. A managed feed or
    // package-publishing grant must not become a registry-wide metadata grant.
    private static IResult? RequireRead(HttpContext context, Settings settings) =>
        Access.RequireDashboardGlobalRead(context, settings);

    private sealed record DiscoveryQuery(string? Search, int Limit, string? Cursor, string? Version);

    private static IResult? ReadDiscoveryQuery(HttpContext context, bool versions,
        out DiscoveryQuery result)
    {
        result = new DiscoveryQuery(null, McpRegistryDiscovery.DefaultPageSize, null, null);
        var values = context.Request.Query;
        var allowed = versions ? new[] { "limit", "cursor", "include_deleted" }
            : new[] { "limit", "cursor", "search", "version", "include_deleted", "updated_since" };
        if (values.Any(x => !allowed.Contains(x.Key, StringComparer.Ordinal) || x.Value.Count != 1))
            return Results.BadRequest(new { error = "Unknown or repeated registry query parameter" });
        // Incremental synchronization requires non-distributable tombstones.
        // Until that independent contract exists, reject it explicitly rather
        // than returning an incomplete change feed or exposing revoked content.
        if (values.ContainsKey("updated_since") ||
            (values.TryGetValue("include_deleted", out var deleted) &&
                (!bool.TryParse(deleted, out var includeDeleted) || includeDeleted)))
            return Results.BadRequest(new { error = "Deleted-record synchronization is not supported by the published-only profile" });
        var size = McpRegistryDiscovery.DefaultPageSize;
        if (values.TryGetValue("limit", out var limit) &&
            (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out size) ||
                size is < 1 or > McpRegistryDiscovery.MaximumPageSize))
            return Results.BadRequest(new { error = "Registry limit must be between 1 and 100" });
        var search = values.TryGetValue("search", out var searchValue) ? searchValue.ToString() : null;
        var version = values.TryGetValue("version", out var versionValue) ? versionValue.ToString() : null;
        if (!McpRegistryDiscovery.ValidSearch(search) ||
            (version is not null && version != "latest" && !McpRegistryManifest.ValidVersion(version)))
            return Results.BadRequest(new { error = "Invalid registry search or version filter" });
        result = new DiscoveryQuery(search, size,
            values.TryGetValue("cursor", out var cursor) ? cursor.ToString() : null, version);
        return null;
    }
}

[McpServerToolType]
public sealed class McpRegistryMcpTools
{
    [McpServerTool(Name = "search_mcp_servers", ReadOnly = true)]
    [Description("Find operator-published MCP server descriptors by name with cursor pagination. Repeat the same query and pass nextCursor to continue. No connection, ownership verification or execution.")]
    public static object SearchMcpServers(string query, McpRegistryStore store, int limit = 20,
        string? cursor = null, string? version = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var page = store.ListPublishedPage(query, limit, cursor, version: version,
                cancellationToken: cancellationToken);
            return new { items = page.Items.Select(x => x.Summary).ToArray(),
                nextCursor = page.NextCursor, publisherVerified = false };
        }
        catch (ArgumentException)
        {
            throw new ModelContextProtocol.McpProtocolException("Invalid MCP registry query or cursor filters",
                ModelContextProtocol.McpErrorCode.InvalidParams);
        }
    }

    [McpServerTool(Name = "get_mcp_server", ReadOnly = true)]
    [Description("Read a published immutable MCP server.json revision. Metadata has not been independently verified against its publisher.")]
    public static object GetMcpServer(string name, string version, McpRegistryStore store)
    {
        var found = version == "latest" ? store.FindLatest(name) : store.Find(name, version);
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
        registryApiVersion = McpRegistryDiscovery.ApiVersion,
        registrySpecification = McpRegistryDiscovery.SpecificationVersion,
        registryProfile = "published-only discovery with cursor pagination; synchronization unavailable",
        registryRoutes = new[] { "/v0.1/servers", "/v0.1/servers/{serverName}/versions",
            "/v0.1/servers/{serverName}/versions/{version}" },
        mode = "read-only discovery; HTTPS Streamable HTTP metadata only",
        operations = new[] { "search_mcp_servers", "get_mcp_server" },
        publisherVerified = false, safeToConnect = false,
        note = "Packory does not connect to or run remote servers. Clients enforce independent endpoint, provenance and token policies."
    });
}
