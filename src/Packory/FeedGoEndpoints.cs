using System.Security.Cryptography;
using System.Text;

namespace Packory;

/// <summary>
/// Hosted private GOPROXY with immutable one-object triplets per feed.
/// The global Go module route is never queried or used as an upstream.
/// </summary>
public static class FeedGoEndpoints
{
    private static bool Active(IPackageDatabase db, string feedId) =>
        db.Feed(feedId) is
        { Kind: "go", Mode: "hosted", Visibility: "private", Enabled: true };

    public static void Map(WebApplication app)
    {
        app.MapPost("/feeds/{feedId}/api/go/modules", async (
            string feedId, Settings cfg, IPackageDatabase db,
            BlobStore blobs, ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId, publish: true) is { } denied)
                return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            if (!ctx.Request.HasFormContentType)
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            if (ctx.Request.ContentLength is null or <= 0)
                return Results.StatusCode(StatusCodes.Status411LengthRequired);
            if (ctx.Request.ContentLength > GoModuleBundle.MaxBundleBytes + 1048576 ||
                ctx.Request.ContentLength > cfg.MaxArtifactBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            IFormCollection form;
            try { form = await ctx.Request.ReadFormAsync(ctx.RequestAborted); }
            catch (InvalidDataException)
            { return Results.BadRequest(new { error = "Invalid Go multipart payload" }); }
            if (form["module"].Count != 1 || form["version"].Count != 1)
                return Results.BadRequest(new { error = "Require module and version" });
            var module = form["module"].ToString();
            var version = form["version"].ToString();
            if (!GoModuleBundle.ValidCoordinates(module, version))
                return Results.BadRequest(new { error = "Invalid Go module coordinates" });

            var archive = form.Files.GetFile("zip");
            var modFile = form.Files.GetFile("mod");
            if (archive is null || modFile is null || form.Files.Count != 2 ||
                archive.Length is <= 0 or > GoModuleZipInspector.MaxArchiveBytes ||
                modFile.Length is <= 0 or > GoModuleBundle.MaxModBytes)
                return Results.BadRequest(new { error = "Invalid or missing Go module files" });

            DateTimeOffset timestamp;
            if (form["time"].Count == 0) timestamp = DateTimeOffset.UtcNow;
            else if (form["time"].Count != 1 ||
                !DateTimeOffset.TryParse(form["time"].ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out timestamp))
                return Results.BadRequest(new { error = "Invalid Go timestamp" });

            byte[] zip;
            byte[] mod;
            try
            {
                await using (var stream = archive.OpenReadStream())
                    zip = await BlobStore.ReadBoundedAsync(stream,
                        GoModuleZipInspector.MaxArchiveBytes, ctx.RequestAborted);
                await using (var stream = modFile.OpenReadStream())
                    mod = await BlobStore.ReadBoundedAsync(stream,
                        GoModuleBundle.MaxModBytes, ctx.RequestAborted);
            }
            catch (PayloadTooLargeException)
            { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }

            GoModuleBundle.Package package;
            try { package = GoModuleBundle.Create(module, version, mod, zip, timestamp); }
            catch (ArgumentException)
            { return Results.BadRequest(new { error = "Invalid Go module identity" }); }
            catch (InvalidDataException)
            { return Results.BadRequest(new { error = "Invalid Go module archive" }); }
            var asset = FeedGoBundle.Entry(feedId, module, version, package);
            var key = FeedAssetEntry.BlobKey(asset);
            using var gate = await locks.AcquireAsync("feed-go",
                feedId + "/" + module, version, ctx.RequestAborted);
            if (db.GetFeedAsset(feedId, "go", asset.Path) is not null ||
                blobs.Contains(key))
                return Results.Conflict(new { error = "Immutable private Go revision exists" });

            db.BeginPublication(key, "feed-go", feedId + "|" + asset.Path, version);
            try
            {
                using var source = new MemoryStream(package.Bundle, writable: false);
                if (!await blobs.PutAsync(key, source, GoModuleBundle.MaxBundleBytes,
                    ctx.RequestAborted))
                    return Results.Conflict(new { error = "Private Go bundle collision" });
                var stored = blobs.Existing(key);
                if (stored is null || !await ArtifactDigests.MatchesSha256Async(
                    stored, asset.Sha256, asset.Size, ctx.RequestAborted))
                    throw new InvalidDataException("Private Go bundle verification failed");
                if (!db.PublishFeedAsset(asset, Access.Actor(ctx)))
                    return Results.Conflict(new { error = "Private Go revision conflict" });
                db.CompletePublication(key);
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Json(new
                {
                    module, version, zipSha256 = package.ZipSha256,
                    zipH1 = package.ZipH1, bundleSha256 = package.BundleSha256,
                    proxy = cfg.Base + "/feeds/" + feedId + "/go"
                }, statusCode: StatusCodes.Status201Created);
            }
            catch (PayloadTooLargeException)
            { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            catch (StorageQuotaExceededException)
            { return Results.StatusCode(507); }
        }).RequireRateLimiting("publish");

        app.MapMethods("/feeds/{feedId}/go/{**path}", ["GET", "HEAD"], async (
            string feedId, string? path, Settings cfg, IPackageDatabase db,
            BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (path is null || path.Length > 1024 || path.Contains('%') ||
                path.Contains('\\') || path.Contains('?') || path.Contains('#'))
                return Results.BadRequest(new { error = "Invalid Go proxy path" });
            if (path.EndsWith("/@latest", StringComparison.Ordinal))
            {
                var escaped = path[..^"/@latest".Length];
                if (!GoProxyCoordinates.TryDecodeModulePath(escaped, out var name))
                    return Results.BadRequest();
                var versions = db.FeedGoVersions(feedId, name);
                if (versions.Count == 0) return Results.NotFound();
                var stable = versions.Where(v => !v.Contains('-', StringComparison.Ordinal))
                    .ToArray();
                var latest = (stable.Length > 0 ? stable : versions)
                    .OrderBy(x => x, Comparer<string>.Create(
                        GoModuleBundle.CompareVersions)).Last();
                return await Part(feedId, name, latest, "info", db, blobs, ctx);
            }
            if (!GoProxyCoordinates.TryParseRequest(path, out var request) ||
                request is null)
                return Results.BadRequest(new { error = "Invalid Go coordinate" });
            if (request.Operation == "list")
            {
                var versions = db.FeedGoVersions(feedId, request.ModulePath)
                    .OrderBy(x => x, Comparer<string>.Create(
                        GoModuleBundle.CompareVersions)).ToArray();
                return versions.Length == 0
                    ? Results.NotFound()
                    : Results.Text(string.Join("\n", versions) + "\n",
                        "text/plain; charset=utf-8", Encoding.UTF8);
            }
            return await Part(feedId, request.ModulePath, request.Version!,
                request.Operation, db, blobs, ctx);
        });
    }

