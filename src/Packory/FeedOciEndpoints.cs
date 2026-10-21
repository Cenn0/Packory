using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Packory;

/// <summary>
/// Private hosted OCI repositories use the native repository prefix feeds/{feedId}.
/// Every artifact, tag and resumable upload is bound to that exact feed and repository.
/// No mount, global registry lookup or upstream fallback can grant access to other data.
/// </summary>
public static class FeedOciEndpoints
{
    public const string ManifestMedia = "application/vnd.oci.image.manifest.v1+json";
    public const string IndexMedia = "application/vnd.oci.image.index.v1+json";
    private const string DockerManifestMedia = "application/vnd.docker.distribution.manifest.v2+json";
    private const string DockerIndexMedia = "application/vnd.docker.distribution.manifest.list.v2+json";
    private const string ArtifactMedia = "application/vnd.oci.artifact.manifest.v1+json";
    private static readonly HashSet<string> AllowedMedia = new(StringComparer.Ordinal)
    { ManifestMedia, IndexMedia, DockerManifestMedia, DockerIndexMedia, ArtifactMedia };

    public static string AssetPath(string repository, string digest, bool manifest) =>
        (manifest ? "manifest/" : "blob/") + repository + "/" + digest[7..];

    public static string BlobKey(FeedAssetEntry asset) =>
        "feeds/" + asset.FeedId + "/oci/" + asset.Path +
        (asset.Path.StartsWith("manifest/", StringComparison.Ordinal) ? ".json" : ".blob");

    public static bool ValidAsset(FeedAssetEntry asset)
    {
        if (asset.Kind != "oci" || !FeedDefinition.ValidId(asset.FeedId) ||
            !ValidRepository(asset.FeedId, asset.Name) || !OciEndpoints.ValidDigest(asset.Version) ||
            asset.Sha256 != asset.Version[7..] || asset.Size < 0)
            return false;
        return asset.Path == AssetPath(asset.Name, asset.Version, false)
            ? asset.MediaType == "application/octet-stream"
            : asset.Path == AssetPath(asset.Name, asset.Version, true) &&
              asset.Size > 0 && AllowedMedia.Contains(asset.MediaType);
    }

    private static bool ValidRepository(string feed, string repository) =>
        OciEndpoints.ValidRepository(repository) && OciEndpoints.ValidRepository("feeds/" + feed + "/" + repository);

    private static bool Active(IPackageDatabase db, string feed) =>
        db.Feed(feed) is { Kind: "oci", Mode: "hosted", Visibility: "private", Enabled: true };

    public static void Map(WebApplication app)
    {
        var cfg = app.Services.GetRequiredService<Settings>();
        // Reuse the bounded, durable offset/rollback implementation, but never
        // expose its identities through legacy global OCI upload sessions.
        var uploads = new OciUploadStore(new Settings
        {
            DataDirectory = Path.Combine(cfg.DataDirectory, "feed-oci"),
            MaxArtifactBytes = cfg.MaxArtifactBytes
        });
        const string route = "/v2/feeds/{feedId}/{**resource}";
        app.MapMethods(route, ["GET", "HEAD"], (string feedId, string? resource,
            Settings settings, IPackageDatabase db, BlobStore blobs, ArtifactLocks locks, HttpContext ctx) =>
            Handle(feedId, resource, settings, db, blobs, locks, uploads, ctx));
        app.MapMethods(route, ["POST", "PATCH", "PUT", "DELETE"], (string feedId, string? resource,
            Settings settings, IPackageDatabase db, BlobStore blobs, ArtifactLocks locks, HttpContext ctx) =>
            Handle(feedId, resource, settings, db, blobs, locks, uploads, ctx)).RequireRateLimiting("publish");
    }

