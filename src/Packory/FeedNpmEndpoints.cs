using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Packory;

/// <summary>
/// Native hosted npm publishing and consumption within one explicitly authorized
/// private feed. Each version's manifest, tags and unchanged tarball are committed
/// together. These routes never consult a global registry or an upstream.
/// </summary>
public static partial class FeedNpmEndpoints
{
    public const string MediaType = "application/vnd.packory.npm-package+zip";
    public const long MaxUncompressedBytes = 128L * 1024 * 1024;
    private const int MaxArchiveEntries = 16384;

    [GeneratedRegex("\\A(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SemverRegex();

    public static bool ValidVersion(string? version)
    {
        // npm's semver.clean publication identity excludes build metadata.
        // Accept only that canonical identity in URL/ledger/manifest fields.
        if (version is null || version.Length is < 5 or > 128 || version.Contains('+')) return false;
        var match = SemverRegex().Match(version);
        return match.Success && Enumerable.Range(1, 3).All(i =>
            long.TryParse(match.Groups[i].Value, out var part) && part <= 9007199254740991);
    }

    private static string? ArchiveVersion(string? version)
    {
        if (version is null || version.Length > 128) return null;
        var cleaned = version.Trim().TrimStart('v', '=');
        if (!SemverRegex().IsMatch(cleaned)) return null;
        var canonical = cleaned.Split('+', 2)[0];
        return ValidVersion(canonical) ? canonical : null;
    }

    public static string PackagePath(string name, string version) => name + "/" + version;

    public static bool ValidAsset(FeedAssetEntry item) =>
        item.Kind == "npm" && Names.Npm(item.Name) && ValidVersion(item.Version) &&
        item.Path == PackagePath(item.Name, item.Version) && item.MediaType == MediaType;

    private static bool Active(IPackageDatabase db, string feedId) =>
        db.Feed(feedId) is
        { Kind: "npm", Mode: "hosted", Visibility: "private", Enabled: true };

    private static string? DecodePath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 1024) return null;
        var decoded = Uri.UnescapeDataString(value);
        return decoded.Contains('%') || decoded.Contains('\\') ||
            decoded.Contains('?') || decoded.Contains('#') || decoded.Any(char.IsControl)
            ? null : decoded;
    }

    public static string TarballUrl(Settings cfg, string feedId, string name, string version) =>
        cfg.Base + "/feeds/" + feedId + "/npm/" + Names.EscapedNpm(name) +
        "/-/" + Uri.EscapeDataString(version) + ".tgz";

