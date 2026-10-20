using System.Security.Cryptography;
using System.Text;

namespace Packory;

/// <summary>
/// Hosted Go module proxy, using one immutable ZIP bundle per published triplet.
/// Publishing is a Packory-specific authenticated multipart API; Go clients consume
/// the standard read-only GOPROXY endpoints. No outbound proxy or sumdb endpoint.
/// </summary>
public static class GoEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/go/modules", async (Settings cfg, IPackageDatabase db,
            BlobStore blobs, ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.Require(ctx, cfg, write: true) is { } denied) return denied;
            if (!ctx.Request.HasFormContentType)
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            if (ctx.Request.ContentLength is null or <= 0)
                return Results.StatusCode(StatusCodes.Status411LengthRequired);
            if (ctx.Request.ContentLength > GoModuleBundle.MaxBundleBytes + 1048576 ||
                ctx.Request.ContentLength > cfg.MaxArtifactBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            IFormCollection form;
            try { form = await ctx.Request.ReadFormAsync(ctx.RequestAborted); }
            catch (InvalidDataException) { return Results.BadRequest(new { error = "Invalid multipart body" }); }

            if (form["module"].Count != 1 || form["version"].Count != 1)
                return Results.BadRequest(new { error = "Require one module and one version" });
            var name = form["module"].ToString();
            var version = form["version"].ToString();
            if (!GoModuleBundle.ValidCoordinates(name, version))
                return Results.BadRequest(new { error = "Invalid canonical Go coordinates" });
            if (Access.RequireNamespace(ctx, cfg, "go", name) is { } scopeDenied)
                return scopeDenied;
            var zipFile = form.Files.GetFile("zip");
            var modFile = form.Files.GetFile("mod");
            if (zipFile is null || modFile is null || form.Files.Count != 2 ||
                zipFile.Length is <= 0 or > GoModuleZipInspector.MaxArchiveBytes ||
                modFile.Length is <= 0 or > GoModuleBundle.MaxModBytes)
                return Results.BadRequest(new { error = "Require bounded zip and mod uploads" });

            DateTimeOffset timestamp;
            if (form["time"].Count == 0) timestamp = DateTimeOffset.UtcNow;
            else if (form["time"].Count != 1 ||
                !DateTimeOffset.TryParse(form["time"].ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out timestamp))
                return Results.BadRequest(new { error = "Invalid RFC3339 timestamp" });

            byte[] zip;
            byte[] mod;
            await using (var input = zipFile.OpenReadStream())
                zip = await BlobStore.ReadBoundedAsync(input,
                    GoModuleZipInspector.MaxArchiveBytes, ctx.RequestAborted);
            await using (var input = modFile.OpenReadStream())
                mod = await BlobStore.ReadBoundedAsync(input,
                    GoModuleBundle.MaxModBytes, ctx.RequestAborted);

            GoModuleBundle.Package package;
            try { package = GoModuleBundle.Create(name, version, mod, zip, timestamp); }
            catch (ArgumentException)
            { return Results.BadRequest(new { error = "Invalid module coordinates" }); }
            catch (InvalidDataException)
            { return Results.BadRequest(new { error = "Invalid Go module archive or go.mod" }); }
            var key = GoModuleBundle.Key(name, version);
            using var gate = await locks.AcquireAsync("go", name, version, ctx.RequestAborted);
            if (db.GetGoModule(name, version) is not null || blobs.Contains(key))
                return Results.Conflict(new { error = "Immutable Go module version exists" });

            // Crash-safe publication: only the one committed database row exposes
            // the entire info/mod/zip triplet. An orphan bundle retains its journal.
            db.BeginPublication(key, "go", name, version);
            using var content = new MemoryStream(package.Bundle, writable: false);
            if (!await blobs.PutAsync(key, content, GoModuleBundle.MaxBundleBytes,
                ctx.RequestAborted))
                return Results.Conflict(new { error = "Immutable Go bundle already exists" });
            var physical = blobs.Existing(key);
            if (physical is null || !await ArtifactDigests.MatchesSha256Async(
                physical, package.BundleSha256, package.Bundle.LongLength,
                ctx.RequestAborted))
                throw new InvalidDataException("Go bundle was not durably verified.");
            var revision = new GoModuleRevision(name, version, package.BundleSha256,
                package.Bundle.LongLength, package.ZipSha256, package.ZipSize,
                package.ModSha256, package.ModSize, package.PublishedAt);
            if (!db.PublishGoModule(revision, Access.Actor(ctx)))
                return Results.Conflict(new { error = "Immutable Go version already published" });
            db.CompletePublication(key);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { module = name, version,
                zipSha256 = revision.ZipSha256, zipH1 = package.ZipH1,
                zipSize = revision.ZipSize,
                bundleSha256 = revision.BundleSha256,
                proxy = cfg.Base + "/go" },
                statusCode: StatusCodes.Status201Created);
        }).RequireRateLimiting("publish");

        app.MapMethods("/go/{**path}", ["GET", "HEAD"], async (
            string? path, Settings cfg, IPackageDatabase db, BlobStore blobs,
            HttpContext ctx) =>
        {
            if (Access.RequireGlobalRaw(ctx, cfg) is { } denied) return denied;
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
                var versions = db.GoModuleVersions(name);
                if (versions.Count == 0) return Results.NotFound();
                // Go prefers the highest stable release, then the highest
                // prerelease. A later prerelease must not displace a release.
                // See https://go.dev/ref/mod#module-proxy-protocol
                var releases = versions.Where(v => !v.Contains('-', StringComparison.Ordinal))
                    .ToArray();
                var latest = (releases.Length > 0 ? releases : versions)
                    .OrderBy(x => x, Comparer<string>.Create(
                        GoModuleBundle.CompareVersions)).Last();
                return await SendPart(db, blobs, ctx, name, latest, "info");
            }

            if (!GoProxyCoordinates.TryParseRequest(path, out var request) ||
                request is null)
                return Results.BadRequest(new { error = "Invalid canonical GOPROXY coordinate" });
            if (request.Operation == "list")
            {
                var versions = db.GoModuleVersions(request.ModulePath)
                    .OrderBy(x => x, Comparer<string>.Create(
                        GoModuleBundle.CompareVersions)).ToArray();
                if (versions.Length == 0) return Results.NotFound();
                return Results.Text(string.Join("\n", versions) + "\n", "text/plain; charset=utf-8",
                    Encoding.UTF8);
            }
            return await SendPart(db, blobs, ctx, request.ModulePath,
                request.Version!, request.Operation);
        });
    }

    private static async Task<IResult> SendPart(IPackageDatabase db, BlobStore blobs,
        HttpContext ctx, string name, string version, string part)
    {
        var revision = db.GetGoModule(name, version);
        if (revision is null) return Results.NotFound();
        if (!GoModuleBundle.ValidRecord(revision))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        var physical = blobs.Existing(GoModuleBundle.Key(name, version));
        if (physical is null || !await ArtifactDigests.MatchesSha256Async(
            physical, revision.BundleSha256, revision.BundleSize, ctx.RequestAborted))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        try
        {
            var bytes = await File.ReadAllBytesAsync(physical, ctx.RequestAborted);
            var data = GoModuleBundle.Extract(bytes, revision, part);
            var digest = Convert.ToHexStringLower(SHA256.HashData(data));
            var etag = "\"" + digest + "\"";
            ctx.Response.Headers.ETag = etag;
            if (ctx.Request.Headers.IfNoneMatch.ToString() == etag)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(data, part switch
            {
                "zip" => "application/zip",
                "mod" => "text/plain; charset=utf-8",
                _ => "application/json"
            });
        }
        catch (InvalidDataException)
        {
            // Never serve bytes when the immutable bundle no longer matches the ledger.
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