    private static async Task<IResult> Handle(string feed, string? resource, Settings cfg,
        IPackageDatabase db, BlobStore blobs, ArtifactLocks locks, OciUploadStore uploads, HttpContext ctx)
    {
        ctx.Response.Headers["Docker-Distribution-API-Version"] = "registry/2.0";
        ctx.Response.Headers.CacheControl = "private, no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var write = ctx.Request.Method is not ("GET" or "HEAD");
        if (Access.RequireFeed(ctx, cfg, feed, publish: write) is { } denied) return denied;
        if (!Active(db, feed)) return Error(404, "NAME_UNKNOWN", "Private OCI feed unavailable");
        var value = resource ?? "";
        if (value.Length > 512 || value.Contains('\\') || value.Contains('%') ||
            value.Split('/').Any(p => p is "." or ".."))
            return Error(400, "NAME_INVALID", "Invalid OCI path");
        // Protocol words are valid repository segments. Select the last
        // endpoint delimiter, so e.g. team/blobs/uploads/image is one name.
        var boundary = new[] { "/blobs/uploads/", "/blobs/", "/manifests/", "/referrers/" }
            .Select(marker => value.LastIndexOf(marker, StringComparison.Ordinal))
            .Append(value.EndsWith("/tags/list", StringComparison.Ordinal) ? value.Length - "/tags/list".Length : -1)
            .Max();
        try
        {
            var at = value.LastIndexOf("/blobs/uploads/", StringComparison.Ordinal);
            if (at > 0 && at == boundary && ValidRepository(feed, value[..at]))
                return await Upload(ctx, cfg, db, blobs, locks, uploads, feed,
                    value[..at], value[(at + "/blobs/uploads/".Length)..]);
            at = value.LastIndexOf("/blobs/", StringComparison.Ordinal);
            if (at > 0 && at == boundary && ValidRepository(feed, value[..at]))
            {
                var digest = value[(at + "/blobs/".Length)..];
                if (!OciEndpoints.ValidDigest(digest)) return Error(400, "DIGEST_INVALID", "Invalid digest");
                if (ctx.Request.Method is not ("GET" or "HEAD")) return Unsupported();
                return await Read(ctx, cfg, db, blobs, feed, value[..at], digest, manifest: false);
            }
            at = value.LastIndexOf("/manifests/", StringComparison.Ordinal);
            if (at > 0 && at == boundary && ValidRepository(feed, value[..at]))
            {
                var reference = value[(at + "/manifests/".Length)..];
                if (!OciEndpoints.ValidReference(reference)) return Error(400, "NAME_INVALID", "Invalid reference");
                if (ctx.Request.Method is "GET" or "HEAD")
                {
                    var digest = OciEndpoints.ValidDigest(reference) ? reference : db.GetFeedOciTag(feed, value[..at], reference);
                    return digest is null ? Error(404, "MANIFEST_UNKNOWN", "Manifest not found") :
                        await Read(ctx, cfg, db, blobs, feed, value[..at], digest, manifest: true);
                }
                return ctx.Request.Method == "PUT" ?
                    await PutManifest(ctx, cfg, db, blobs, locks, feed, value[..at], reference) : Unsupported();
            }
            if (value.EndsWith("/tags/list", StringComparison.Ordinal) &&
                value.Length - "/tags/list".Length == boundary &&
                ValidRepository(feed, value[..^"/tags/list".Length]))
            {
                if (ctx.Request.Method != "GET") return Unsupported();
                var repository = value[..^"/tags/list".Length];
                var last = ctx.Request.Query["last"].ToString();
                if (last.Length != 0 && (!OciEndpoints.ValidReference(last) || OciEndpoints.ValidDigest(last)))
                    return Error(400, "NAME_INVALID", "Invalid tag cursor");
                var count = 1000;
                if (ctx.Request.Query.ContainsKey("n") &&
                    (!int.TryParse(ctx.Request.Query["n"], NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 0))
                    return Error(400, "UNSUPPORTED", "Invalid page size");
                count = Math.Min(count, 1000);
                var tags = db.FeedOciTags(feed, repository).Where(t => string.CompareOrdinal(t, last) > 0)
                    .Order(StringComparer.Ordinal).Take(count + 1).ToArray();
                var page = tags.Take(count).ToArray();
                if (tags.Length > page.Length && page.Length > 0)
                    ctx.Response.Headers.Link = "<" + Url(cfg, feed, repository) + "/tags/list?n=" + count +
                        "&last=" + Uri.EscapeDataString(page[^1]) + ">; rel=\"next\"";
                return Results.Json(new { name = "feeds/" + feed + "/" + repository, tags = page });
            }
            at = value.LastIndexOf("/referrers/", StringComparison.Ordinal);
            if (at > 0 && at == boundary && ValidRepository(feed, value[..at]))
            {
                var digest = value[(at + "/referrers/".Length)..];
                if (!OciEndpoints.ValidDigest(digest)) return Error(400, "DIGEST_INVALID", "Invalid subject");
                return ctx.Request.Method == "GET" ?
                    await Referrers(ctx, cfg, db, blobs, feed, value[..at], digest) : Unsupported();
            }
            return Error(404, "NAME_UNKNOWN", "Unknown private OCI endpoint");
        }
        catch (PayloadTooLargeException) { return Error(413, "SIZE_INVALID", "Upload limit exceeded"); }
        catch (StorageQuotaExceededException) { return Error(507, "UNAVAILABLE", "Storage capacity exhausted"); }
    }

    private static async Task<IResult> Read(HttpContext ctx, Settings cfg, IPackageDatabase db,
        BlobStore blobs, string feed, string repository, string digest, bool manifest)
    {
        if (!OciEndpoints.ValidDigest(digest)) return Error(503, "DIGEST_INVALID", "Invalid committed digest");
        var asset = db.GetFeedAsset(feed, "oci", AssetPath(repository, digest, manifest));
        if (asset is null) return Error(404, manifest ? "MANIFEST_UNKNOWN" : "BLOB_UNKNOWN", "Content not found");
        var path = await VerifiedPath(asset, cfg, blobs, ctx.RequestAborted);
        if (path is null) return Error(503, "DIGEST_INVALID", "Stored content requires integrity recovery");
        ctx.Response.Headers["Docker-Content-Digest"] = digest;
        return Results.File(path, asset.MediaType, enableRangeProcessing: !manifest);
    }

    private static async Task<string?> VerifiedPath(FeedAssetEntry asset, Settings cfg, BlobStore blobs, CancellationToken ct)
    {
        var limit = asset.Path.StartsWith("manifest/", StringComparison.Ordinal) ? cfg.MaxMetadataBytes : cfg.MaxArtifactBytes;
        if (!ValidAsset(asset) || asset.Size > limit) return null;
        var key = BlobKey(asset);
        if (blobs is S3BlobStore remote)
        {
            var probe = await remote.InspectRemoteAsync(key, "sha256", limit, ct);
            if (!probe.Exists || probe.ExceedsLimit || probe.Size != asset.Size ||
                probe.Digest is null || Convert.ToHexStringLower(probe.Digest) != asset.Sha256)
                return null;
        }
        var path = blobs.Existing(key);
        return path is not null && await ArtifactDigests.MatchesSha256Async(path, asset.Sha256, asset.Size, ct) ? path : null;
    }

    private static async Task<IResult> Upload(HttpContext ctx, Settings cfg, IPackageDatabase db,
        BlobStore blobs, ArtifactLocks locks, OciUploadStore sessions, string feed, string repository, string id)
    {
        var scope = "feeds/" + feed + "/" + repository;
        if (id.Length == 0 && ctx.Request.Method == "POST")
        {
            var digest = ctx.Request.Query["digest"].ToString();
            if (ctx.Request.Query.ContainsKey("digest") && !OciEndpoints.ValidDigest(digest))
                return Error(400, "DIGEST_INVALID", "Invalid digest");
            if (digest.Length == 0 && ctx.Request.ContentLength > 0)
                return Error(400, "BLOB_UPLOAD_INVALID", "A session starts with an empty body");
            // Mounting is deliberately not performed. Returning 202 is the OCI
            // fallback and discloses neither presence nor bytes in any source.
            string started;
            try { started = sessions.Begin(scope); }
            catch (OciUploadCapacityException)
            {
                ctx.Response.Headers.RetryAfter = "60";
                return Error(429, "TOOMANYREQUESTS", "Upload session capacity reached");
            }
            if (digest.Length != 0 && sessions.TryGet(scope, started, out var upload) && upload is not null)
                return await Complete(ctx, cfg, db, blobs, locks, sessions, upload, started, feed, repository, digest);
            SetUploadHeaders(ctx, cfg, feed, repository, started, 0);
            return Results.StatusCode(202);
        }
        if (!sessions.TryGet(scope, id, out var session) || session is null)
            return Error(404, "BLOB_UPLOAD_UNKNOWN", "Upload session not found");
        if (ctx.Request.Method == "PUT")
        {
            var digest = ctx.Request.Query["digest"].ToString();
            if (!OciEndpoints.ValidDigest(digest)) return Error(400, "DIGEST_INVALID", "Missing digest");
            return await Complete(ctx, cfg, db, blobs, locks, sessions, session, id, feed, repository, digest);
        }
        if (ctx.Request.Method is not ("GET" or "PATCH" or "DELETE")) return Unsupported();
        await session.Gate.WaitAsync(ctx.RequestAborted);
        try
        {
            if (!sessions.IsCurrent(scope, id, session)) return Error(404, "BLOB_UPLOAD_UNKNOWN", "Upload session expired");
            if (ctx.Request.Method == "DELETE")
            {
                sessions.EndLocked(id, session);
                return Results.StatusCode(204);
            }
            if (ctx.Request.Method == "PATCH") await Append(ctx, cfg, sessions, session);
            SetUploadHeaders(ctx, cfg, feed, repository, id, session.CommittedLength);
            return Results.StatusCode(ctx.Request.Method == "GET" ? 204 : 202);
        }
        catch (OciChunkRangeException)
        {
            SetUploadHeaders(ctx, cfg, feed, repository, id, session.CommittedLength);
            return Error(416, "BLOB_UPLOAD_INVALID", "Chunk does not match committed offset");
        }
        finally { session.Gate.Release(); }
    }

    private static Task<long> Append(HttpContext ctx, Settings cfg, OciUploadStore sessions, OciUploadStore.Ticket ticket) =>
        OciChunkWriter.AppendAsync(sessions, ticket, ctx.Request.Body, cfg.MaxArtifactBytes,
            ctx.Request.Headers.ContainsKey("Content-Range") ? ctx.Request.Headers.ContentRange.ToString() : null,
            ctx.Request.ContentLength, ctx.RequestAborted);

    private static async Task<IResult> Complete(HttpContext ctx, Settings cfg, IPackageDatabase db,
        BlobStore blobs, ArtifactLocks locks, OciUploadStore sessions, OciUploadStore.Ticket session,
        string id, string feed, string repository, string digest)
    {
        await session.Gate.WaitAsync(ctx.RequestAborted);
        try
        {
            if (!sessions.IsCurrent("feeds/" + feed + "/" + repository, id, session))
                return Error(404, "BLOB_UPLOAD_UNKNOWN", "Upload session expired");
            await Append(ctx, cfg, sessions, session);
            await using (var source = File.OpenRead(session.Path))
            {
                if ("sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(source, ctx.RequestAborted)) != digest)
                    return Error(400, "DIGEST_INVALID", "Blob digest mismatch");
                source.Position = 0;
                var asset = NewAsset(feed, repository, digest, false, "application/octet-stream", source.Length);
                if (await Persist(ctx, cfg, db, blobs, locks, asset, source) is { } error) return error;
            }
            sessions.EndLocked(id, session);
            ctx.Response.Headers.Location = Url(cfg, feed, repository) + "/blobs/" + digest;
            ctx.Response.Headers["Docker-Content-Digest"] = digest;
            return Results.StatusCode(201);
        }
        catch (OciChunkRangeException)
        {
            SetUploadHeaders(ctx, cfg, feed, repository, id, session.CommittedLength);
            return Error(416, "BLOB_UPLOAD_INVALID", "Chunk does not match committed offset");
        }
        finally { session.Gate.Release(); }
    }

    private static FeedAssetEntry NewAsset(string feed, string repository, string digest,
        bool manifest, string media, long length) => new(feed, "oci", AssetPath(repository, digest, manifest),
            repository, digest, media, digest[7..], length, DateTimeOffset.UtcNow.ToString("O"));

    private static async Task<IResult?> Persist(HttpContext ctx, Settings cfg, IPackageDatabase db,
        BlobStore blobs, ArtifactLocks locks, FeedAssetEntry asset, Stream source)
    {
        using var lease = await locks.AcquireAsync("feed-oci", asset.FeedId + "/" + asset.Name, asset.Path, ctx.RequestAborted);
        var existing = db.GetFeedAsset(asset.FeedId, "oci", asset.Path);
        if (existing is not null)
            return existing.Sha256 == asset.Sha256 && existing.Size == asset.Size && existing.MediaType == asset.MediaType &&
                await VerifiedPath(existing, cfg, blobs, ctx.RequestAborted) is not null ? null :
                Error(503, "DIGEST_INVALID", "Immutable content requires recovery");
        var key = BlobKey(asset);
        db.BeginPublication(key, "feed-oci", asset.FeedId + "|" + asset.Path, asset.Version);
        await blobs.PutAsync(key, source, asset.Path.StartsWith("manifest/", StringComparison.Ordinal) ?
            cfg.MaxMetadataBytes : cfg.MaxArtifactBytes, ctx.RequestAborted);
        if (await VerifiedPath(asset, cfg, blobs, ctx.RequestAborted) is null)
            return Error(503, "DIGEST_INVALID", "Content-addressed blob collision");
        if (!db.PublishFeedAsset(asset, Access.Actor(ctx)))
            return Error(409, "UNAVAILABLE", "Feed changed during publication");
        db.CompletePublication(key);
        return null;
    }

    private static async Task<IResult> PutManifest(HttpContext ctx, Settings cfg, IPackageDatabase db,
        BlobStore blobs, ArtifactLocks locks, string feed, string repository, string reference)
    {
        var media = ctx.Request.ContentType?.Split(';', 2)[0].Trim() ?? "";
        if (!AllowedMedia.Contains(media)) return Error(415, "MANIFEST_INVALID", "Unsupported manifest media type");
        var bytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body, cfg.MaxMetadataBytes, ctx.RequestAborted);
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes); }
        catch (JsonException) { return Error(400, "MANIFEST_INVALID", "Invalid manifest JSON"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !UniqueProperties(root) ||
                !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var version) || version != 2 ||
                (root.TryGetProperty("mediaType", out var declared) &&
                    (declared.ValueKind != JsonValueKind.String || declared.GetString() != media)))
                return Error(400, "MANIFEST_INVALID", "Unsupported manifest schema");
            if (root.TryGetProperty("artifactType", out var artifactType) &&
                (artifactType.ValueKind != JsonValueKind.String || !RawEndpoints.ValidMediaType(artifactType.GetString())))
                return Error(400, "MANIFEST_INVALID", "Invalid artifact media type");
            if (root.TryGetProperty("annotations", out var annotations) &&
                (annotations.ValueKind != JsonValueKind.Object || annotations.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String)))
                return Error(400, "MANIFEST_INVALID", "Invalid manifest annotations");
            if (!await ValidateContent(root, media, cfg, db, blobs, feed, repository, ctx.RequestAborted))
                return Error(400, "MANIFEST_BLOB_UNKNOWN", "Manifest references invalid or unavailable repository content");
            string? subject = null;
            if (root.TryGetProperty("subject", out var subjectNode))
            {
                if (!Descriptor(subjectNode, out subject, out _, out _))
                    return Error(400, "MANIFEST_INVALID", "Invalid subject descriptor");
                // OCI 1.1 requires acceptance before the subject exists. This is
                // an inert association, never a lookup in any other repository.
            }
            var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (OciEndpoints.ValidDigest(reference) && reference != digest)
                return Error(400, "DIGEST_INVALID", "Manifest digest mismatch");
            var asset = NewAsset(feed, repository, digest, true, media, bytes.LongLength);
            if (await Persist(ctx, cfg, db, blobs, locks, asset, new MemoryStream(bytes, false)) is { } error) return error;
            if (!OciEndpoints.ValidDigest(reference))
            {
                try { db.SetFeedOciTag(feed, repository, reference, digest, Access.Actor(ctx)); }
                catch (InvalidOperationException) { return Error(409, "UNAVAILABLE", "Feed changed during tag publication"); }
            }
            ctx.Response.Headers.Location = Url(cfg, feed, repository) + "/manifests/" + digest;
            ctx.Response.Headers["Docker-Content-Digest"] = digest;
            if (subject is not null) ctx.Response.Headers["OCI-Subject"] = subject;
            return Results.StatusCode(201);
        }
    }

    private static async Task<bool> ValidateContent(JsonElement root, string media, Settings cfg,
        IPackageDatabase db, BlobStore blobs, string feed, string repository, CancellationToken ct)
    {
        var isIndex = media is IndexMedia or DockerIndexMedia;
        var isArtifact = media == ArtifactMedia;
        var requiredArray = isIndex ? "manifests" : isArtifact ? "blobs" : "layers";
        if (!root.TryGetProperty(requiredArray, out var required) || required.ValueKind != JsonValueKind.Array ||
            required.GetArrayLength() > 4096 ||
            (!isIndex && !isArtifact && !root.TryGetProperty("config", out _))) return false;
        var verified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in new[] { "config", "layers", "blobs", "manifests" })
        {
            if (!root.TryGetProperty(property, out var element)) continue;
            if (property != "config" && (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > 4096)) return false;
            var descriptors = property == "config" ? new[] { element } : element.EnumerateArray().ToArray();
            foreach (var descriptor in descriptors)
            {
                if (!Descriptor(descriptor, out var digest, out var size, out var declaredMedia)) return false;
                var path = AssetPath(repository, digest!, property == "manifests");
                var asset = db.GetFeedAsset(feed, "oci", path);
                if (asset is null || asset.Size != size ||
                    (property == "manifests" && asset.MediaType != declaredMedia)) return false;
                if (verified.Add(path) && await VerifiedPath(asset, cfg, blobs, ct) is null) return false;
            }
        }
        return true;
    }

    private static bool Descriptor(JsonElement node, out string? digest, out long size, out string? media)
    {
        digest = media = null;
        size = 0;
        if (node.ValueKind != JsonValueKind.Object ||
            // Native OCI clients may try these URLs before their registry.
            // This hosted profile never publishes alternate download origins.
            node.TryGetProperty("urls", out _) ||
            !node.TryGetProperty("digest", out var dg) || dg.ValueKind != JsonValueKind.String ||
            !OciEndpoints.ValidDigest(dg.GetString() ?? "") ||
            !node.TryGetProperty("size", out var length) || length.ValueKind != JsonValueKind.Number ||
            !length.TryGetInt64(out size) || size < 0 ||
            !node.TryGetProperty("mediaType", out var mt) || mt.ValueKind != JsonValueKind.String ||
            !RawEndpoints.ValidMediaType(mt.GetString())) return false;
        digest = dg.GetString();
        media = mt.GetString();
        if (node.TryGetProperty("data", out var embedded))
        {
            if (embedded.ValueKind != JsonValueKind.String) return false;
            var encoded = embedded.GetString()!;
            // The enclosing manifest is metadata-bounded. Reject impossible
            // lengths before allocating the inline content, then verify its
            // identity. Inline bytes never replace the same-feed ledger check.
            var maximumDecoded = encoded.Length / 4L * 3;
            if (encoded.Length % 4 != 0 || size > maximumDecoded || maximumDecoded - size > 2)
                return false;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException) { return false; }
            if (bytes.LongLength != size || Convert.ToBase64String(bytes) != encoded ||
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)) != digest)
                return false;
        }
        return true;
    }

    private static bool UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueProperties(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (!UniqueProperties(item)) return false;
        return true;
    }

    private static async Task<IResult> Referrers(HttpContext ctx, Settings cfg, IPackageDatabase db,
        BlobStore blobs, string feed, string repository, string subject)
    {
        var filter = ctx.Request.Query["artifactType"].ToString();
        if (filter.Length > 0 && !RawEndpoints.ValidMediaType(filter))
            return Error(400, "MANIFEST_INVALID", "Invalid artifact media type");
        var rows = db.FeedPackageAssets(feed, "oci", repository)
            .Where(a => a.Path.StartsWith("manifest/", StringComparison.Ordinal)).Take(1001).ToArray();
        // Do not silently return an incomplete reference set. This bounded
        // hosted profile is not an unlimited OCI discovery implementation.
        if (rows.Length > 1000) return Error(503, "UNAVAILABLE", "Referrer inspection capacity exceeded");
        var descriptors = new List<Dictionary<string, object?>>();
        foreach (var row in rows)
        {
            var path = await VerifiedPath(row, cfg, blobs, ctx.RequestAborted);
            if (path is null) return Error(503, "DIGEST_INVALID", "Referrer content requires recovery");
            await using var stream = File.OpenRead(path);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ctx.RequestAborted);
            var root = doc.RootElement;
            if (!root.TryGetProperty("subject", out var node) ||
                !Descriptor(node, out var digest, out _, out _) || digest != subject) continue;
            string? type = null;
            if (root.TryGetProperty("artifactType", out var artifact)) type = artifact.GetString();
            else if (root.TryGetProperty("config", out var config) && config.TryGetProperty("mediaType", out var mt)) type = mt.GetString();
            if (filter.Length > 0 && type != filter) continue;
            var descriptor = new Dictionary<string, object?>
            { ["digest"] = row.Version, ["mediaType"] = row.MediaType, ["size"] = row.Size };
            if (type is not null) descriptor["artifactType"] = type;
            if (root.TryGetProperty("annotations", out var annotations)) descriptor["annotations"] = annotations.Clone();
            descriptors.Add(descriptor);
        }
        if (ctx.Request.Query.ContainsKey("artifactType")) ctx.Response.Headers["OCI-Filters-Applied"] = "artifactType";
        return Results.Json(new { schemaVersion = 2, mediaType = IndexMedia, manifests = descriptors }, contentType: IndexMedia);
    }

    private static string Url(Settings cfg, string feed, string repository) => cfg.Base + "/v2/feeds/" + feed + "/" + repository;
    private static void SetUploadHeaders(HttpContext ctx, Settings cfg, string feed, string repository, string id, long offset)
    {
        ctx.Response.Headers.Location = Url(cfg, feed, repository) + "/blobs/uploads/" + id;
        ctx.Response.Headers["Docker-Upload-UUID"] = id;
        ctx.Response.Headers.Range = "0-" + Math.Max(0, offset - 1);
    }
    private static IResult Unsupported() => Error(405, "UNSUPPORTED", "Method unsupported");
    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { errors = new[] { new { code, message } } }, statusCode: status);
}