    public static void Map(WebApplication app)
    {
        app.MapPut("/feeds/{feedId}/npm/{**resource}", async (
            string feedId, string? resource, Settings cfg, IPackageDatabase db,
            BlobStore blobs, ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId, publish: true) is { } denied)
                return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, no-store";
            var name = DecodePath(resource);
            if (!Names.Npm(name)) return Results.BadRequest(new { error = "Invalid npm package name" });
            if (cfg.DeniesNpm(name!)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!ctx.Request.HasJsonContentType())
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

            JsonObject publish;
            try
            {
                var bytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body,
                    cfg.MaxArtifactBytes * 2 + cfg.MaxMetadataBytes, ctx.RequestAborted);
                using var document = JsonDocument.Parse(bytes,
                    new JsonDocumentOptions { MaxDepth = 64 });
                if (!UniqueProperties(document.RootElement) ||
                    JsonNode.Parse(bytes) is not JsonObject root)
                    return Results.BadRequest(new { error = "Invalid or ambiguous npm publish document" });
                publish = root;
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid npm JSON" }); }
            catch (PayloadTooLargeException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }

            if (Text(publish["name"]) != name || publish["versions"] is not JsonObject versions ||
                versions.Count != 1 || publish["_attachments"] is not JsonObject attachments ||
                attachments.Count != 1)
                return Results.BadRequest(new { error = "Require one version and one matching tarball" });
            var (version, node) = versions.First();
            if (!ValidVersion(version) || node is not JsonObject manifest ||
                Text(manifest["name"]) != name || Text(manifest["version"]) != version ||
                !RegistryDependenciesOnly(manifest) ||
                attachments.First().Value is not JsonObject attachment ||
                Text(attachment["data"]) is not { } encoded)
                return Results.BadRequest(new { error = "Invalid npm package identity or attachment" });
            if (encoded.Length > (cfg.MaxArtifactBytes + 2) / 3 * 4)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            byte[] tarball;
            try { tarball = Convert.FromBase64String(encoded); }
            catch (FormatException) { return Results.BadRequest(new { error = "Invalid npm base64 attachment" }); }
            if (tarball.Length > cfg.MaxArtifactBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            if (attachment["length"] is not JsonValue length ||
                !length.TryGetValue<long>(out var declaredLength) || declaredLength != tarball.LongLength ||
                !ValidDeclaredDigests(manifest, tarball))
                return Results.BadRequest(new { error = "npm attachment length or digest mismatch" });
            try { await ValidateArchive(tarball, name!, version, cfg.MaxMetadataBytes, ctx.RequestAborted); }
            catch (PayloadTooLargeException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            catch (Exception error) when (error is InvalidDataException or IOException or JsonException or ArgumentException)
            { return Results.BadRequest(new { error = "Invalid npm archive or package identity" }); }

            var tags = new JsonObject();
            if (publish["dist-tags"] is not JsonObject submittedTags || submittedTags.Count == 0)
                return Results.BadRequest(new { error = "Require npm publication dist tags" });
            foreach (var tag in submittedTags)
            {
                if (!ValidTag(tag.Key) || Text(tag.Value) != version)
                    return Results.BadRequest(new { error = "Invalid npm dist tag target" });
                tags[tag.Key] = version;
            }
            manifest["dist"] = new JsonObject
            {
                ["tarball"] = TarballUrl(cfg, feedId, name!, version),
                ["shasum"] = Convert.ToHexStringLower(SHA1.HashData(tarball)),
                ["integrity"] = "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball))
            };
            var stored = new JsonObject { ["manifest"] = manifest.DeepClone(), ["tags"] = tags };
            var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(stored);
            if (metadataBytes.Length > cfg.MaxMetadataBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            using var metadata = JsonDocument.Parse(metadataBytes);
            try
            {
                if (!await FeedPackageStore.PublishAsync(feedId, "npm", PackagePath(name!, version),
                    name!, version, metadata.RootElement, tarball, db, blobs, locks,
                    cfg.MaxArtifactBytes, Access.Actor(ctx), ctx.RequestAborted))
                    return Results.Conflict(new { error = "Immutable npm version already exists" });
            }
            catch (PayloadTooLargeException) { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            catch (StorageQuotaExceededException) { return Results.StatusCode(507); }
            return Results.Json(new { ok = true, id = name, version },
                statusCode: StatusCodes.Status201Created);
        }).RequireRateLimiting("publish");

        app.MapMethods("/feeds/{feedId}/npm/{**resource}", ["GET", "HEAD"], async (
            string feedId, string? resource, Settings cfg, IPackageDatabase db,
            BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var path = DecodePath(resource);
            if (path is null) return Results.BadRequest();
            if (path == "-/ping") return Results.Json(new { ok = true });
            if (path == "-/whoami") return Results.Json(new { username = "packory" });
            try
            {
                var separator = path.IndexOf("/-/", StringComparison.Ordinal);
                if (separator >= 0)
                {
                    var name = path[..separator];
                    var file = path[(separator + 3)..];
                    if (!Names.Npm(name) || !file.EndsWith(".tgz", StringComparison.Ordinal) ||
                        !ValidVersion(file[..^4])) return Results.BadRequest();
                    if (cfg.DeniesNpm(name)) return Results.StatusCode(StatusCodes.Status403Forbidden);
                    var version = file[..^4];
                    var package = await FeedPackageStore.ReadAsync(feedId, "npm", PackagePath(name, version),
                        db, blobs, cfg.MaxArtifactBytes, ctx.RequestAborted);
                    if (package is null) return Results.NotFound();
                    ValidateStoredMetadata(package.Metadata, package.Payload, name, version);
                    var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(package.Payload)) + "\"";
                    ctx.Response.Headers.ETag = etag;
                    if (ctx.Request.Headers.IfNoneMatch.ToString() == etag)
                        return Results.StatusCode(StatusCodes.Status304NotModified);
                    return Results.File(package.Payload, "application/octet-stream", enableRangeProcessing: true);
                }
                var packageName = path;
                string? requestedVersion = null;
                if (!Names.Npm(packageName))
                {
                    var cut = path.LastIndexOf('/');
                    if (cut < 1) return Results.BadRequest();
                    packageName = path[..cut];
                    requestedVersion = path[(cut + 1)..];
                    if (!Names.Npm(packageName) || (!ValidVersion(requestedVersion) && !ValidTag(requestedVersion)))
                        return Results.BadRequest();
                }
                if (cfg.DeniesNpm(packageName)) return Results.StatusCode(StatusCodes.Status403Forbidden);
                var entries = db.FeedPackageAssets(feedId, "npm", packageName);
                if (entries.Count == 0) return Results.NotFound();
                var versions = new JsonObject();
                var tags = new JsonObject();
                long totalMetadata = 0;
                foreach (var entry in entries.OrderBy(x => x.CreatedAt, StringComparer.Ordinal)
                    .ThenBy(x => x.Version, StringComparer.Ordinal))
                {
                    var package = await FeedPackageStore.ReadAsync(feedId, "npm", entry.Path,
                        db, blobs, cfg.MaxArtifactBytes, ctx.RequestAborted)
                        ?? throw new InvalidDataException("Private npm revision disappeared.");
                    ValidateStoredMetadata(package.Metadata, package.Payload, packageName, entry.Version);
                    totalMetadata += Encoding.UTF8.GetByteCount(package.Metadata.GetRawText());
                    if (totalMetadata > cfg.MaxMetadataBytes)
                        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    var manifest = JsonNode.Parse(package.Metadata.GetProperty("manifest").GetRawText())!.AsObject();
                    manifest["dist"]!["tarball"] = TarballUrl(cfg, feedId, packageName, entry.Version);
                    versions[entry.Version] = manifest;
                    foreach (var tag in package.Metadata.GetProperty("tags").EnumerateObject())
                        tags[tag.Name] = tag.Value.GetString();
                }
                if (requestedVersion is not null)
                {
                    var selected = ValidVersion(requestedVersion)
                        ? requestedVersion : Text(tags[requestedVersion]);
                    return selected is not null && versions[selected] is { } manifest
                        ? Results.Json(manifest) : Results.NotFound();
                }
                return Results.Json(new JsonObject
                {
                    ["name"] = packageName, ["versions"] = versions, ["dist-tags"] = tags
                });
            }
            catch (Exception error) when (error is InvalidDataException or IOException or JsonException)
            { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        });
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool ValidTag(string tag) => tag.Length is >= 1 and <= 64 &&
        char.IsAsciiLetter(tag[0]) && tag.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    // A private hosted profile must not turn a normal install into a direct
    // git/HTTP/file fetch outside this feed. Version ranges, dist tags and npm
    // aliases remain registry resolved. Arbitrary external dependency origins
    // require a separately designed, explicit origin policy.
    private static bool RegistryDependenciesOnly(JsonObject manifest)
    {
        foreach (var key in new[] { "dependencies", "optionalDependencies", "peerDependencies", "devDependencies" })
        {
            if (!manifest.ContainsKey(key)) continue;
            if (manifest[key] is not JsonObject dependencies || dependencies.Count > 1024) return false;
            foreach (var dependency in dependencies)
            {
                if (!Names.Npm(dependency.Key) || Text(dependency.Value) is not { } specification ||
                    specification.Length > 512) return false;
                if (specification.StartsWith("npm:", StringComparison.Ordinal))
                {
                    var alias = specification[4..];
                    var split = alias.LastIndexOf('@');
                    if (split <= 0 || !Names.Npm(alias[..split])) return false;
                    specification = alias[(split + 1)..];
                }
                if (specification.Length == 0 || specification[0] == '.' ||
                    specification.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
                    specification.Any(c => !char.IsAsciiLetterOrDigit(c) &&
                        c is not (' ' or '.' or '-' or '+' or '_' or '^' or '~' or '<' or '>' or '=' or '|' or '*')))
                    return false;
            }
        }
        return true;
    }

    private static bool ValidDeclaredDigests(JsonObject manifest, byte[] tarball)
    {
        if (manifest["dist"] is not JsonObject dist) return false;
        var integrity = Text(dist["integrity"]);
        var checksum = Text(dist["shasum"]);
        if (integrity is null && checksum is null) return false;
        return (!dist.ContainsKey("integrity") || integrity == "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball))) &&
            (!dist.ContainsKey("shasum") || string.Equals(checksum,
                Convert.ToHexStringLower(SHA1.HashData(tarball)), StringComparison.OrdinalIgnoreCase));
    }

    private static void ValidateStoredMetadata(JsonElement metadata, byte[] tarball, string name, string version)
    {
        if (!UniqueProperties(metadata) || metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("manifest", out var manifest) ||
            manifest.ValueKind != JsonValueKind.Object ||
            !manifest.TryGetProperty("name", out var packageName) || packageName.ValueKind != JsonValueKind.String ||
            packageName.GetString() != name ||
            !manifest.TryGetProperty("version", out var release) || release.ValueKind != JsonValueKind.String ||
            release.GetString() != version ||
            !metadata.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object ||
            tags.EnumerateObject().Any(t => !ValidTag(t.Name) || t.Value.ValueKind != JsonValueKind.String ||
                t.Value.GetString() != version) ||
            NpmTarballIntegrity.ParseExpected(manifest.GetRawText(), name, version, false) is not { Algorithm: "sha512" } expected ||
            !CryptographicOperations.FixedTimeEquals(expected.Bytes, SHA512.HashData(tarball)))
            throw new InvalidDataException("Private npm manifest failed integrity validation.");
    }

    private static bool UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().All(UniqueProperties);
        if (value.ValueKind != JsonValueKind.Object) return true;
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(p => names.Add(p.Name) && UniqueProperties(p.Value));
    }

    private static async Task ValidateArchive(byte[] tarball, string name, string version,
        long maxMetadataBytes, CancellationToken ct)
    {
        using var compressed = new MemoryStream(tarball, writable: false);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        var expanded = await BlobStore.ReadBoundedAsync(gzip, MaxUncompressedBytes, ct);
        using var tar = new MemoryStream(expanded, writable: false);
        using var reader = new TarReader(tar);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var buffer = new byte[81920];
        var count = 0;
        var hasManifest = false;
        while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry)
        {
            if (++count > MaxArchiveEntries) throw new InvalidDataException("Too many npm archive entries.");
            var path = entry.Name;
            if (!path.StartsWith("package/", StringComparison.Ordinal) || path.Length > 1024 ||
                path.Contains('\\') || path.Contains(':') || path.Contains("//", StringComparison.Ordinal) || path.Any(char.IsControl) ||
                path.Split('/').Any(x => x is "." or "..") || !paths.Add(path.TrimEnd('/')) ||
                entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory))
                throw new InvalidDataException("Unsafe npm archive path or entry type.");
            if (entry.EntryType == TarEntryType.Directory)
            {
                if (entry.Length != 0) throw new InvalidDataException("npm directory has file data.");
                continue;
            }
            if (path.EndsWith('/') || entry.Length < 0 || entry.Length > expanded.LongLength ||
                (entry.Length > 0 && entry.DataStream is null))
                throw new InvalidDataException("Invalid npm archive length.");
            if (path != "package/package.json")
            {
                long consumed = 0;
                if (entry.DataStream is { } content)
                    while (true)
                    {
                        var read = await content.ReadAsync(buffer, ct);
                        if (read == 0) break;
                        consumed += read;
                    }
                if (consumed != entry.Length) throw new InvalidDataException("Truncated npm archive entry.");
                continue;
            }
            if (entry.DataStream is null || entry.Length > maxMetadataBytes)
                throw new InvalidDataException("Invalid npm package manifest.");
            var manifestBytes = await BlobStore.ReadBoundedAsync(entry.DataStream, maxMetadataBytes, ct);
            if (manifestBytes.LongLength != entry.Length) throw new InvalidDataException("Truncated npm package manifest.");
            using var manifest = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = manifest.RootElement;
            if (!UniqueProperties(root) || root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("name", out var packageName) || packageName.ValueKind != JsonValueKind.String ||
                packageName.GetString() != name ||
                !root.TryGetProperty("version", out var release) || release.ValueKind != JsonValueKind.String ||
                ArchiveVersion(release.GetString()) != version ||
                !RegistryDependenciesOnly(JsonNode.Parse(root.GetRawText())!.AsObject()))
                throw new InvalidDataException("npm archive identity differs from publication metadata.");
            hasManifest = true;
        }
        if (!hasManifest) throw new InvalidDataException("npm package manifest is missing.");
    }
}
