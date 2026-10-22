using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Packory;

/// <summary>
/// Authenticated hosted Cargo sparse registries. Each immutable index record and
/// crate share one verified feed-qualified bundle; no global or upstream lookup.
/// </summary>
public static class FeedCargoEndpoints
{
    private static readonly Regex VersionPattern = new(
        @"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsVersion(string? version) =>
        version is { Length: > 0 and <= 128 } && VersionPattern.IsMatch(version);

    // Cargo index uniqueness ignores SemVer build metadata.
    public static string AssetPath(string name, string version) =>
        name + "/" + version.Split('+', 2)[0];

    public static bool ValidAsset(FeedAssetEntry item) =>
        item.Kind == "cargo" && CargoEndpoints.IsName(item.Name) &&
        IsVersion(item.Version) && item.Path == AssetPath(item.Name, item.Version) &&
        item.MediaType == "application/vnd.packory.cargo-package+zip";

    private static bool Active(IPackageDatabase db, string id) =>
        db.Feed(id) is { Kind: "cargo", Mode: "hosted", Visibility: "private", Enabled: true };

    private static string Base(Settings cfg, string feedId) => cfg.Base + "/feeds/" + feedId + "/cargo";

    public static void Map(WebApplication app)
    {
        app.MapGet("/feeds/{feedId}/cargo/index/config.json", (
            string feedId, Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, no-store";
            return Results.Json(new Dictionary<string, object>
            {
                ["dl"] = Base(cfg, feedId) + "/api/v1/crates",
                ["api"] = Base(cfg, feedId), ["auth-required"] = true
            });
        });

        app.MapMethods("/feeds/{feedId}/cargo/index/{**path}", ["GET", "HEAD"], async (
            string feedId, string? path, Settings cfg, IPackageDatabase db, BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            if (string.IsNullOrEmpty(path)) return Results.NotFound();
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (!CargoEndpoints.IsName(name) || path != CargoEndpoints.IndexPath(name)) return Results.NotFound();
            var entries = db.FeedPackageAssets(feedId, "cargo", name);
            if (entries.Count == 0) return Results.NotFound();
            try
            {
                var rows = new List<string>();
                foreach (var entry in entries)
                {
                    var package = await FeedPackageStore.ReadAsync(feedId, "cargo", entry.Path,
                        db, blobs, cfg.MaxArtifactBytes, ctx.RequestAborted);
                    if (package is null || !ValidMetadata(package.Metadata, name, entry.Version, package.Payload))
                        return Results.StatusCode(503);
                    // The immutable bundle is never rewritten for yank/unyank.
                    // Overlay only the exact feed-local mutable index flag.
                    var overrideYanked = db.FeedCargoYanked(feedId, name, entry.Version);
                    if (overrideYanked is { } state)
                    {
                        var row = JsonNode.Parse(package.Metadata.GetRawText())?.AsObject()
                            ?? throw new InvalidDataException("Invalid private Cargo index JSON");
                        row["yanked"] = state;
                        rows.Add(row.ToJsonString());
                    }
                    else rows.Add(package.Metadata.GetRawText());
                }
                var data = string.Join("\n", rows) + "\n";
                var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data))) + "\"";
                ctx.Response.Headers.CacheControl = "private, no-store";
                ctx.Response.Headers.ETag = etag;
                if (ctx.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(304);
                return Results.Text(data, "text/plain; charset=utf-8");
            }
            catch (InvalidDataException) { return Results.StatusCode(503); }
        });

        app.MapMethods("/feeds/{feedId}/cargo/api/v1/crates/{name}/{version}/download", ["GET", "HEAD"], async (
            string feedId, string name, string version, Settings cfg, IPackageDatabase db,
            BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            if (!CargoEndpoints.IsName(name) || !IsVersion(version)) return Results.NotFound();
            var record = db.GetFeedAsset(feedId, "cargo", AssetPath(name, version));
            if (record is null || record.Version != version) return Results.NotFound();
            try
            {
                var package = await FeedPackageStore.ReadAsync(feedId, "cargo", AssetPath(name, version),
                    db, blobs, cfg.MaxArtifactBytes, ctx.RequestAborted);
                if (package is null) return Results.NotFound();
                if (!ValidMetadata(package.Metadata, name, version, package.Payload)) return Results.StatusCode(503);
                ctx.Response.Headers.CacheControl = "private, no-store";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ctx.Response.Headers.ETag = "\"" + package.Metadata.GetProperty("cksum").GetString() + "\"";
                return Results.Bytes(package.Payload, "application/octet-stream", enableRangeProcessing: true);
            }
            catch (InvalidDataException) { return Results.StatusCode(503); }
        });

