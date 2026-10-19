using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Packory;

/// <summary>
/// V4 global hosted Raw artifact transport. Not a native package manager protocol.
/// Feed-local isolation, arbitrary upstream proxying and artifact deletion are intentionally absent.
/// </summary>
public static class RawEndpoints
{
    public static string Key(string name, string version) => $"raw/{name}/{version}/payload.blob";

    public static bool ValidName(string? name) =>
        name is { Length: >= 1 and <= 128 } &&
        Regex.IsMatch(name, @"\A[a-z0-9](?:[a-z0-9._-]{0,126}[a-z0-9])?\z",
            RegexOptions.CultureInvariant) &&
        !name.Contains("..", StringComparison.Ordinal);

    public static bool ValidVersion(string? version) =>
        Names.Version(version) && !version!.Contains("..", StringComparison.Ordinal);

    public static bool ValidMediaType(string? mediaType) =>
        mediaType is { Length: >= 3 and <= 100 } &&
        Regex.IsMatch(mediaType,
            @"\A[a-z0-9][a-z0-9!#$&^_.+-]*/[a-z0-9][a-z0-9!#$&^_.+-]*\z",
            RegexOptions.CultureInvariant);

    private static bool Valid(string name, string version) =>
        ValidName(name) && ValidVersion(version);

    public static void Map(WebApplication app)
    {
        app.MapPut("/raw/{name}/{version}", async (string name, string version,
            Settings cfg, IPackageDatabase db, BlobStore blobs, ArtifactLocks locks,
            HttpContext ctx) =>
        {
            if (Access.Require(ctx, cfg, write: true) is { } denied) return denied;
            if (!Valid(name, version)) return Results.BadRequest(new { error = "Invalid artifact coordinates" });
            if (Access.RequireNamespace(ctx, cfg, "raw", name) is { } namespaceDenied)
                return namespaceDenied;
            if (ctx.Request.ContentLength is 0 or < 0)
                return Results.BadRequest(new { error = "Empty artifact" });
            if (ctx.Request.ContentLength > cfg.MaxArtifactBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var mediaType = ctx.Request.ContentType ?? "application/octet-stream";
            if (!ValidMediaType(mediaType))
                return Results.BadRequest(new { error = "Media type must be a bare MIME type without parameters" });

            using var gate = await locks.AcquireAsync("raw", name, version, ctx.RequestAborted);
            var key = Key(name, version);
            if (db.GetRawArtifact(name, version) is not null || blobs.Contains(key))
                return Results.Conflict(new { error = "Immutable raw artifact exists" });

            // Persist the recovery intent BEFORE writing bytes. The publication transaction
            // atomically binds catalog metadata, digest and audit after the immutable write.
            db.BeginPublication(key, "raw", name, version);
            try
            {
                if (!await blobs.PutAsync(key, ctx.Request.Body, cfg.MaxArtifactBytes,
                    ctx.RequestAborted))
                    return Results.Conflict(new { error = "Immutable raw artifact collision" });
                var path = blobs.Existing(key) ??
                    throw new InvalidDataException("Raw artifact missing after storage acknowledgement");
                var length = new FileInfo(path).Length;
                if (length == 0) throw new InvalidDataException("Empty artifact is not accepted");
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ctx.RequestAborted));
                if (!db.PublishRaw(name, version, mediaType, hash, length, Access.Actor(ctx)))
                    return Results.Conflict(new { error = "Artifact version exists" });
                db.CompletePublication(key);
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Json(new
                {
                    name, version, mediaType, sha256 = hash, size = length,
                    url = cfg.Base + "/raw/" + Uri.EscapeDataString(name) +
                        "/" + Uri.EscapeDataString(version)
                }, statusCode: StatusCodes.Status201Created);
            }
            catch (PayloadTooLargeException)
            { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            catch (StorageQuotaExceededException)
            { return Results.StatusCode(507); }
            // Other failures retain the journal for recovery investigation.
        }).RequireRateLimiting("publish");

        app.MapGet("/raw/{name}/{version}/metadata", (string name, string version,
            Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.RequireGlobalRaw(ctx, cfg) is { } denied) return denied;
            if (!Valid(name, version)) return Results.BadRequest();
            var artifact = db.GetRawArtifact(name, version);
            if (artifact is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new
            {
                artifact.Name, artifact.Version, artifact.MediaType,
                artifact.Sha256, artifact.Size, artifact.CreatedAt
            });
        });

        app.MapMethods("/raw/{name}/{version}", ["GET", "HEAD"], async (
            string name, string version, Settings cfg, IPackageDatabase db,
            BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.RequireGlobalRaw(ctx, cfg) is { } denied) return denied;
            if (!Valid(name, version)) return Results.BadRequest();
            var artifact = db.GetRawArtifact(name, version);
            if (artifact is null) return Results.NotFound();
            if (!ArtifactDigests.ValidSha256(artifact.Sha256) ||
                artifact.Size is <= 0 || artifact.Size > cfg.MaxArtifactBytes ||
                !ValidMediaType(artifact.MediaType))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            var path = blobs.Existing(Key(name, version));
            if (path is null || !await ArtifactDigests.MatchesSha256Async(
                path, artifact.Sha256, artifact.Size, ctx.RequestAborted))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            // Force download and nosniff for untrusted content; never serve inline HTML/JS.
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers.ContentDisposition =
                $"attachment; filename=\"{name}-{version}.bin\"";
            ctx.Response.Headers.CacheControl = "private, no-store";
            var etag = "\"" + artifact.Sha256 + "\"";
            ctx.Response.Headers.ETag = etag;
            if (ctx.Request.Headers.IfNoneMatch.ToString() == etag)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.File(path, artifact.MediaType, enableRangeProcessing: true);
        });
    }
}
