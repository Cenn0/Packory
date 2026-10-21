using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Packory;

/// <summary>
/// Private immutable Maven release transport backed by the shared V8 feed ledger.
/// Not a proxy, SNAPSHOT repository or complete native Maven deploy endpoint.
/// </summary>
public static class FeedMavenEndpoints
{
    private static bool Active(IPackageDatabase db, string feedId) =>
        db.Feed(feedId) is
        { Kind: "maven", Mode: "hosted", Visibility: "private", Enabled: true };

    private static string Key(string feedId, string path) =>
        "feeds/" + feedId + "/maven/" + path;

    private static bool TryChecksum(string path, out string target, out string? algorithm)
    {
        target = path;
        algorithm = null;
        foreach (var suffix in new[] { ".sha256", ".sha1", ".md5" })
        {
            if (!path.EndsWith(suffix, StringComparison.Ordinal)) continue;
            target = path[..^suffix.Length];
            algorithm = suffix[1..];
            break;
        }
        return MavenEndpoints.ValidAssetPath(target, out _);
    }

    private static bool TryMetadata(string path, out string group, out string artifact,
        out string? algorithm)
    {
        group = artifact = "";
        algorithm = null;
        if (path.Length > 1024 || path.Contains('%') || path.Contains('\\') ||
            path.StartsWith('/') || path.Any(char.IsControl))
            return false;
        var target = path;
        foreach (var suffix in new[] { ".sha256", ".sha1", ".md5" })
        {
            if (!target.EndsWith(suffix, StringComparison.Ordinal)) continue;
            algorithm = suffix[1..];
            target = target[..^suffix.Length];
            break;
        }
        const string metadata = "/maven-metadata.xml";
        if (!target.EndsWith(metadata, StringComparison.Ordinal)) return false;
        var segments = target[..^metadata.Length].Split('/');
        if (segments.Length is < 2 or > 19 ||
            segments.Any(x => !MavenCoordinates.ValidSegment(x)) ||
            segments[..^1].Any(x => x.Contains('.')))
            return false;
        group = string.Join('.', segments[..^1]);
        artifact = segments[^1];
        return MavenCoordinates.ValidGroup(group);
    }

    private static string HexDigest(byte[] bytes, string algorithm) => algorithm switch
    {
        "sha256" => Convert.ToHexStringLower(SHA256.HashData(bytes)),
        "sha1" => Convert.ToHexStringLower(SHA1.HashData(bytes)),
        "md5" => Convert.ToHexStringLower(MD5.HashData(bytes)),
        _ => throw new ArgumentException("Invalid Maven digest algorithm")
    };

    private static async Task<string> FileDigest(string path, string algorithm,
        CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = algorithm switch
        {
            "sha256" => await SHA256.HashDataAsync(stream, ct),
            "sha1" => await SHA1.HashDataAsync(stream, ct),
            "md5" => await MD5.HashDataAsync(stream, ct),
            _ => throw new ArgumentException("Invalid Maven digest algorithm")
        };
        return Convert.ToHexStringLower(digest);
    }

