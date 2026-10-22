using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Packory;

/// <summary>
/// npm's mutable dist-tags are feed-local pointers, not mutable tarballs or
/// embedded immutable publication metadata. Tombstones suppress old tags.
/// </summary>
public static class FeedNpmTagEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapMethods("/feeds/{feedId}/npm/-/package/{**resource}",
            ["GET", "PUT", "DELETE"], async (
                string feedId, string? resource, Settings cfg, IPackageDatabase db,
                BlobStore blobs, ArtifactLocks locks, HttpContext ctx) =>
        {
            var mutates = ctx.Request.Method != HttpMethods.Get;
            if (Access.RequireFeed(ctx, cfg, feedId, publish: mutates) is { } denied)
                return denied;
            if (db.Feed(feedId) is not
                { Kind: "npm", Mode: "hosted", Visibility: "private", Enabled: true })
                return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, no-store";
            var path = FeedNpmEndpoints.DecodePath(resource);
            const string marker = "/dist-tags";
            var markerIndex = path?.LastIndexOf(marker, StringComparison.Ordinal) ?? -1;
            if (markerIndex <= 0) return Results.BadRequest();
            var name = path![..markerIndex];
            var suffix = path[(markerIndex + marker.Length)..];
            if (!Names.Npm(name) || cfg.DeniesNpm(name))
                return !Names.Npm(name) ? Results.BadRequest() :
                    Results.StatusCode(StatusCodes.Status403Forbidden);
            var hasTag = suffix.StartsWith('/') && suffix.Length > 1;
            var tag = hasTag ? suffix[1..] : "";
            if (suffix != "" && !hasTag) return Results.BadRequest();
            if (hasTag && !FeedNpmEndpoints.ValidTag(tag)) return Results.BadRequest();
            if (db.FeedPackageAssets(feedId, "npm", name).Count == 0)
                return Results.NotFound();

            if (!mutates)
            {
                try
                {
                    var tags = await EffectiveTags(feedId, name, db, blobs, cfg, ctx.RequestAborted);
                    if (!hasTag) return Results.Json(tags);
                    return tags[tag] is JsonValue value && value.TryGetValue<string>(out var version)
                        ? Results.Json(version) : Results.NotFound();
                }
                catch (Exception error) when (error is InvalidDataException or IOException or JsonException)
                { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            }
            if (!hasTag) return Results.BadRequest(new { error = "A dist-tag name is required" });
            using var lease = await locks.AcquireAsync("feed-npm-tag",
                feedId + "/" + name, tag, ctx.RequestAborted);
            if (ctx.Request.Method == HttpMethods.Delete)
            {
                return db.SetFeedNpmTag(feedId, name, tag, "", deleted: true, Access.Actor(ctx))
                    ? Results.NoContent() : Results.NotFound();
            }
            if (!ctx.Request.HasJsonContentType())
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            byte[] body;
            try { body = await BlobStore.ReadBoundedAsync(ctx.Request.Body, 256, ctx.RequestAborted); }
            catch (PayloadTooLargeException)
            { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            string? requested;
            try { requested = JsonSerializer.Deserialize<string>(body); }
            catch (JsonException) { return Results.BadRequest(new { error = "Expected a JSON string version" }); }
            if (!FeedNpmEndpoints.ValidVersion(requested))
                return Results.BadRequest(new { error = "Invalid npm dist-tag target" });
            if (db.GetFeedAsset(feedId, "npm", FeedNpmEndpoints.PackagePath(name, requested!)) is null)
                return Results.NotFound();
            return db.SetFeedNpmTag(feedId, name, tag, requested!, deleted: false, Access.Actor(ctx))
                ? Results.StatusCode(StatusCodes.Status201Created) : Results.NotFound();
        }).RequireRateLimiting("publish");
    }

    public static async Task<JsonObject> EffectiveTags(string feedId, string name,
        IPackageDatabase db, BlobStore blobs, Settings cfg, CancellationToken ct)
    {
        var tags = new JsonObject();
        foreach (var entry in db.FeedPackageAssets(feedId, "npm", name)
            .OrderBy(x => x.CreatedAt, StringComparer.Ordinal)
            .ThenBy(x => x.Version, StringComparer.Ordinal))
        {
            var revision = await FeedPackageStore.ReadAsync(feedId, "npm", entry.Path,
                db, blobs, cfg.MaxArtifactBytes, ct)
                ?? throw new InvalidDataException("Private npm revision disappeared");
            FeedNpmEndpoints.ValidateStoredMetadata(revision.Metadata,
                revision.Payload, name, entry.Version);
            foreach (var tag in revision.Metadata.GetProperty("tags").EnumerateObject())
                tags[tag.Name] = tag.Value.GetString();
        }
        ApplyPointers(tags, feedId, name, db);
        return tags;
    }

    internal static void ApplyPointers(JsonObject tags, string feedId,
        string name, IPackageDatabase db)
    {
        foreach (var pointer in db.FeedNpmTags(feedId, name))
        {
            if (!FeedNpmEndpoints.ValidTag(pointer.Tag) ||
                (!pointer.Deleted && !FeedNpmEndpoints.ValidVersion(pointer.Version)))
                throw new InvalidDataException("Invalid committed npm dist-tag pointer");
            if (pointer.Deleted)
                tags.Remove(pointer.Tag);
            else
            {
                if (db.GetFeedAsset(feedId, "npm",
                    FeedNpmEndpoints.PackagePath(name, pointer.Version)) is null)
                    throw new InvalidDataException("npm dist-tag references a missing package");
                tags[pointer.Tag] = pointer.Version;
            }
        }
    }
}
