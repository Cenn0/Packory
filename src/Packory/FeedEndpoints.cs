namespace Packory;

/// <summary>
/// V3 read-only feed metadata inventory. Route naming deliberately avoids
/// implying operational multi-feed isolation or write support.
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
