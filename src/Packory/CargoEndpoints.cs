using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Packory;

/// <summary>
/// A private Cargo alternate registry backed by a native sparse index.
/// This is not a crates.io source-replacement mirror.
/// </summary>
public static class CargoEndpoints
{
    private static readonly Regex ValidName = new("^[a-z][a-z0-9_-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Semver = new(
        @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsName(string value) => value.Length <= 64 && ValidName.IsMatch(value);
    public static bool IsVersion(string value) => value.Length <= 128 && Semver.IsMatch(value);

    public static string IndexPath(string name) => name.Length switch
    {
        1 => "1/" + name,
        2 => "2/" + name,
        3 => "3/" + name[0] + "/" + name,
        _ => name[..2] + "/" + name.Substring(2, 2) + "/" + name
    };

    public static string Key(string name, string version) => "cargo/" + name + "/" + version + "/" + name + "-" + version + ".crate";

    public static void Map(WebApplication app)
    {
        app.MapGet("/cargo/index/config.json", (Settings cfg, HttpContext ctx) =>
        {
            if (Access.Require(ctx, cfg) is { } denied) return denied;
            return Results.Json(new Dictionary<string, object>
            {
                ["dl"] = cfg.Base + "/cargo/api/v1/crates",
                ["api"] = cfg.Base + "/cargo/api",
                ["auth-required"] = !cfg.AnonymousRead
            });
        });

        app.MapGet("/cargo/index/{**path}", (string? path, Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.Require(ctx, cfg) is { } denied) return denied;
            if (string.IsNullOrEmpty(path)) return Results.NotFound();
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (!IsName(name) || path != IndexPath(name)) return Results.NotFound();
            var versions = db.CargoIndex(name);
            if (versions.Count == 0) return Results.NotFound();
            var data = string.Join("\n", versions) + "\n";
            var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(data))) + "\"";
            ctx.Response.Headers.ETag = etag;
            ctx.Response.Headers.CacheControl = "no-cache";
            if (ctx.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(304);
            return Results.Text(data, "text/plain; charset=utf-8");
        });

        app.MapGet("/cargo/api/v1/crates/{name}/{version}/download", (string name, string version,
            Settings cfg, IPackageDatabase db, BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.Require(ctx, cfg) is { } denied) return denied;
            if (!IsName(name) || !IsVersion(version) || db.PackageOrigin("cargo", name, version) is null)
                return Results.NotFound();
            var path = blobs.Existing(Key(name, version));
            return path is null ? Results.NotFound() :
                Results.File(path, "application/octet-stream", enableRangeProcessing: true);
        });

        app.MapPut("/cargo/api/v1/crates/new", async (Settings cfg, IPackageDatabase db, BlobStore blobs,
            ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.Require(ctx, cfg, write: true) is { } denied) return denied;
            var data = await BlobStore.ReadBoundedAsync(ctx.Request.Body,
                cfg.MaxArtifactBytes + cfg.MaxMetadataBytes + 8L, ctx.RequestAborted);
            if (data.Length < 8) return Results.BadRequest(new { errors = new[] { new { detail = "Truncated Cargo publish payload" } } });
            var metadataLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
            if (metadataLength == 0 || metadataLength > cfg.MaxMetadataBytes ||
                (long)metadataLength + 8 > data.Length)
                return Results.BadRequest(new { error = "Invalid Cargo metadata size" });
            var offset = 4 + (int)metadataLength;
            var crateLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            if (crateLength < 2 || crateLength > cfg.MaxArtifactBytes ||
                (long)offset + 4 + crateLength != data.Length)
                return Results.BadRequest(new { error = "Invalid Cargo archive size" });
            JsonObject? metadata;
            try { metadata = JsonNode.Parse(data.AsSpan(4, (int)metadataLength)) as JsonObject; }
            catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "Invalid Cargo JSON" }); }
            if (metadata is null) return Results.BadRequest();
            var name = metadata["name"]?.ToString() ?? "";
            var version = metadata["vers"]?.ToString() ?? "";
            if (!IsName(name) || !IsVersion(version))
                return Results.BadRequest(new { error = "Invalid Cargo name or version" });
            var archive = data.AsSpan(offset + 4, (int)crateLength).ToArray();
            if (archive[0] != 0x1f || archive[1] != 0x8b)
                return Results.BadRequest(new { error = "Cargo archive must be gzip" });
            var checksum = Convert.ToHexStringLower(SHA256.HashData(archive));
            var dependencies = new JsonArray();
            if (metadata["deps"] is JsonArray rawDependencies)
                foreach (var item in rawDependencies)
                {
                    if (item is not JsonObject dep || dep["name"]?.ToString() is not { } depName ||
                        dep["version_req"]?.ToString() is not { } requirement)
                        return Results.BadRequest(new { error = "Invalid Cargo dependency metadata" });
                    var alias = dep["explicit_name_in_toml"]?.ToString();
                    dependencies.Add(new JsonObject
                    {
                        ["name"] = alias ?? depName,
                        ["req"] = requirement,
                        ["features"] = dep["features"]?.DeepClone() ?? new JsonArray(),
                        ["optional"] = dep["optional"]?.DeepClone() ?? JsonValue.Create(false),
                        ["default_features"] = dep["default_features"]?.DeepClone() ?? JsonValue.Create(true),
                        ["target"] = dep["target"]?.DeepClone(),
                        ["kind"] = dep["kind"]?.DeepClone(),
                        ["registry"] = dep["registry"]?.DeepClone(),
                        ["package"] = alias is null ? null : depName
                    });
                }
            var index = new JsonObject
            {
                ["name"] = name,
                ["vers"] = version,
                ["deps"] = dependencies,
                ["cksum"] = checksum,
                ["features"] = metadata["features"]?.DeepClone() ?? new JsonObject(),
                ["yanked"] = false,
                ["links"] = metadata["links"]?.DeepClone(),
                ["rust_version"] = metadata["rust_version"]?.DeepClone()
            };
            var key = Key(name, version);
            using var lease = await locks.AcquireAsync("cargo", name, version, ctx.RequestAborted);
            if (db.HasPackage("cargo", name, version) || blobs.Existing(key) is not null)
                return Results.Conflict(new { error = "Cargo version already exists" });
            if (!await blobs.PutAsync(key, new MemoryStream(archive, writable: false),
                cfg.MaxArtifactBytes, ctx.RequestAborted)) return Results.Conflict();
            var description = metadata["description"]?.ToString() ?? "";
            if (!db.PublishCargo(name, version, index.ToJsonString(),
                    description[..Math.Min(2000, description.Length)], Access.Actor(ctx)))
                return Results.Conflict();
            return Results.Json(new { warnings = new { invalid_categories = Array.Empty<string>(),
                invalid_badges = Array.Empty<string>(), other = Array.Empty<string>() } });
        }).RequireRateLimiting("publish");
    }
}