    private static async Task<IResult> VerifySidecar(string feedId, string path,
        string algorithm, Settings cfg, IPackageDatabase db, BlobStore blobs,
        HttpContext ctx)
    {
        if (ctx.Request.ContentLength is null)
            return Results.StatusCode(StatusCodes.Status411LengthRequired);
        if (ctx.Request.ContentLength <= 0)
            return Results.BadRequest(new { error = "Empty checksum" });
        if (ctx.Request.ContentLength > 130)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        byte[] contents;
        try { contents = await BlobStore.ReadBoundedAsync(ctx.Request.Body, 130, ctx.RequestAborted); }
        catch (PayloadTooLargeException)
        { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        var supplied = Encoding.ASCII.GetString(contents);
        if (supplied.EndsWith("\r\n", StringComparison.Ordinal)) supplied = supplied[..^2];
        else if (supplied.EndsWith("\n", StringComparison.Ordinal)) supplied = supplied[..^1];
        int length = algorithm switch { "sha256" => 64, "sha1" => 40, _ => 32 };
        if (supplied.Length != length || !supplied.All(Uri.IsHexDigit))
            return Results.BadRequest(new { error = "Invalid checksum" });

        var entry = db.GetFeedAsset(feedId, "maven", path);
        if (entry is null) return Results.NotFound();
        if (!FeedAssetEntry.Valid(entry) || entry.Size > cfg.MaxArtifactBytes)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (!await ArtifactDigests.MatchesRemoteAsync(blobs, Key(feedId, path), "sha256",
            Convert.FromHexString(entry.Sha256), entry.Size, cfg.MaxArtifactBytes, ctx.RequestAborted))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var stored = blobs.Existing(Key(feedId, path));
        if (stored is null || !await ArtifactDigests.MatchesSha256Async(
                stored, entry.Sha256, entry.Size, ctx.RequestAborted))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var expected = algorithm == "sha256" ? entry.Sha256 :
            await FileDigest(stored, algorithm, ctx.RequestAborted);
        return string.Equals(expected, supplied, StringComparison.OrdinalIgnoreCase)
            ? Results.NoContent()
            : Results.Conflict(new { error = "Checksum differs from immutable artifact" });
    }

    public static void Map(WebApplication app)
    {
        app.MapMethods("/feeds/{feedId}/maven/{**path}", ["GET", "HEAD"], async (
            string feedId, string path, Settings cfg, IPackageDatabase db,
            BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";

            if (TryMetadata(path, out var group, out var name, out var metadataDigest))
            {
                var versions = db.FeedMavenVersions(feedId, group, name);
                if (versions.Count == 0) return Results.NotFound();
                var xml = new XDocument(new XElement("metadata",
                    new XElement("groupId", group),
                    new XElement("artifactId", name),
                    new XElement("versioning", new XElement("versions",
                        versions.Select(x => new XElement("version", x))))));
                var contents = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
                return metadataDigest is null
                    ? Results.Bytes(contents, "application/xml")
                    : Results.Text(HexDigest(contents, metadataDigest), "text/plain");
            }

            if (!TryChecksum(path, out var assetPath, out var algorithm))
                return Results.BadRequest(new { error = "Invalid Maven release path" });
            var entry = db.GetFeedAsset(feedId, "maven", assetPath);
            if (entry is null) return Results.NotFound();
            if (!FeedAssetEntry.Valid(entry) || entry.Size > cfg.MaxArtifactBytes)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (!await ArtifactDigests.MatchesRemoteAsync(blobs, Key(feedId, assetPath), "sha256",
                Convert.FromHexString(entry.Sha256), entry.Size, cfg.MaxArtifactBytes, ctx.RequestAborted))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var stored = blobs.Existing(Key(feedId, assetPath));
            if (stored is null || !await ArtifactDigests.MatchesSha256Async(
                stored, entry.Sha256, entry.Size, ctx.RequestAborted))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (algorithm is not null)
                return Results.Text(algorithm == "sha256" ? entry.Sha256 :
                    await FileDigest(stored, algorithm, ctx.RequestAborted), "text/plain");

            ctx.Response.Headers.ETag = "\"" + entry.Sha256 + "\"";
            if (ctx.Request.Headers.IfNoneMatch.ToString() == "\"" + entry.Sha256 + "\"")
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.File(stored, entry.MediaType, enableRangeProcessing: true);
        });

        app.MapPut("/feeds/{feedId}/maven/{**path}", async (
            string feedId, string path, Settings cfg, IPackageDatabase db,
            BlobStore blobs, ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId, publish: true) is { } denied)
                return denied;
            if (!Active(db, feedId)) return Results.NotFound();

            if (!TryChecksum(path, out var assetPath, out var checksumAlgorithm) ||
                !MavenEndpoints.ValidAssetPath(assetPath, out var coordinates) ||
                coordinates is null)
                return Results.BadRequest(new { error = "Unsupported Maven release path" });
            if (checksumAlgorithm is not null)
                return await VerifySidecar(feedId, assetPath, checksumAlgorithm,
                    cfg, db, blobs, ctx);
            if (ctx.Request.ContentLength is null)
                return Results.StatusCode(StatusCodes.Status411LengthRequired);
            if (ctx.Request.ContentLength <= 0)
                return Results.BadRequest(new { error = "Empty artifact" });
            if (ctx.Request.ContentLength > cfg.MaxArtifactBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            byte[]? pomBytes = null;
            if (coordinates.Extension == "pom")
            {
                try
                {
                    pomBytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body,
                        Math.Min(cfg.MaxMetadataBytes,
                            (int)Math.Min(cfg.MaxArtifactBytes, int.MaxValue)),
                        ctx.RequestAborted);
                }
                catch (PayloadTooLargeException)
                { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
                if (!MavenEndpoints.PomIdentityMatches(pomBytes, coordinates))
                    return Results.BadRequest(new { error = "POM identity differs from Maven coordinates" });
            }

            var key = Key(feedId, assetPath);
            using var gate = await locks.AcquireAsync("feed-maven",
                feedId + "/" + assetPath, coordinates.Version, ctx.RequestAborted);
            if (db.GetFeedAsset(feedId, "maven", assetPath) is not null ||
                blobs.Contains(key))
                return Results.Conflict(new { error = "Immutable Maven artifact exists" });

            db.BeginPublication(key, "feed-maven", feedId + "|" + assetPath,
                coordinates.Version);
            try
            {
                await using var buffered = pomBytes is null ? null :
                    new MemoryStream(pomBytes, writable: false);
                if (!await blobs.PutAsync(key, buffered ?? ctx.Request.Body,
                    cfg.MaxArtifactBytes, ctx.RequestAborted))
                    return Results.Conflict(new { error = "Immutable blob collision" });
                var stored = blobs.Existing(key) ??
                    throw new InvalidDataException("Maven blob missing after write");
                var size = new FileInfo(stored).Length;
                if (size <= 0 || size > cfg.MaxArtifactBytes)
                    throw new InvalidDataException("Invalid private Maven artifact length");
                await using var stream = new FileStream(stored, FileMode.Open,
                    FileAccess.Read, FileShare.Read, 81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var sha256 = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(stream, ctx.RequestAborted));
                var entry = new FeedAssetEntry(feedId, "maven", assetPath,
                    coordinates.GroupId + ":" + coordinates.ArtifactId,
                    coordinates.Version, MavenEndpoints.AssetMediaType(coordinates.Extension)!,
                    sha256, size, DateTimeOffset.UtcNow.ToString("O"));
                if (!db.PublishFeedAsset(entry, Access.Actor(ctx)))
                    return Results.Conflict(new { error = "Feed artifact conflict" });
                db.CompletePublication(key);
                return Results.StatusCode(StatusCodes.Status201Created);
            }
            catch (PayloadTooLargeException)
            { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            catch (StorageQuotaExceededException)
            { return Results.StatusCode(507); }
            // Unexpected cross-store failures leave the intent for reconciliation.
        }).RequireRateLimiting("publish");
    }
}
