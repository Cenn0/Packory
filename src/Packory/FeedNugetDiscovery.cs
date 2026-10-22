using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Packory;

/// <summary>
/// Feed-local NuGet V3 search and registration. All lookups are authorized
/// before touching package metadata, and no upstream/global fallback exists.
/// </summary>
public static class FeedNugetDiscovery
{
    private const int PageSize = 64;

    private static string Root(Settings settings, string feedId) =>
        settings.Base + "/feeds/" + feedId + "/nuget/v3/";

    private static IResult? Gate(string feedId, Settings cfg, IPackageDatabase db, HttpContext ctx)
    {
        if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
        if (db.Feed(feedId) is not
            { Kind: "nuget", Mode: "hosted", Visibility: "private", Enabled: true })
            return Results.NotFound();
        ctx.Response.Headers.CacheControl = "no-store";
        return null;
    }

    public static void Map(WebApplication app)
    {
        app.MapGet("/feeds/{feedId}/nuget/v3/search", (
            string feedId, string? q, int? skip, int? take,
            Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Gate(feedId, cfg, db, ctx) is { } denied) return denied;
            if ((q?.Length ?? 0) > 128 || skip is < 0 or > 10000 || take is < 1 or > 100)
                return Results.BadRequest(new { error = "Invalid NuGet search parameters" });
            var query = q?.Trim() ?? "";
            var groups = db.FeedNugetRevisions(feedId)
                .Where(x => !cfg.DeniesNuget(x.Name) &&
                    (x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     x.Description.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(x => x.Name)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToArray();
            var root = Root(cfg, feedId);
            var entries = groups.Skip(skip ?? 0).Take(take ?? 20).Select(group =>
            {
                var sorted = group.OrderByDescending(x => x.Version, StringComparer.Ordinal).ToArray();
                return new
                {
                    id = group.Key,
                    version = sorted[0].Version,
                    description = sorted[0].Description,
                    versions = sorted.Select(x => new
                    {
                        version = x.Version,
                        @id = root + "registration/" + group.Key + "/" + x.Version + ".json"
                    }).ToArray()
                };
            }).ToArray();
            return Results.Json(new { totalHits = groups.Length, data = entries });
        });

        app.MapGet("/feeds/{feedId}/nuget/v3/registration/{id}/index.json", (
            string feedId, string id, Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Gate(feedId, cfg, db, ctx) is { } denied) return denied;
            id = id.ToLowerInvariant();
            if (!Names.Nuget(id)) return Results.BadRequest();
            if (cfg.DeniesNuget(id)) return Results.StatusCode(403);
            var versions = db.FeedNugetVersions(feedId, id);
            if (versions.Count == 0) return Results.NotFound();
            var root = Root(cfg, feedId) + "registration/" + id + "/";
            var pages = new JsonArray();
            for (var offset = 0; offset < versions.Count; offset += PageSize)
            {
                var chunk = versions.Skip(offset).Take(PageSize).ToArray();
                pages.Add(new JsonObject
                {
                    ["@id"] = root + "page/" + (offset / PageSize) + ".json",
                    ["count"] = chunk.Length,
                    ["lower"] = chunk[0],
                    ["upper"] = chunk[^1]
                });
            }
            return Results.Json(new JsonObject
            {
                ["@id"] = root + "index.json",
                ["count"] = pages.Count,
                ["items"] = pages
            });
        });

        app.MapGet("/feeds/{feedId}/nuget/v3/registration/{id}/page/{page:int}.json", async (
            string feedId, string id, int page, Settings cfg, IPackageDatabase db,
            BlobStore blobs, HttpContext ctx) =>
        {
            if (Gate(feedId, cfg, db, ctx) is { } denied) return denied;
            id = id.ToLowerInvariant();
            if (!Names.Nuget(id) || page < 0) return Results.BadRequest();
            if (cfg.DeniesNuget(id)) return Results.StatusCode(403);
            var versions = db.FeedNugetVersions(feedId, id);
            var offset = (long)page * PageSize;
            if (offset >= versions.Count) return Results.NotFound();
            var selected = versions.Skip((int)offset).Take(PageSize).ToArray();
            var items = new JsonArray();
            foreach (var version in selected)
            {
                var revision = db.GetFeedNugetRevision(feedId, id, version);
                if (revision is null) return Results.StatusCode(503);
                try { items.Add(await Leaf(cfg, feedId, revision, blobs, ctx.RequestAborted)); }
                catch (Exception ex) when (ex is InvalidDataException or XmlException)
                { return Results.StatusCode(503); }
            }
            return Results.Json(new JsonObject
            {
                ["@id"] = Root(cfg, feedId) + "registration/" + id + "/page/" + page + ".json",
                ["count"] = items.Count,
                ["lower"] = selected[0],
                ["upper"] = selected[^1],
                ["items"] = items
            });
        });

        app.MapGet("/feeds/{feedId}/nuget/v3/registration/{id}/{version}.json", async (
            string feedId, string id, string version, Settings cfg, IPackageDatabase db,
            BlobStore blobs, HttpContext ctx) =>
        {
            if (Gate(feedId, cfg, db, ctx) is { } denied) return denied;
            id = id.ToLowerInvariant();
            version = version.ToLowerInvariant();
            if (!Names.Nuget(id) || !Names.Version(version)) return Results.BadRequest();
            if (cfg.DeniesNuget(id)) return Results.StatusCode(403);
            var revision = db.GetFeedNugetRevision(feedId, id, version);
            if (revision is null) return Results.NotFound();
            try { return Results.Json(await Leaf(cfg, feedId, revision, blobs, ctx.RequestAborted)); }
            catch (Exception ex) when (ex is InvalidDataException or XmlException)
            { return Results.StatusCode(503); }
        });
    }

