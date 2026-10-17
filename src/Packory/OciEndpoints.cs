using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Packory;

/// <summary>Bounded single-writer OCI Distribution adapter; see the tested support matrix.</summary>
public static class OciEndpoints
{
    private const string ManifestMedia = "application/vnd.oci.image.manifest.v1+json";
    private const string IndexMedia = "application/vnd.oci.image.index.v1+json";
    private static readonly Regex RepositoryRegex = new(
        "\\A[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*\\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DigestRegex = new("\\Asha256:[0-9a-f]{64}\\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new("\\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedMedia = new(StringComparer.Ordinal)
    {
        ManifestMedia, IndexMedia,
        "application/vnd.docker.distribution.manifest.v2+json",
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.artifact.manifest.v1+json"
    };

    public static bool ValidRepository(string name) => name.Length is >= 1 and <= 255 && RepositoryRegex.IsMatch(name);
    public static bool ValidDigest(string digest) => DigestRegex.IsMatch(digest);
    public static bool ValidReference(string reference) => ValidDigest(reference) || TagRegex.IsMatch(reference);
    public static string BlobKey(string digest) => "oci/blobs/sha256/" + digest[7..] + ".blob";
    public static string ManifestKey(string digest) => "oci/manifests/sha256/" + digest[7..] + ".json";

    public static void Map(WebApplication app)
    {
        app.MapGet("/v2/", (Settings cfg, HttpContext ctx) =>
        {
            ctx.Response.Headers["Docker-Distribution-API-Version"] = "registry/2.0";
            if (cfg.OciAuthenticatedPing && Access.Require(ctx, cfg, write: true) is { } pingDenied)
            {
                ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Packory OCI\"";
                return pingDenied;
            }
            if (Access.Require(ctx, cfg) is { } denied)
            {
                ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Packory OCI\"";
                return denied;
            }
            return Results.Ok();
        });

        app.MapMethods("/v2/{**resource}", ["GET", "HEAD", "POST", "PATCH", "PUT", "DELETE"],
            async (string? resource, Settings cfg, IPackageDatabase db, BlobStore blobs,
                OciUploadStore uploads, ArtifactLocks locks, HttpContext ctx) =>
        {
            ctx.Response.Headers["Docker-Distribution-API-Version"] = "registry/2.0";
            var write = ctx.Request.Method is "PUT" or "POST" or "PATCH" or "DELETE";
            if (Access.Require(ctx, cfg, write) is { } denied)
            {
                ctx.Response.Headers["WWW-Authenticate"] = "Basic realm=\"Packory OCI\"";
                return denied;
            }
            var value = resource ?? "";
            if (value.Length > 512 || value.Contains('\\') || value.Contains('%') ||
                value.Split('/').Any(part => part is "." or ".."))
                return Error(400, "NAME_INVALID", "Invalid OCI registry path");
            if (write)
            {
                var boundaries = new[] { "/blobs/uploads/", "/blobs/", "/manifests/", "/tags/list", "/referrers/" };
                var cut = boundaries.Select(boundary => value.IndexOf(boundary, StringComparison.Ordinal))
                    .Where(i => i > 0).DefaultIfEmpty(-1).Min();
                if (cut <= 0 || !ValidRepository(value[..cut]))
                    return Error(400, "NAME_INVALID", "Invalid OCI mutation repository");
                if (Access.RequireNamespace(ctx, cfg, "oci", value[..cut]) is { } forbidden) return forbidden;
            }
            var uploadsAt = value.IndexOf("/blobs/uploads/", StringComparison.Ordinal);
            if (uploadsAt > 0)
            {
                var repository = value[..uploadsAt];
                var id = value[(uploadsAt + "/blobs/uploads/".Length)..];
                if (!ValidRepository(repository)) return Error(400, "NAME_INVALID", "Invalid repository");
                return await Upload(ctx, cfg, db, blobs, uploads, repository, id);
            }
            var blobsAt = value.IndexOf("/blobs/", StringComparison.Ordinal);
            if (blobsAt > 0)
            {
                var repository = value[..blobsAt];
                var digest = value[(blobsAt + "/blobs/".Length)..];
                if (!ValidRepository(repository) || !ValidDigest(digest))
                    return Error(400, "DIGEST_INVALID", "Invalid digest or repository");
                if (ctx.Request.Method is not ("GET" or "HEAD")) return Error(405, "UNSUPPORTED", "Method unsupported");
                if (!db.OciHasBlob(repository, digest)) return Error(404, "BLOB_UNKNOWN", "Blob not found");
                var path = blobs.Existing(BlobKey(digest));
                if (path is null) return Error(404, "BLOB_UNKNOWN", "Blob unavailable");
                if (!await DigestMatches(path, digest, ctx.RequestAborted))
                    return Error(503, "DIGEST_INVALID", "Stored blob requires integrity recovery");
                ctx.Response.Headers["Docker-Content-Digest"] = digest;
                return Results.File(path, "application/octet-stream", enableRangeProcessing: true);
            }
            var manifestsAt = value.IndexOf("/manifests/", StringComparison.Ordinal);
            if (manifestsAt > 0)
            {
                var repository = value[..manifestsAt];
                var reference = value[(manifestsAt + "/manifests/".Length)..];
                if (!ValidRepository(repository) || !ValidReference(reference))
                    return Error(400, "NAME_INVALID", "Invalid repository or reference");
                if (ctx.Request.Method is "GET" or "HEAD")
                {
                    var manifest = db.OciManifest(repository, reference);
                    if (manifest is null) return Error(404, "MANIFEST_UNKNOWN", "Manifest not found");
                    var path = blobs.Existing(ManifestKey(manifest.Digest));
                    if (path is null) return Error(404, "MANIFEST_UNKNOWN", "Manifest unavailable");
                    if (new FileInfo(path).Length != manifest.Size ||
                        !await DigestMatches(path, manifest.Digest, ctx.RequestAborted))
                        return Error(503, "DIGEST_INVALID", "Stored manifest requires integrity recovery");
                    ctx.Response.Headers["Docker-Content-Digest"] = manifest.Digest;
                    return Results.File(path, manifest.MediaType, enableRangeProcessing: false);
                }
                if (ctx.Request.Method != "PUT") return Error(405, "UNSUPPORTED", "Method unsupported");
                return await PutManifest(ctx, cfg, db, blobs, locks, repository, reference);
            }
            if (value.EndsWith("/tags/list", StringComparison.Ordinal))
            {
                var repository = value[..^"/tags/list".Length];
                if (!ValidRepository(repository)) return Error(400, "NAME_INVALID", "Invalid repository");
                if (ctx.Request.Method != "GET") return Error(405, "UNSUPPORTED", "Method unsupported");
                var tags = db.OciTags(repository);
                var last = ctx.Request.Query["last"].ToString();
                var n = 1000;
                if (ctx.Request.Query.ContainsKey("n") &&
                    (!int.TryParse(ctx.Request.Query["n"], out n) || n < 0))
                    return Error(400, "UNSUPPORTED", "Invalid tag page size");
                n = Math.Min(n, 1000);
                var filtered = tags.Where(tag => string.CompareOrdinal(tag, last) > 0).ToArray();
                var page = filtered.Take(n).ToArray();
                if (filtered.Length > page.Length && page.Length != 0)
                {
                    var nextUrl = cfg.Base + "/v2/" + repository + "/tags/list?n=" + n +
                        "&last=" + Uri.EscapeDataString(page[^1]);
                    ctx.Response.Headers.Link = "<" + nextUrl + ">; rel=\"next\"";
                }
                return Results.Json(new { name = repository, tags = page });
            }
            var referrersAt = value.IndexOf("/referrers/", StringComparison.Ordinal);
            if (referrersAt > 0)
            {
                var repository = value[..referrersAt];
                var digest = value[(referrersAt + "/referrers/".Length)..];
                if (!ValidRepository(repository) || !ValidDigest(digest))
                    return Error(400, "DIGEST_INVALID", "Invalid repository or digest");
                if (ctx.Request.Method != "GET") return Error(405, "UNSUPPORTED", "Method unsupported");
                IEnumerable<OciReferrerEntry> refs = db.OciReferrers(repository, digest);
                if (ctx.Request.Query.ContainsKey("artifactType"))
                {
                    var filter = ctx.Request.Query["artifactType"].ToString();
                    refs = refs.Where(entry => entry.ArtifactType == filter);
                    ctx.Response.Headers["OCI-Filters-Applied"] = "artifactType";
                }
                return Results.Json(new
                {
                    schemaVersion = 2,
                    mediaType = IndexMedia,
                    manifests = refs.Select(entry => new
                    {
                        digest = entry.Digest, mediaType = entry.MediaType,
                        size = entry.Size, artifactType = entry.ArtifactType
                    })
                }, contentType: IndexMedia);
            }
            return Error(404, "NAME_UNKNOWN", "Unknown registry endpoint");
        });
    }

    private static async Task<IResult> Upload(HttpContext ctx, Settings cfg, IPackageDatabase db,
        BlobStore blobs, OciUploadStore sessions, string repository, string id)
    {
        var method = ctx.Request.Method;
        if (id.Length == 0 && method == "POST")
        {
            var suppliedDigest = ctx.Request.Query["digest"].ToString();
            if (ctx.Request.Query.ContainsKey("digest") && !ValidDigest(suppliedDigest))
                return Error(400, "DIGEST_INVALID", "Invalid SHA256 digest");
            var mount = ctx.Request.Query["mount"].ToString();
            var from = ctx.Request.Query["from"].ToString();
            if (ValidDigest(mount) && ValidRepository(from) && db.OciHasBlob(from, mount) &&
                blobs.Existing(BlobKey(mount)) is { } mounted)
            {
                if (!await DigestMatches(mounted, mount, ctx.RequestAborted))
                    return Error(503, "DIGEST_INVALID", "Stored mount source requires integrity recovery");
                db.OciAddBlob(repository, mount);
                ctx.Response.Headers.Location = cfg.Base + "/v2/" + repository + "/blobs/" + mount;
                ctx.Response.Headers["Docker-Content-Digest"] = mount;
                return Results.StatusCode(201);
            }
            string uuid;
            try { uuid = sessions.Begin(repository); }
            catch (OciUploadCapacityException)
            {
                ctx.Response.Headers.RetryAfter = "60";
                return Error(429, "TOOMANYREQUESTS", "OCI upload session capacity reached");
            }
            if (ValidDigest(suppliedDigest))
            {
                if (!sessions.TryGet(repository, uuid, out var started) || started is null)
                    return Error(500, "BLOB_UPLOAD_INVALID", "Upload initialization failed");
                return await Complete(ctx, cfg, db, blobs, sessions, started, uuid, repository, suppliedDigest);
            }
            SetUploadHeaders(ctx, cfg, repository, uuid, 0);
            return Results.StatusCode(202);
        }
        if (!sessions.TryGet(repository, id, out var session) || session is null)
            return Error(404, "BLOB_UPLOAD_UNKNOWN", "Upload session not found");
        if (method == "PUT")
        {
            var digest = ctx.Request.Query["digest"].ToString();
            if (!ValidDigest(digest)) return Error(400, "DIGEST_INVALID", "Missing SHA256 digest");
            return await Complete(ctx, cfg, db, blobs, sessions, session, id, repository, digest);
        }
        if (method is not ("PATCH" or "GET" or "DELETE")) return Error(405, "UNSUPPORTED", "Method unsupported");
        await session.Gate.WaitAsync(ctx.RequestAborted);
        try
        {
            if (!sessions.IsCurrent(repository, id, session))
                return Error(404, "BLOB_UPLOAD_UNKNOWN", "Upload session is no longer active");
            if (method == "DELETE")
            {
                sessions.EndLocked(id, session);
                return Results.StatusCode(204);
            }
            if (method == "GET")
            {
                SetUploadHeaders(ctx, cfg, repository, id, session.CommittedLength);
                return Results.StatusCode(204);
            }
            var count = await Append(ctx, cfg, sessions, session);
            SetUploadHeaders(ctx, cfg, repository, id, count);
            return Results.StatusCode(202);
        }
        catch (OciChunkRangeException)
        {
            SetUploadHeaders(ctx, cfg, repository, id, session.CommittedLength);
            return Error(416, "BLOB_UPLOAD_INVALID", "Chunk range or length does not match committed upload offset");
        }
        finally { session.Gate.Release(); }
    }

    private static Task<long> Append(HttpContext ctx, Settings cfg, OciUploadStore sessions,
        OciUploadStore.Ticket session) => OciChunkWriter.AppendAsync(sessions, session,
        ctx.Request.Body, cfg.MaxArtifactBytes,
        ctx.Request.Headers.ContainsKey("Content-Range") ? ctx.Request.Headers.ContentRange.ToString() : null,
        ctx.Request.ContentLength, ctx.RequestAborted);

    private static async Task<IResult> Complete(HttpContext ctx, Settings cfg, IPackageDatabase db, BlobStore blobs,
        OciUploadStore sessions, OciUploadStore.Ticket session, string id, string repository, string digest)
    {
        await session.Gate.WaitAsync(ctx.RequestAborted);
        try
        {
            if (!sessions.IsCurrent(repository, id, session))
                return Error(404, "BLOB_UPLOAD_UNKNOWN", "Upload session is no longer active");
            await Append(ctx, cfg, sessions, session);
            var key = BlobKey(digest);
            // Close the source before deleting staging files (also required on Windows).
            await using (var source = File.OpenRead(session.Path))
            {
                var actual = "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(source, ctx.RequestAborted));
                if (actual != digest) return Error(400, "DIGEST_INVALID", "SHA256 does not match");
                source.Position = 0;
                db.BeginPublication(key, "oci-blob", repository, digest);
                if (!await blobs.PutAsync(key, source, cfg.MaxArtifactBytes, ctx.RequestAborted))
                {
                    var existing = blobs.Existing(key);
                    if (existing is null) return Error(500, "BLOB_UPLOAD_INVALID", "Blob publication failed");
                    if (!await DigestMatches(existing, digest, ctx.RequestAborted))
                        return Error(503, "DIGEST_INVALID", "Existing CAS blob corrupted");
                }
            }
            db.OciAddBlob(repository, digest);
            db.CompletePublication(key);
            sessions.EndLocked(id, session);
            ctx.Response.Headers.Location = cfg.Base + "/v2/" + repository + "/blobs/" + digest;
            ctx.Response.Headers["Docker-Content-Digest"] = digest;
            return Results.StatusCode(201);
        }
        catch (OciChunkRangeException)
        {
            SetUploadHeaders(ctx, cfg, repository, id, session.CommittedLength);
            return Error(416, "BLOB_UPLOAD_INVALID", "Chunk range or length does not match committed upload offset");
        }
        finally { session.Gate.Release(); }
    }

    private static async Task<bool> DigestMatches(string path, string digest, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        return "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct)) == digest;
    }

    private static void SetUploadHeaders(HttpContext ctx, Settings cfg, string repository, string id, long offset)
    {
        ctx.Response.Headers.Location = cfg.Base + "/v2/" + repository + "/blobs/uploads/" + id;
        ctx.Response.Headers["Docker-Upload-UUID"] = id;
        ctx.Response.Headers.Range = "0-" + Math.Max(0, offset - 1);
    }

    private static async Task<IResult> PutManifest(HttpContext ctx, Settings cfg, IPackageDatabase db,
        BlobStore blobs, ArtifactLocks locks, string repository, string reference)
    {
        var media = ctx.Request.ContentType?.Split(';', 2)[0].Trim() ?? "";
        if (!AllowedMedia.Contains(media)) return Error(415, "MANIFEST_INVALID", "Unsupported manifest media type");
        var bytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body, cfg.MaxMetadataBytes, ctx.RequestAborted);
        JsonDocument manifest;
        try { manifest = JsonDocument.Parse(bytes); }
        catch (JsonException) { return Error(400, "MANIFEST_INVALID", "Manifest is not JSON"); }
        using (manifest)
        {
            var root = manifest.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 2 ||
                (root.TryGetProperty("mediaType", out var declared) &&
                 (declared.ValueKind != JsonValueKind.String || declared.GetString() != media)))
                return Error(400, "MANIFEST_INVALID", "Unsupported OCI schema or media type");
            if (!ValidateDescriptors(root, repository, db))
                return Error(400, "MANIFEST_BLOB_UNKNOWN", "Manifest references invalid or unknown content");
            string? subject = null;
            if (root.TryGetProperty("subject", out var subjectNode))
            {
                if (subjectNode.ValueKind != JsonValueKind.Object ||
                    !subjectNode.TryGetProperty("digest", out var subjectDigest) ||
                    subjectDigest.ValueKind != JsonValueKind.String ||
                    !ValidDigest(subjectDigest.GetString() ?? ""))
                    return Error(400, "DIGEST_INVALID", "Invalid subject digest");
                subject = subjectDigest.GetString();
            }
            if (root.TryGetProperty("artifactType", out var artifact) && artifact.ValueKind != JsonValueKind.String)
                return Error(400, "MANIFEST_INVALID", "Invalid artifact type");
            var artifactType = artifact.ValueKind == JsonValueKind.String ? artifact.GetString() : null;
            if (artifactType is null && root.TryGetProperty("config", out var config) &&
                config.ValueKind == JsonValueKind.Object && config.TryGetProperty("mediaType", out var configMedia) &&
                configMedia.ValueKind == JsonValueKind.String) artifactType = configMedia.GetString();
            var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (reference.StartsWith("sha256:", StringComparison.Ordinal) && reference != digest)
                return Error(400, "DIGEST_INVALID", "Manifest reference digest mismatch");
            using var lease = await locks.AcquireAsync("oci", repository, reference, ctx.RequestAborted);
            var key = ManifestKey(digest);
            db.BeginPublication(key, "oci-manifest", repository, digest);
            if (!await blobs.PutAsync(key, new MemoryStream(bytes, false), cfg.MaxMetadataBytes, ctx.RequestAborted))
            {
                var existing = blobs.Existing(key);
                if (existing is null || !await DigestMatches(existing, digest, ctx.RequestAborted))
                    return Error(503, "MANIFEST_INVALID", "Content-addressed manifest collision");
            }
            if (!db.OciPutManifest(repository, reference,
                    new OciManifestEntry(digest, media, bytes.Length), subject, artifactType, Access.Actor(ctx)))
                return Error(409, "MANIFEST_INVALID", "Manifest metadata publication conflict");
            db.CompletePublication(key);
            ctx.Response.Headers.Location = cfg.Base + "/v2/" + repository + "/manifests/" + digest;
            ctx.Response.Headers["Docker-Content-Digest"] = digest;
            if (subject is not null) ctx.Response.Headers["OCI-Subject"] = subject;
            return Results.StatusCode(201);
        }
    }

    private static bool ValidateDescriptors(JsonElement root, string repository, IPackageDatabase db)
    {
        foreach (var property in new[] { "config", "layers", "blobs", "manifests" })
        {
            if (!root.TryGetProperty(property, out var element)) continue;
            if (property != "config" && element.ValueKind != JsonValueKind.Array) return false;
            var descriptors = property == "config" ? new[] { element } : element.EnumerateArray().ToArray();
            foreach (var descriptor in descriptors)
            {
                if (descriptor.ValueKind != JsonValueKind.Object ||
                    !descriptor.TryGetProperty("digest", out var dig) || dig.ValueKind != JsonValueKind.String)
                    return false;
                var digest = dig.GetString() ?? "";
                if (!ValidDigest(digest)) return false;
                if (property == "manifests")
                {
                    if (db.OciManifest(repository, digest) is null) return false;
                }
                else if (!db.OciHasBlob(repository, digest)) return false;
            }
        }
        return true;
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { errors = new[] { new { code, message } } }, statusCode: status);
}
