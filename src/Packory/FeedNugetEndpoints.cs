using System.Security.Cryptography;
using System.Xml;

namespace Packory;

/// <summary>
/// First isolated hosted feed adapter. No upstream proxy, no global namespace
/// fallback, and no appearance in the legacy search/catalog/registry.
/// The only supported mode is an explicitly activated private NuGet feed.
/// </summary>
public static class FeedNugetEndpoints
{
    public static string BlobKey(string feedId, string name, string version) =>
        $"feeds/{feedId}/nuget/{name}/{version}/{name}.{version}.nupkg";

    private static bool Active(IPackageDatabase db, string feedId) =>
        db.Feed(feedId) is
        { Kind: "nuget", Mode: "hosted", Visibility: "private", Enabled: true };

    public static void Map(WebApplication app)
    {
        FeedNugetDiscovery.Map(app);
        app.MapGet("/feeds/{feedId}/nuget/v3/index.json", (
            string feedId, Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-store";
            var root = cfg.Base + "/feeds/" + feedId + "/nuget/v3/";
            return Results.Json(new
            {
                version = "3.0.0",
                resources = new object[]
                {
                    new Dictionary<string,string>
                    {
                        ["@id"] = root + "flatcontainer/",
                        ["@type"] = "PackageBaseAddress/3.0.0"
                    },
                    new Dictionary<string,string>
                    {
                        ["@id"] = root + "registration/",
                        ["@type"] = "RegistrationsBaseUrl/3.6.0"
                    },
                    new Dictionary<string,string>
                    {
                        ["@id"] = root + "search",
                        ["@type"] = "SearchQueryService/3.5.0"
                    },
                    new Dictionary<string,string>
                    {
                        ["@id"] = root + "push",
                        ["@type"] = "PackagePublish/2.0.0"
                    }
                }
            });
        });

        app.MapGet("/feeds/{feedId}/nuget/v3/flatcontainer/{id}/index.json", (
            string feedId, string id, Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            id = id.ToLowerInvariant();
            if (!Names.Nuget(id)) return Results.BadRequest();
            if (cfg.DeniesNuget(id)) return Results.StatusCode(403);
            var versions = db.FeedNugetVersions(feedId, id);
            ctx.Response.Headers.CacheControl = "no-store";
            return versions.Count == 0 ? Results.NotFound() :
                Results.Json(new { versions });
        });

        app.MapGet("/feeds/{feedId}/nuget/v3/flatcontainer/{id}/{version}/{file}", async (
            string feedId, string id, string version, string file, Settings cfg,
            IPackageDatabase db, BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            id = id.ToLowerInvariant();
            version = version.ToLowerInvariant();
            if (!Names.Nuget(id) || !Names.Version(version)) return Results.BadRequest();
            if (cfg.DeniesNuget(id)) return Results.StatusCode(403);
            var prefix = id + "." + version;
            if (!string.Equals(file, prefix + ".nupkg", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(file, prefix + ".nupkg.sha512", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(file, id + ".nuspec", StringComparison.OrdinalIgnoreCase))
                return Results.NotFound();
            var revision = db.GetFeedNugetRevision(feedId, id, version);
            if (revision is null) return Results.NotFound();
            if (!NugetDigest.Valid(revision.Sha512) || revision.Size < 0)
                throw new InvalidDataException("Private feed has invalid immutable checksum metadata.");
            var key = BlobKey(feedId, id, version);
            if (!await ArtifactDigests.MatchesRemoteAsync(blobs, key, "sha512",
                Convert.FromBase64String(revision.Sha512), revision.Size, cfg.MaxArtifactBytes, ctx.RequestAborted))
                throw new InvalidDataException("Remote private feed package integrity mismatch.");
            var path = blobs.Existing(key) ??
                throw new InvalidDataException("Private feed package bytes are missing.");
            if (!await NugetDigest.MatchesAsync(path,
                new NugetDigest(revision.Sha512, revision.Size, "hosted"), ctx.RequestAborted))
                throw new InvalidDataException("Private feed package integrity mismatch.");
            ctx.Response.Headers.CacheControl = "no-store";
            if (file.EndsWith(".sha512", StringComparison.OrdinalIgnoreCase))
                return Results.Text(revision.Sha512, "text/plain");
            if (file.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                return Results.Bytes(NuGetEndpoints.ReadNuspec(path).Xml, "application/xml");
            return Results.File(path, "application/octet-stream", enableRangeProcessing: true);
        });

        app.MapMethods("/feeds/{feedId}/nuget/v3/push", ["PUT", "POST"], async (
            string feedId, Settings cfg, IPackageDatabase db,
            BlobStore blobs, ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId, publish: true) is { } denied)
                return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected multipart NuGet package" });
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            if (form.Files.Count != 1)
                return Results.BadRequest(new { error = "Expected exactly one package" });
            var package = form.Files[0];
            if (package.Length > cfg.MaxArtifactBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            await using var body = package.OpenReadStream();
            var bytes = await BlobStore.ReadBoundedAsync(body, cfg.MaxArtifactBytes,
                ctx.RequestAborted);
            NuGetEndpoints.Nuspec nuspec;
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                nuspec = NuGetEndpoints.ReadNuspec(stream);
            }
            catch (Exception error) when (error is InvalidDataException or XmlException
                or InvalidOperationException)
            { return Results.BadRequest(new { error = "Invalid NuGet package identity" }); }
            var name = nuspec.Id.ToLowerInvariant();
            var version = nuspec.Version.ToLowerInvariant();
            if (cfg.DeniesNuget(name)) return Results.StatusCode(403);
            using var gate = await locks.AcquireAsync("feed-nuget:" + feedId,
                name, version, ctx.RequestAborted);
            var key = BlobKey(feedId, name, version);
            if (db.GetFeedNugetRevision(feedId, name, version) is not null ||
                blobs.Existing(key) is not null)
                return Results.Conflict(new { error = "Immutable feed version exists" });
            // Keep the intent until both stores agree, exactly as for legacy
            // hosted publishing. Recovery resolves this feed-specific reference.
            db.BeginPublication(key, "feed-nuget", feedId + "|" + name, version);
            using var streamData = new MemoryStream(bytes, writable: false);
            if (!await blobs.PutAsync(key, streamData, cfg.MaxArtifactBytes,
                ctx.RequestAborted))
                return Results.Conflict(new { error = "Immutable blob collision" });
            if (!db.PublishFeedNuget(feedId, name, version, nuspec.Description,
                Convert.ToBase64String(SHA512.HashData(bytes)), bytes.LongLength,
                Access.Actor(ctx)))
                return Results.Conflict(new { error = "Feed was disabled or version exists" });
            db.CompletePublication(key);
            return Results.StatusCode(StatusCodes.Status201Created);
        }).RequireRateLimiting("publish");
    }
}