    private static async Task<JsonObject> Leaf(Settings cfg, string feedId,
        FeedNugetRevision revision, BlobStore blobs, CancellationToken ct)
    {
        if (!NugetDigest.Valid(revision.Sha512) || revision.Size < 0)
            throw new InvalidDataException("Invalid stored NuGet digest");
        var key = FeedNugetEndpoints.BlobKey(feedId, revision.Name, revision.Version);
        if (!await ArtifactDigests.MatchesRemoteAsync(blobs, key, "sha512",
            Convert.FromBase64String(revision.Sha512), revision.Size, cfg.MaxArtifactBytes, ct))
            throw new InvalidDataException("Private NuGet revision bytes do not match the ledger");
        var path = blobs.Existing(key) ??
            throw new InvalidDataException("Private NuGet package bytes unavailable");
        if (!await NugetDigest.MatchesAsync(path,
            new NugetDigest(revision.Sha512, revision.Size, "hosted"), ct))
            throw new InvalidDataException("NuGet package cache integrity failure");
        var nuspec = NuGetEndpoints.ReadNuspec(path);
        if (!string.Equals(nuspec.Id, revision.Name, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(nuspec.Version, revision.Version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NuGet metadata identity mismatch");
        using var reader = XmlReader.Create(new MemoryStream(nuspec.Xml),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(reader);
        var dependencies = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "dependencies");
        var groups = new JsonArray();
        if (dependencies is not null)
        {
            var frames = dependencies.Elements().Where(x => x.Name.LocalName == "group").ToList();
            if (frames.Count == 0) frames.Add(dependencies);
            foreach (var frame in frames)
            {
                var items = new JsonArray();
                foreach (var child in frame.Elements().Where(x => x.Name.LocalName == "dependency"))
                    items.Add(new JsonObject
                    {
                        ["id"] = (string?)child.Attribute("id") ?? "",
                        ["range"] = (string?)child.Attribute("version") ?? ""
                    });
                groups.Add(new JsonObject
                {
                    ["targetFramework"] = (string?)frame.Attribute("targetFramework") ?? "",
                    ["dependencies"] = items
                });
            }
        }
        var root = Root(cfg, feedId);
        var id = revision.Name;
        var version = revision.Version;
        var payload = root + "flatcontainer/" + id + "/" + version +
            "/" + id + "." + version + ".nupkg";
        return new JsonObject
        {
            ["@id"] = root + "registration/" + id + "/" + version + ".json",
            ["packageContent"] = payload,
            ["catalogEntry"] = new JsonObject
            {
                ["id"] = id,
                ["version"] = version,
                ["description"] = revision.Description,
                ["packageContent"] = payload,
                ["dependencyGroups"] = groups
            }
        };
    }
}