        app.MapPut("/feeds/{feedId}/cargo/api/v1/crates/new", async (
            string feedId, Settings cfg, IPackageDatabase db, BlobStore blobs,
            ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId, publish: true) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            byte[] data;
            try
            {
                data = await BlobStore.ReadBoundedAsync(ctx.Request.Body,
                    cfg.MaxArtifactBytes + Math.Min(cfg.MaxMetadataBytes, 1024 * 1024) + 8L, ctx.RequestAborted);
            }
            catch (PayloadTooLargeException) { return Results.StatusCode(413); }
            if (data.Length < 8) return Results.BadRequest(new { error = "Truncated Cargo payload" });
            var metadataLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
            if (metadataLength == 0 || metadataLength > Math.Min(cfg.MaxMetadataBytes, 1024 * 1024) ||
                (long)metadataLength + 8 > data.Length) return Results.BadRequest(new { error = "Invalid Cargo metadata size" });
            var offset = 4 + (int)metadataLength;
            var crateLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            if (crateLength < 2 || crateLength > cfg.MaxArtifactBytes ||
                (long)offset + 4 + crateLength != data.Length) return Results.BadRequest(new { error = "Invalid Cargo archive size" });
            try
            {
                using var document = JsonDocument.Parse(data.AsMemory(4, (int)metadataLength),
                    new JsonDocumentOptions { MaxDepth = 32 });
                var metadata = document.RootElement;
                if (!UniqueObject(metadata)) return Results.BadRequest(new { error = "Invalid Cargo JSON fields" });
                var name = String(metadata, "name");
                var version = String(metadata, "vers");
                if (!CargoEndpoints.IsName(name) || !IsVersion(version)) return Results.BadRequest(new { error = "Invalid Cargo coordinates" });
                var archive = data.AsSpan(offset + 4, (int)crateLength).ToArray();
                if (!ValidateArchive(archive, name!, version!, cfg.MaxArtifactBytes))
                    return Results.BadRequest(new { error = "Invalid Cargo archive" });
                var index = BuildIndex(metadata, name!, version!, archive, Base(cfg, feedId) + "/index/");
                if (index is null) return Results.BadRequest(new { error = "Invalid or external Cargo dependency metadata" });
                var published = await FeedPackageStore.PublishAsync(feedId, "cargo", AssetPath(name!, version!),
                    name!, version!, JsonSerializer.SerializeToElement(index), archive,
                    db, blobs, locks, cfg.MaxArtifactBytes, Access.Actor(ctx), ctx.RequestAborted);
                if (!published) return Results.Conflict(new { error = "Immutable Cargo version exists" });
                ctx.Response.Headers.CacheControl = "private, no-store";
                return Results.Json(new { warnings = new { invalid_categories = Array.Empty<string>(),
                    invalid_badges = Array.Empty<string>(), other = Array.Empty<string>() } });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Invalid Cargo metadata" }); }
            catch (InvalidDataException) { return Results.BadRequest(new { error = "Invalid Cargo archive" }); }
            catch (PayloadTooLargeException) { return Results.StatusCode(413); }
        }).RequireRateLimiting("publish");

        app.MapDelete("/feeds/{feedId}/cargo/api/v1/crates/{name}/{version}/yank",
            async (string feedId, string name, string version, Settings cfg,
                IPackageDatabase db, ArtifactLocks locks, HttpContext ctx) =>
                await ChangeYank(feedId, name, version, true, cfg, db, locks, ctx))
            .RequireRateLimiting("publish");

