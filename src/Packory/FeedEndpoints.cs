using System.Text.Json;

namespace Packory;

/// <summary>
/// V3 feed metadata inventory and disabled-only draft lifecycle.
/// Drafts do not route native clients or confer any isolation/authorization.
/// </summary>
public static class FeedEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/admin/feeds", (Settings cfg, IPackageDatabase db,
            HttpContext ctx, string? after, int? limit) =>
        {
            if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
            if (after is not null && !FeedDefinition.ValidId(after))
                return Results.BadRequest(new { error = "Invalid cursor" });
            if (limit is < 1 or > 100)
                return Results.BadRequest(new { error = "Limit must be from 1 to 100" });
            var pageSize = limit ?? 25;
            var rows = db.Feeds(after, pageSize + 1);
            var hasMore = rows.Count > pageSize;
            var results = hasMore ? rows.Take(pageSize).ToArray() : rows.ToArray();
            var nextCursor = hasMore ? results[^1].Id : null;
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { apiVersion = "v1", items = results,
                nextCursor, isolationSupported = false });
        });

        app.MapPost("/api/v1/admin/feeds/drafts", async (Settings cfg, IPackageDatabase db,
            HttpContext ctx) =>
        {
            if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
            ctx.Response.Headers.CacheControl = "no-store";
            if (ctx.Request.ContentLength is > 4096)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            byte[] bytes;
            try { bytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body, 4096, ctx.RequestAborted); }
            catch (PayloadTooLargeException)
            { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            FeedDefinition? feed;
            try
            {
                using var document = JsonDocument.Parse(bytes,
                    new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false });
                var obj = document.RootElement;
                if (obj.ValueKind != JsonValueKind.Object) return Results.BadRequest(new { error = "Expected object" });
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in obj.EnumerateObject())
                    if (!names.Add(property.Name) ||
                        property.Name is not ("id" or "kind" or "mode" or "displayName") ||
                        property.Value.ValueKind != JsonValueKind.String)
                        return Results.BadRequest(new { error = "Invalid or duplicate field" });
                if (names.Count != 4) return Results.BadRequest(new { error = "Missing required fields" });
                var id = obj.GetProperty("id").GetString();
                var kind = obj.GetProperty("kind").GetString();
                var mode = obj.GetProperty("mode").GetString();
                var displayName = obj.GetProperty("displayName").GetString();
                if (!FeedDefinition.ValidId(id) || id!.StartsWith("default-", StringComparison.Ordinal) ||
                    kind is not ("nuget" or "npm" or "cargo" or "pypi" or "oci" or "skill" or "raw" or "maven" or "go") ||
                    mode is not ("hosted" or "proxy") || string.IsNullOrWhiteSpace(displayName) ||
                    displayName.Length > 120 ||
                    !string.Equals(displayName, displayName.Trim(), StringComparison.Ordinal) ||
                    displayName.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
                    return Results.BadRequest(new { error = "Invalid draft feed specification" });
                var at = DateTimeOffset.UtcNow.ToString("O");
                feed = new FeedDefinition(id, kind, mode, "private", displayName, false, at, at);
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid JSON" }); }
            if (!db.TryCreateDraftFeed(feed, Access.Actor(ctx)))
                return Results.Conflict(new { error = "Feed ID already exists" });
            return Results.Json(new { apiVersion = "v1", feed,
                isolationSupported = false, operational = false }, statusCode: StatusCodes.Status201Created);
        }).RequireRateLimiting("publish");

        app.MapDelete("/api/v1/admin/feeds/drafts/{id}", (string id, Settings cfg,
            IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
            ctx.Response.Headers.CacheControl = "no-store";
            if (!FeedDefinition.ValidId(id)) return Results.BadRequest(new { error = "Invalid feed ID" });
            // Preserve existing default feeds and every draft referenced by policy,
            // upstream or namespace metadata. A no-op is a conflict, not success.
            if (!db.TryDeleteDraftFeed(id, Access.Actor(ctx)))
                return Results.Conflict(new { error = "Feed is not a removable unused draft" });
            return Results.NoContent();
        }).RequireRateLimiting("publish");

        // Activating a reviewed private NuGet-hosted draft is a separate,
        // auditable administrator decision; proxy/group/other ecosystems remain
        // draft-only until their native isolation suites are implemented.
        app.MapPost("/api/v1/admin/feeds/{id}/activate", (
            string id, Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
            if (!FeedDefinition.ValidId(id)) return Results.BadRequest();
            ctx.Response.Headers.CacheControl = "no-store";
            var feed = db.Feed(id);
            if (feed is not { Kind: "nuget" or "raw" or "maven", Mode: "hosted",
                Visibility: "private", Enabled: false } ||
                !db.TrySetHostedFeedEnabled(id, feed.Kind, true, Access.Actor(ctx)))
                return Results.Conflict(new { error = "Only reviewed private NuGet/Raw hosted drafts can be activated" });
            return Results.Ok(new { id, enabled = true, kind = feed.Kind, mode = "hosted",
                visibility = "private" });
        }).RequireRateLimiting("publish");

        app.MapPost("/api/v1/admin/feeds/{id}/deactivate", (
            string id, Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
            if (!FeedDefinition.ValidId(id)) return Results.BadRequest();
            ctx.Response.Headers.CacheControl = "no-store";
            var feed = db.Feed(id);
            if (feed is not { Kind: "nuget" or "raw" or "maven", Mode: "hosted",
                Visibility: "private", Enabled: true } ||
                !db.TrySetHostedFeedEnabled(id, feed.Kind, false, Access.Actor(ctx)))
                return Results.Conflict(new { error = "Only active private NuGet/Raw hosted feeds can be disabled" });
            // Keep every revision, digest, journal and blob. Re-activation
            // is a separate explicit operator action; no GC is performed.
            return Results.Ok(new { id, enabled = false, retained = true });
        }).RequireRateLimiting("publish");

        app.MapGet("/api/v1/admin/feeds/{id}", (string id, Settings cfg,
            IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
            if (!FeedDefinition.ValidId(id))
                return Results.BadRequest(new { error = "Invalid feed ID" });
            var feed = db.Feed(id);
            ctx.Response.Headers.CacheControl = "no-store";
            return feed is null ? Results.NotFound() : Results.Json(feed);
        });
    }
}
