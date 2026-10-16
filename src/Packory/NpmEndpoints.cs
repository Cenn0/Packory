using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Packory;

public static class NpmEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/npm/-/v1/search", (string? text, int? size, Settings settings, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.Require(ctx, settings) is { } denied) return denied;
            var found = db.Search(text ?? "", "npm", Math.Clamp(size ?? 20, 1, 100))
                .Where(e => !settings.DeniesNpm(e.Name)).ToList();
            return Results.Json(new { total = found.Count, objects = found.Select(e => new
            {
                package = new { name = e.Name, version = e.Version, description = e.Description }
            }) });
        });

        app.MapGet("/npm/{**resource}", async (string? resource, Settings settings, IPackageDatabase db, BlobStore blobs,
            IHttpClientFactory clients, ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.Require(ctx, settings) is { } denied) return denied;
            if (string.IsNullOrEmpty(resource)) return Results.NotFound();
            var path = Uri.UnescapeDataString(resource).Trim('/');
            if (path.Contains("/-/", StringComparison.Ordinal))
            {
                var parts = path.Split("/-/", 2, StringSplitOptions.None);
                var name = parts[0]; var tail = parts[1];
                if (!Names.Npm(name) || !tail.EndsWith(".tgz", StringComparison.Ordinal) ||
                    !Names.Version(tail[..^4])) return Results.NotFound();
                if (settings.DeniesNpm(name)) return Results.StatusCode(403);
                var version = tail[..^4];
                using var gate = await locks.AcquireAsync("npm", name, version, ctx.RequestAborted);
                var key = NpmKey(name, version);
                var origin = db.PackageOrigin("npm", name, version);
                if (blobs.Existing(key) is { } local)
                {
                    if (origin is not null) return Results.File(local, "application/octet-stream", enableRangeProcessing: true);
                    throw new InvalidDataException("Unindexed npm blob requires recovery");
                }
                if (origin == "hosted") throw new InvalidDataException("Hosted npm blob is missing");
                if (settings.IsReservedNpm(name)) return Results.NotFound();
                var raw = await FetchMetadata(name, settings, blobs, clients.CreateClient("upstream"), ctx.RequestAborted);
                if (raw is null) return Results.NotFound();
                var json = JsonNode.Parse(raw) as JsonObject;
                if (json?["versions"]?[version] is not JsonObject packageManifest ||
                    packageManifest["name"]?.ToString() != name ||
                    packageManifest["version"]?.ToString() != version ||
                    packageManifest["dist"] is not JsonObject dist ||
                    dist["tarball"]?.GetValue<string>() is not { } tarball)
                    return Results.NotFound();
                if (!Uri.TryCreate(tarball, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                    uri.Host != new Uri(settings.NpmUpstreamUrl).Host || uri.Port != 443 ||
                    !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                    return Results.StatusCode(502);
                using var response = await clients.CreateClient("upstream").GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
                if (response.StatusCode == HttpStatusCode.NotFound) return Results.NotFound();
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } contentLength && contentLength > settings.MaxArtifactBytes)
                    throw new PayloadTooLargeException();
                await using var body = await response.Content.ReadAsStreamAsync(ctx.RequestAborted);
                var bytes = await BlobStore.ReadBoundedAsync(body, settings.MaxArtifactBytes, ctx.RequestAborted);
                if (dist["integrity"]?.GetValue<string>() is { } integrity && integrity.StartsWith("sha512-", StringComparison.Ordinal))
                {
                    if (!string.Equals(integrity[7..], Convert.ToBase64String(SHA512.HashData(bytes)), StringComparison.Ordinal))
                        return Results.StatusCode(502);
                }
                else if (dist["shasum"]?.GetValue<string>() is { } checksum)
                {
                    if (!string.Equals(checksum, Convert.ToHexStringLower(SHA1.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
                        return Results.StatusCode(502);
                }
                else
                {
                    // No integrity claim from the upstream: do not publish unverified content.
                    return Results.StatusCode(502);
                }
                if (!await blobs.PutAsync(key, new MemoryStream(bytes, writable: false), settings.MaxArtifactBytes, ctx.RequestAborted))
                    throw new InvalidDataException("npm cache blob was concurrently replaced");
                var description = json["versions"]?[version]?["description"]?.ToString() ?? json["description"]?.ToString() ?? "";
                db.UpsertPackage("npm", name, version, description[..Math.Min(description.Length, 2000)], "mirror");
                return Results.File(blobs.Existing(key)!, "application/octet-stream", enableRangeProcessing: true);
            }
            // npm can request the full packument or one version's metadata.
            string nameOnly = path;
            string? requestedVersion = null;
            if (!Names.Npm(path))
            {
                var cut = path.LastIndexOf('/');
                if (cut < 0) return Results.NotFound();
                nameOnly = path[..cut]; requestedVersion = path[(cut + 1)..];
                if (!Names.Npm(nameOnly) || !Names.Version(requestedVersion)) return Results.NotFound();
            }
            if (settings.DeniesNpm(nameOnly)) return Results.StatusCode(403);
            var packument = await BuildPackument(nameOnly, settings, db, blobs, clients.CreateClient("upstream"), ctx.RequestAborted);
            if (packument is null) return Results.NotFound();
            if (requestedVersion is not null)
                return packument["versions"]?[requestedVersion] is { } selected ? Results.Json(selected) : Results.NotFound();
            return Results.Json(packument);
        });

        app.MapPut("/npm/{**packageName}", async (string? packageName, Settings settings, IPackageDatabase db,
            BlobStore blobs, ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.Require(ctx, settings, write: true) is { } denied) return denied;
            var name = Uri.UnescapeDataString(packageName ?? "");
            if (!Names.Npm(name)) return Results.BadRequest(new { error = "Invalid npm package name" });
            if (settings.DeniesNpm(name)) return Results.StatusCode(403);
            if (Access.RequireNamespace(ctx, settings, "npm", name) is { } forbidden) return forbidden;
            var data = await BlobStore.ReadBoundedAsync(ctx.Request.Body, settings.MaxArtifactBytes * 2, ctx.RequestAborted);
            JsonObject? publish;
            try { publish = JsonNode.Parse(data) as JsonObject; }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "Invalid JSON" }); }
            if (publish is null || publish["name"]?.ToString() != name || publish["versions"] is not JsonObject versions ||
                versions.Count != 1 || publish["_attachments"] is not JsonObject attachments || attachments.Count != 1)
                return Results.BadRequest(new { error = "Expected one version, one attachment, and matching package name" });
            var (version, value) = versions.First();
            if (!Names.Version(version) || value is not JsonObject manifest ||
                manifest["name"]?.ToString() != name || manifest["version"]?.ToString() != version)
                return Results.BadRequest(new { error = "Invalid package manifest" });
            using var gate = await locks.AcquireAsync("npm", name, version, ctx.RequestAborted);
            if (db.HasPackage("npm", name, version) || blobs.Existing(NpmKey(name, version)) is not null)
                return Results.Conflict(new { error = "Version already exists" });
            var attachment = attachments.First().Value as JsonObject;
            if (attachment?["data"]?.GetValue<string>() is not { } encoded) return Results.BadRequest();
            byte[] tarball;
            try { tarball = Convert.FromBase64String(encoded); }
            catch (FormatException) { return Results.BadRequest(new { error = "Invalid base64 attachment" }); }
            if (tarball.Length < 2 || tarball[0] != 0x1f || tarball[1] != 0x8b)
                return Results.BadRequest(new { error = "Expected gzipped npm tarball" });
            if (tarball.Length > settings.MaxArtifactBytes) return Results.StatusCode(413);
            var tags = new Dictionary<string, string>(StringComparer.Ordinal);
            if (publish["dist-tags"] is JsonObject distTags)
                foreach (var tag in distTags)
                {
                    if (tag.Key.Length is < 1 or > 64 || !tag.Key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
                        return Results.BadRequest(new { error = "Invalid dist tag" });
                    if (tag.Value?.ToString() == version) tags[tag.Key] = version;
                }
            manifest["dist"] = new JsonObject
            {
                ["tarball"] = TarballUrl(settings, name, version),
                ["shasum"] = Convert.ToHexStringLower(SHA1.HashData(tarball)),
                ["integrity"] = "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball))
            };
            var description = manifest["description"]?.ToString() ?? "";
            var key = NpmKey(name, version);
            db.BeginPublication(key, "npm", name, version);
            if (!await blobs.PutAsync(key, new MemoryStream(tarball, writable: false), settings.MaxArtifactBytes, ctx.RequestAborted))
                return Results.Conflict();
            if (!db.PutNpmVersion(name, version, manifest.ToJsonString(), description[..Math.Min(description.Length, 2000)], tags, Access.Actor(ctx)))
                return Results.Conflict();
            db.CompletePublication(key);
            return Results.Json(new { ok = true, id = name, version }, statusCode: StatusCodes.Status201Created);
        }).RequireRateLimiting("publish");
    }

    public static string NpmKey(string name, string version) => $"npm/{Names.HashName(name)}/{version}.tgz";

    public static string TarballUrl(Settings settings, string name, string version) =>
        settings.Base + "/npm/" + Names.EscapedNpm(name) + "/-/" + version + ".tgz";

    public static async Task<byte[]?> FetchMetadata(string name, Settings settings, BlobStore blobs, HttpClient http, CancellationToken ct)
    {
        if (settings.IsReservedNpm(name)) return null;
        var key = $"npm-metadata/{Names.HashName(name)}.json";
        var cached = blobs.Existing(key);
        var url = settings.NpmUpstreamUrl.TrimEnd('/') + "/" + Names.EscapedNpm(name);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return cached is not null ? await File.ReadAllBytesAsync(cached, ct) : null;
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length > settings.MaxMetadataBytes)
                throw new PayloadTooLargeException();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            var bytes = await BlobStore.ReadBoundedAsync(input, settings.MaxMetadataBytes, ct);
            // Validate the received document before making it part of the offline cache.
            if (JsonNode.Parse(bytes) is not JsonObject) throw new InvalidDataException("Invalid upstream npm metadata");
            await blobs.ReplaceAsync(key, new MemoryStream(bytes, false), settings.MaxMetadataBytes, ct);
            return bytes;
        }
        catch (HttpRequestException) when (cached is not null) { return await File.ReadAllBytesAsync(cached, ct); }
        catch (TaskCanceledException) when (cached is not null && !ct.IsCancellationRequested) { return await File.ReadAllBytesAsync(cached, ct); }
    }

    public static async Task<JsonObject?> BuildPackument(string name, Settings settings, IPackageDatabase db, BlobStore blobs, HttpClient http, CancellationToken ct)
    {
        var hosted = db.NpmHosted(name);
        byte[]? remote;
        try { remote = await FetchMetadata(name, settings, blobs, http, ct); }
        catch (HttpRequestException) when (hosted.Versions.Count > 0) { remote = null; }
        catch (TaskCanceledException) when (hosted.Versions.Count > 0 && !ct.IsCancellationRequested) { remote = null; }
        var doc = remote is null ? null : JsonNode.Parse(remote) as JsonObject;
        if (doc is null && hosted.Versions.Count == 0) return null;
        doc ??= new JsonObject { ["name"] = name, ["versions"] = new JsonObject(), ["dist-tags"] = new JsonObject() };
        if (doc["versions"] is not JsonObject versions) doc["versions"] = versions = new JsonObject();
        if (doc["dist-tags"] is not JsonObject tags) doc["dist-tags"] = tags = new JsonObject();
        foreach (var (version, json) in hosted.Versions) versions[version] = JsonNode.Parse(json);
        foreach (var (tag, version) in hosted.Tags) tags[tag] = version;
        // Rewrite all download URLs to Packory; clients must never bypass the artifact cache.
        foreach (var (version, node) in versions)
        {
            if (!Names.Version(version) || node is not JsonObject manifest) continue;
            if (manifest["dist"] is not JsonObject dist) manifest["dist"] = dist = new JsonObject();
            dist["tarball"] = TarballUrl(settings, name, version);
        }
        return doc;
    }
}
