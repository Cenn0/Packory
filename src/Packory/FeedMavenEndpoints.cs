using System.Security.Cryptography;
using System.Text;

namespace Packory;

/// <summary>
/// Immutable private Maven release-file transport sharing E0's feed asset
/// ledger. It intentionally does not implement snapshot, mutable Maven
/// metadata PUT, native deploy lifecycle or upstream proxy behavior.
/// </summary>
public static class FeedMavenEndpoints
{
    private static bool Active(IPackageDatabase db, string feedId) =>
        db.Feed(feedId) is
        { Kind: "maven", Mode: "hosted", Visibility: "private", Enabled: true };

    private static bool TryChecksumAsset(string? path, out string assetPath,
        out string algorithm, out MavenCoordinates.Artifact? coordinate)
    {
        assetPath = algorithm = "";
        coordinate = null;
        if (path is null) return false;
        foreach (var suffix in new[] { ".sha256", ".sha1", ".md5" })
        {
            if (!path.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var stripped = path[..^suffix.Length];
            if (!MavenEndpoints.ValidAssetPath(stripped, out coordinate)) return false;
            assetPath = stripped;
            algorithm = suffix[1..];
            return true;
        }
        return false;
    }

    private static async Task<IResult> VerifyChecksumUpload(string feedId,
        string assetPath, string algorithm, Settings cfg, IPackageDatabase db,
        BlobStore blobs, HttpContext ctx)
    {
        if (ctx.Request.ContentLength is null)
            return Results.StatusCode(StatusCodes.Status411LengthRequired);
        if (ctx.Request.ContentLength <= 0)
            return Results.BadRequest(new { error = "Empty checksum" });
        if (ctx.Request.ContentLength > 130)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        byte[] bytes;
        try { bytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body, 130, ctx.RequestAborted); }
        catch (PayloadTooLargeException)
        { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        var hex = Encoding.ASCII.GetString(bytes);
        if (hex.EndsWith("\r\n", StringComparison.Ordinal)) hex = hex[..^2];
        else if (hex.EndsWith("\n", StringComparison.Ordinal)) hex = hex[..^1];
        var required = algorithm switch { "sha256" => 64, "sha1" => 40, _ => 32 };
        if (hex.Length != required || !hex.All(Uri.IsHexDigit))
            return Results.BadRequest(new { error = "Invalid checksum format" });
        var asset = db.GetFeedAsset(feedId, "maven", assetPath);
        if (asset is null) return Results.NotFound();
        if (!FeedAssetEntry.Valid(asset) || asset.Size > cfg.MaxArtifactBytes)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var local = blobs.Existing(FeedAssetEntry.BlobKey(asset));
        if (local is null || !await ArtifactDigests.MatchesSha256Async(
            local, asset.Sha256, asset.Size, ctx.RequestAborted))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var authoritative = algorithm == "sha256" ? asset.Sha256 :
            await MavenEndpoints.ChecksumAsync(local, algorithm, ctx.RequestAborted);
        return string.Equals(hex, authoritative, StringComparison.OrdinalIgnoreCase)
            ? Results.NoContent()
            : Results.Conflict(new { error = "Checksum does not match committed Maven bytes" });
    }

    public static void Map(WebApplication app)
    {
        app.MapMethods("/feeds/{feedId}/maven/{**path}", ["GET", "HEAD"], async (
            string feedId, string? path, Settings cfg, IPackageDatabase db,
            BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (path is null) return Results.NotFound();

            if (MavenEndpoints.TryMetadataCoordinates(path, out var group,
                out var name, out var metadataChecksum))
            {
                var versions = db.FeedAssetVersions(feedId, "maven", group + ":" + name);
                if (versions.Count == 0) return Results.NotFound();
                var content = Encoding.UTF8.GetBytes(MavenEndpoints.MetadataXml(group, name, versions));
                if (metadataChecksum is not null)
                {
                    var hex = metadataChecksum switch
                    {
                        ".sha256" => Convert.ToHexStringLower(SHA256.HashData(content)),
                        ".sha1" => Convert.ToHexStringLower(SHA1.HashData(content)),
                        _ => Convert.ToHexStringLower(MD5.HashData(content))
                    };
                    return Results.Text(hex, "text/plain");
                }
                return Results.Bytes(content, "application/xml");
            }

            var algorithm = path.EndsWith(".sha256", StringComparison.Ordinal) ? "sha256" :
                path.EndsWith(".sha1", StringComparison.Ordinal) ? "sha1" :
                path.EndsWith(".md5", StringComparison.Ordinal) ? "md5" : null;
            var artifactPath = algorithm is null ? path : path[..^(algorithm.Length + 1)];
            if (!MavenEndpoints.ValidAssetPath(artifactPath, out _))
                return Results.BadRequest(new { error = "Invalid canonical Maven path" });
            var asset = db.GetFeedAsset(feedId, "maven", artifactPath);
            if (asset is null) return Results.NotFound();
            if (!FeedAssetEntry.Valid(asset) || asset.Size > cfg.MaxArtifactBytes)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var local = blobs.Existing(FeedAssetEntry.BlobKey(asset));
            if (local is null || !await ArtifactDigests.MatchesSha256Async(
                local, asset.Sha256, asset.Size, ctx.RequestAborted))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (algorithm is not null)
            {
                var hex = algorithm == "sha256" ? asset.Sha256 :
                    await MavenEndpoints.ChecksumAsync(local, algorithm, ctx.RequestAborted);
                return Results.Text(hex, "text/plain");
            }
            var tag = "\"" + asset.Sha256 + "\"";
            ctx.Response.Headers.ETag = tag;
            if (ctx.Request.Headers.IfNoneMatch.ToString() == tag)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.File(local, asset.MediaType, enableRangeProcessing: true);
        });

        app.MapPut("/feeds/{feedId}/maven/{**path}", async (
            string feedId, string? path, Settings cfg, IPackageDatabase db,
            BlobStore blobs, ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId, publish: true) is { } denied)
                return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            if (path is null) return Results.BadRequest();
            if (TryChecksumAsset(path, out var checksumAssetPath,
                out var checksumAlgorithm, out _))
                return await VerifyChecksumUpload(feedId, checksumAssetPath,
                    checksumAlgorithm, cfg, db, blobs, ctx);
            if (!MavenEndpoints.ValidAssetPath(path, out var coordinate) ||
                coordinate is null)
                return Results.BadRequest(new { error = "Unsupported Maven release path" });

            if (ctx.Request.ContentLength is null)
                return Results.StatusCode(StatusCodes.Status411LengthRequired);
            if (ctx.Request.ContentLength <= 0)
                return Results.BadRequest(new { error = "Empty Maven artifact" });
            if (ctx.Request.ContentLength > cfg.MaxArtifactBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            byte[]? pom = null;
            if (coordinate.Extension == "pom")
            {
                try
                {
                    pom = await BlobStore.ReadBoundedAsync(ctx.Request.Body,
                        Math.Min(cfg.MaxMetadataBytes, (int)Math.Min(cfg.MaxArtifactBytes, int.MaxValue)),
                        ctx.RequestAborted);
                }
                catch (PayloadTooLargeException)
                { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
                if (!MavenEndpoints.PomIdentityMatches(pom, coordinate))
                    return Results.BadRequest(new { error = "POM does not match Maven coordinates" });
            }

            using var lease = await locks.AcquireAsync("feed-maven", feedId + "/" + path,
                "", ctx.RequestAborted);
            var key = $"feeds/{feedId}/maven/{path}";
            if (db.GetFeedAsset(feedId, "maven", path) is not null || blobs.Contains(key))
                return Results.Conflict(new { error = "Immutable Maven release path exists" });

            db.BeginPublication(key, "feed-maven", feedId + "|" + path, coordinate.Version);
            try
            {
                await using var buffered = pom is null ? null : new MemoryStream(pom, writable: false);
                if (!await blobs.PutAsync(key, buffered ?? ctx.Request.Body,
                    cfg.MaxArtifactBytes, ctx.RequestAborted))
                    return Results.Conflict(new { error = "Immutable Maven blob collision" });
                var local = blobs.Existing(key) ??
                    throw new InvalidDataException("Maven file missing after storage acknowledgement.");
                var size = new FileInfo(local).Length;
                if (size <= 0 || size > cfg.MaxArtifactBytes)
                    throw new InvalidDataException("Invalid stored Maven file length.");
                await using var input = new FileStream(local, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var digest = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(input, ctx.RequestAborted));
                var asset = new FeedAssetEntry(feedId, "maven", path,
                    coordinate.GroupId + ":" + coordinate.ArtifactId,
                    coordinate.Version, MavenEndpoints.AssetMediaType(coordinate.Extension)!,
                    digest, size, DateTimeOffset.UtcNow.ToString("O"));
                if (!db.PublishFeedAsset(asset, Access.Actor(ctx)))
                    return Results.Conflict(new { error = "Private Maven metadata conflict" });
                db.CompletePublication(key);
                return Results.StatusCode(StatusCodes.Status201Created);
            }
            catch (PayloadTooLargeException)
            { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            catch (StorageQuotaExceededException)
            { return Results.StatusCode(507); }
        }).RequireRateLimiting("publish");
    }
}