        app.MapPut("/feeds/{feedId}/cargo/api/v1/crates/{name}/{version}/unyank",
            async (string feedId, string name, string version, Settings cfg,
                IPackageDatabase db, ArtifactLocks locks, HttpContext ctx) =>
                await ChangeYank(feedId, name, version, false, cfg, db, locks, ctx))
            .RequireRateLimiting("publish");
    }

    private static async Task<IResult> ChangeYank(string feedId, string name,
        string version, bool yanked, Settings cfg, IPackageDatabase db,
        ArtifactLocks locks, HttpContext ctx)
    {
        if (Access.RequireFeed(ctx, cfg, feedId, publish: true) is { } denied)
            return denied;
        if (!Active(db, feedId)) return Results.NotFound();
        if (!CargoEndpoints.IsName(name) || !IsVersion(version))
            return Results.BadRequest(new { error = "Invalid Cargo coordinates" });
        using var lease = await locks.AcquireAsync("feed-cargo-yank",
            feedId + "/" + name, AssetPath(name, version), ctx.RequestAborted);
        if (!db.SetFeedCargoYanked(feedId, name, version, yanked, Access.Actor(ctx)))
            return Results.NotFound(new { error = "Unknown private Cargo version" });
        ctx.Response.Headers.CacheControl = "private, no-store";
        return Results.Json(new { ok = true });
    }

    private static string? String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool UniqueObject(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count();

    private static bool Strings(JsonElement value) => value.ValueKind == JsonValueKind.Array &&
        value.GetArrayLength() <= 1024 && value.EnumerateArray().All(v =>
            v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= 1024);

    private static JsonObject? BuildIndex(JsonElement source, string name, string version, byte[] bytes, string indexUrl)
    {
        var dependencies = new JsonArray();
        if (!source.TryGetProperty("deps", out var deps) || deps.ValueKind != JsonValueKind.Array || deps.GetArrayLength() > 1024)
            return null;
        foreach (var dep in deps.EnumerateArray())
        {
            if (!UniqueObject(dep)) return null;
            var actual = String(dep, "name");
            var requirement = String(dep, "version_req");
            var alias = String(dep, "explicit_name_in_toml");
            var registry = String(dep, "registry");
            if (!CargoEndpoints.IsName(actual) || requirement is not { Length: > 0 and <= 512 } ||
                requirement.Any(char.IsControl) ||
                (alias is not null && !CargoEndpoints.IsName(alias)) ||
                (registry is not null && registry != indexUrl && registry != "sparse+" + indexUrl)) return null;
            foreach (var key in new[] { "explicit_name_in_toml", "registry", "target", "kind" })
                if (dep.TryGetProperty(key, out var field) && field.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) return null;
            if (!dep.TryGetProperty("features", out var features) || !Strings(features)) return null;
            if (!dep.TryGetProperty("optional", out var optional) || optional.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !dep.TryGetProperty("default_features", out var defaults) || defaults.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            var kind = String(dep, "kind");
            if (kind is not (null or "normal" or "dev" or "build")) return null;
            dependencies.Add(new JsonObject
            {
                ["name"] = alias ?? actual, ["req"] = requirement,
                ["features"] = JsonNode.Parse(features.GetRawText()), ["optional"] = optional.GetBoolean(),
                ["default_features"] = defaults.GetBoolean(), ["target"] = String(dep, "target"),
                ["kind"] = kind, ["registry"] = null, ["package"] = alias is null ? null : actual
            });
        }
        var featureMap = new JsonObject();
        if (source.TryGetProperty("features", out var featuresValue))
        {
            if (!UniqueObject(featuresValue)) return null;
            foreach (var feature in featuresValue.EnumerateObject())
            {
                if (feature.Name.Length > 128 || !Strings(feature.Value)) return null;
                featureMap.Add(feature.Name, JsonNode.Parse(feature.Value.GetRawText()));
            }
        }
        return new JsonObject
        {
            ["name"] = name, ["vers"] = version, ["deps"] = dependencies,
            ["cksum"] = Convert.ToHexStringLower(SHA256.HashData(bytes)), ["features"] = featureMap,
            ["yanked"] = false, ["links"] = String(source, "links"), ["rust_version"] = String(source, "rust_version")
        };
    }

    private static bool ValidMetadata(JsonElement value, string name, string version, byte[] payload) =>
        value.ValueKind == JsonValueKind.Object && String(value, "name") == name &&
        String(value, "vers") == version && String(value, "cksum") == Convert.ToHexStringLower(SHA256.HashData(payload));

    private static bool ValidateArchive(byte[] data, string name, string version, long maxBytes)
    {
        if (data.Length < 2 || data[0] != 0x1f || data[1] != 0x8b) return false;
        // Bound expansion before TarReader sees any attacker-declared entry size.
        using var gzip = new GZipStream(new MemoryStream(data, writable: false), CompressionMode.Decompress);
        using var expanded = new MemoryStream();
        var buffer = new byte[81920];
        var limit = Math.Min(maxBytes * 4, 128L * 1024 * 1024);
        int count;
        while ((count = gzip.Read(buffer)) != 0)
        {
            if (expanded.Length + count > limit) return false;
            expanded.Write(buffer, 0, count);
        }
        expanded.Position = 0;
        using var tar = new TarReader(expanded);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var prefix = name + "-" + version + "/";
        var manifest = false;
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            if (names.Count >= 10000 || !names.Add(entry.Name) || !entry.Name.StartsWith(prefix, StringComparison.Ordinal) ||
                entry.Name.Contains('\\') || entry.Name.Contains(':') || entry.Name.Any(char.IsControl) ||
                entry.Name.TrimEnd('/').Split('/').Any(p => p is "" or "." or "..") ||
                entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory)) return false;
            if (entry.Name == prefix + "Cargo.toml" && entry.Length is > 0 and <= 1024 * 1024) manifest = true;
        }
        return manifest;
    }
}