    private static async Task<IResult> Part(string feedId, string module,
        string version, string operation, IPackageDatabase db, BlobStore blobs,
        HttpContext ctx)
    {
        var path = FeedAssetEntry.GoPath(module, version);
        var asset = db.GetFeedAsset(feedId, "go", path);
        if (asset is null) return Results.NotFound();
        if (!FeedAssetEntry.Valid(asset))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var stored = blobs.Existing(FeedAssetEntry.BlobKey(asset));
        if (stored is null || !await ArtifactDigests.MatchesSha256Async(
                stored, asset.Sha256, asset.Size, ctx.RequestAborted))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        try
        {
            var bytes = await File.ReadAllBytesAsync(stored, ctx.RequestAborted);
            var revision = FeedGoBundle.Ledger(asset, bytes);
            var contents = GoModuleBundle.Extract(bytes, revision, operation);
            var etag = "\"" +
                Convert.ToHexStringLower(SHA256.HashData(contents)) + "\"";
            ctx.Response.Headers.ETag = etag;
            if (ctx.Request.Headers.IfNoneMatch.ToString() == etag)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(contents, operation switch
            {
                "zip" => "application/zip",
                "mod" => "text/plain; charset=utf-8",
                _ => "application/json"
            });
        }
        catch (InvalidDataException)
        { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
    }
}
