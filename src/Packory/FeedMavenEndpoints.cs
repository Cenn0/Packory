namespace Packory;

/// <summary>Private hosted Maven routes authorize before any scoped metadata or byte lookup.</summary>
public static class FeedMavenEndpoints
{
    private static bool Active(IPackageDatabase db, string feedId) =>
        db.Feed(feedId) is
        { Kind: "maven", Mode: "hosted", Visibility: "private", Enabled: true };

    public static void Map(WebApplication app)
    {
        app.MapMethods("/feeds/{feedId}/maven/{**path}", ["GET", "HEAD"], async (
            string feedId, string path, Settings cfg, IPackageDatabase db,
            BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            return await MavenEndpoints.Read(feedId, path, cfg, db, blobs, ctx);
        });
        app.MapPut("/feeds/{feedId}/maven/{**path}", async (
            string feedId, string path, Settings cfg, IPackageDatabase db,
            BlobStore blobs, ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId, publish: true) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            return await MavenEndpoints.Write(feedId, path, cfg, db, blobs, locks, ctx);
        }).RequireRateLimiting("publish");
    }
}
