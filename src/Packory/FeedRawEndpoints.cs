using System.Security.Cryptography;

namespace Packory;

/// <summary>
/// First shared-ledger E0 adapter: immutable, independently authorized raw
/// bytes under a private hosted feed. No proxy, global fallback or catalog
/// visibility is possible through these routes.
/// </summary>
public static class FeedRawEndpoints
{
    public static string BlobKey(string feedId, string name, string version) =>
        $"feeds/{feedId}/raw/{name}/{version}/payload.blob";

    private static bool Active(IPackageDatabase db, string feedId) =>
        db.Feed(feedId) is
        { Kind: "raw", Mode: "hosted", Visibility: "private", Enabled: true };

    private static bool Valid(string name, string version) =>
        RawEndpoints.ValidName(name) && RawEndpoints.ValidVersion(version);

    public static void Map(WebApplication app)
    {
        app.MapGet("/feeds/{feedId}/raw/usage", (
            string feedId, Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "private, no-store";
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            var usage = db.RawFeedUsage(feedId);
            return Results.Json(new { feedId, usage.Artifacts, usage.Bytes,
                maxBytes = cfg.MaxRawFeedBytes, maxArtifacts = cfg.MaxRawFeedArtifacts,
                accounting = "committed-private-raw-assets", retention = "retain-immutable-no-online-delete" });
        });

        app.MapGet("/feeds/{feedId}/raw/", (
            string feedId, string? prefix, string? cursor, int? limit,
            Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "private, no-store";
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            prefix ??= "";
            var count = limit ?? 30;
            if (count is < 1 or > 100 || prefix.Length > 128 ||
                prefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_')))
                return Results.BadRequest(new { error = "Invalid Raw page query" });
            var scope = "private-raw/" + feedId + "/" + prefix;
            string? after = null;
            try
            {
                if (McpSkillCursor.Decode(cursor, scope) is { } marker)
                {
                    var parts = marker.First.Split('/');
                    if (parts.Length != 2 || marker.Second != "" || !Valid(parts[0], parts[1]))
                        return Results.BadRequest(new { error = "Invalid Raw page cursor" });
                    after = marker.First;
                }
            }
            catch (ModelContextProtocol.McpProtocolException)
            { return Results.BadRequest(new { error = "Invalid Raw page cursor" }); }
            var rows = db.RawFeedPage(feedId, after, prefix, count + 1);
            if (rows.Any(row => !FeedAssetEntry.Valid(row))) return Results.StatusCode(503);
            var items = rows.Take(count).Select(row => new
            {
                row.Name, row.Version, row.MediaType, row.Sha256, row.Size, row.CreatedAt,
                url = cfg.Base + "/feeds/" + feedId + "/raw/" +
                    Uri.EscapeDataString(row.Name) + "/" + Uri.EscapeDataString(row.Version)
            }).ToArray();
            return Results.Json(new { apiVersion = "v1", items,
                nextCursor = rows.Count > count ? McpSkillCursor.Encode(scope, rows[count - 1].Path) : null });
        });

        app.MapPut("/feeds/{feedId}/raw/{name}/{version}", async (
            string feedId, string name, string version, Settings cfg,
            IPackageDatabase db, BlobStore blobs, ArtifactLocks locks,
            HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId, publish: true) is { } denied)
                return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            if (!Valid(name, version))
                return Results.BadRequest(new { error = "Invalid raw artifact coordinates" });
            if (ctx.Request.ContentLength is null)
                return Results.StatusCode(StatusCodes.Status411LengthRequired);
            if (ctx.Request.ContentLength <= 0)
                return Results.BadRequest(new { error = "Empty raw artifact" });
            if (ctx.Request.ContentLength > cfg.MaxArtifactBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var mediaType = ctx.Request.ContentType ?? "application/octet-stream";
            if (!RawEndpoints.ValidMediaType(mediaType))
                return Results.BadRequest(new { error = "Invalid raw media type" });

            var path = FeedAssetEntry.RawPath(name, version);
            var key = BlobKey(feedId, name, version);
            // One feed lock covers quota check, blob persistence and ledger commit.
            // Do not nest striped artifact locks: hash collisions could deadlock.
            using var lease = await locks.AcquireAsync("feed-raw-quota", feedId,
                "", ctx.RequestAborted);
            if (db.GetFeedAsset(feedId, "raw", path) is not null || blobs.Contains(key))
                return Results.Conflict(new { error = "Immutable private raw artifact exists" });

            if ((cfg.MaxRawFeedBytes != 0 || cfg.MaxRawFeedArtifacts != 0) &&
                !RawFeedQuota.Admit(db.RawFeedUsage(feedId), ctx.Request.ContentLength.Value, cfg))
                return Results.Json(new { error = "Private Raw feed quota exceeded" }, statusCode: 507);

            db.BeginPublication(key, "feed-raw", feedId + "|" + name, version);
            try
            {
                if (!await blobs.PutAsync(key, ctx.Request.Body,
                    cfg.MaxArtifactBytes, ctx.RequestAborted))
                    return Results.Conflict(new { error = "Immutable private raw blob collision" });
                var stored = blobs.Existing(key) ??
                    throw new InvalidDataException("Private raw blob not available after persistence.");
                var size = new FileInfo(stored).Length;
                if (size != ctx.Request.ContentLength.Value)
                    return Results.BadRequest(new { error = "Raw body length differs from Content-Length" });
                if (size <= 0 || size > cfg.MaxArtifactBytes)
                    throw new InvalidDataException("Invalid private raw blob length.");
                await using var stream = new FileStream(stored, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var sha = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(stream, ctx.RequestAborted));
                var revision = new FeedAssetEntry(feedId, "raw", path, name,
                    version, mediaType, sha, size, DateTimeOffset.UtcNow.ToString("O"));
                if (!db.PublishFeedAsset(revision, Access.Actor(ctx)))
                    return Results.Conflict(new { error = "Private raw publication conflict" });
                db.CompletePublication(key);
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { feedId, name, version,
                    mediaType, sha256 = sha, size,
                    url = cfg.Base + "/feeds/" + feedId + "/raw/" +
                        Uri.EscapeDataString(name) + "/" + Uri.EscapeDataString(version) },
                    statusCode: StatusCodes.Status201Created);
            }
            catch (PayloadTooLargeException)
            { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            catch (StorageQuotaExceededException)
            { return Results.StatusCode(507); }
            // Keep the journal on unexpected cross-store failures for repair.
        }).RequireRateLimiting("publish");

        app.MapGet("/feeds/{feedId}/raw/{name}/{version}/metadata", (
            string feedId, string name, string version,
            Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            if (!Valid(name, version)) return Results.BadRequest();
            var asset = db.GetFeedAsset(feedId, "raw", FeedAssetEntry.RawPath(name, version));
            if (asset is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { asset.FeedId, asset.Name, asset.Version,
                asset.MediaType, asset.Sha256, asset.Size, asset.CreatedAt });
        });

        app.MapMethods("/feeds/{feedId}/raw/{name}/{version}", ["GET", "HEAD"], async (
            string feedId, string name, string version,
            Settings cfg, IPackageDatabase db, BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            if (!Valid(name, version)) return Results.BadRequest();
            var asset = db.GetFeedAsset(feedId, "raw", FeedAssetEntry.RawPath(name, version));
            if (asset is null) return Results.NotFound();
            if (!FeedAssetEntry.Valid(asset) || asset.Size > cfg.MaxArtifactBytes)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var key = BlobKey(feedId, name, version);
            if (!await ArtifactDigests.MatchesRemoteAsync(blobs, key, "sha256",
                Convert.FromHexString(asset.Sha256), asset.Size, cfg.MaxArtifactBytes, ctx.RequestAborted))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var stored = blobs.Existing(key);
            if (stored is null || !await ArtifactDigests.MatchesSha256Async(
                stored, asset.Sha256, asset.Size, ctx.RequestAborted))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers.ContentDisposition =
                $"attachment; filename=\"{name}-{version}.bin\"";
            ctx.Response.Headers.CacheControl = "private, no-store";
            var etag = "\"" + asset.Sha256 + "\"";
            ctx.Response.Headers.ETag = etag;
            if (ctx.Request.Headers.IfNoneMatch.ToString() == etag)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.File(stored, asset.MediaType, enableRangeProcessing: true);
        });
    }
}
