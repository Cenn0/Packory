using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Packory;

/// <summary>
/// Private hosted Python Simple API and Twine upload. Metadata and original
/// distribution bytes are committed together in an immutable feed bundle.
/// </summary>
public static class FeedPyPiEndpoints
{
    private static readonly Regex VersionPattern = new(
        @"\A[0-9]+(?:\.[0-9]+)*(?:(?:a|b|rc)[0-9]+)?(?:\.post[0-9]+)?(?:\.dev[0-9]+)?(?:\+[a-z0-9]+(?:\.[a-z0-9]+)*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsVersion(string? version) =>
        version is { Length: > 0 and <= 128 } && VersionPattern.IsMatch(version);

    public static string AssetPath(string name, string filename) => name + "/" + filename;

    public static bool ValidAsset(FeedAssetEntry item)
    {
        if (item.Kind != "pypi" || PyPiEndpoints.Normalize(item.Name) != item.Name ||
            !IsVersion(item.Version) || item.MediaType != "application/vnd.packory.pypi-package+zip" ||
            !item.Path.StartsWith(item.Name + "/", StringComparison.Ordinal)) return false;
        return ValidFilename(item.Name, item.Version, item.Path[(item.Name.Length + 1)..]);
    }

    private static bool Active(IPackageDatabase db, string id) =>
        db.Feed(id) is { Kind: "pypi", Mode: "hosted", Visibility: "private", Enabled: true };

    public static void Map(WebApplication app)
    {
        app.MapMethods("/feeds/{feedId}/pypi/simple/", ["GET", "HEAD"], (
            string feedId, Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            var entries = db.FeedPackageAssets(feedId, "pypi");
            if (entries.Any(e => !FeedAssetEntry.Valid(e))) return Results.StatusCode(503);
            var names = entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            ctx.Response.Headers.CacheControl = "private, no-store";
            if (WantsJson(ctx)) return Results.Json(new
            {
                meta = new Dictionary<string, string> { ["api-version"] = "1.1" },
                projects = names.Select(name => new { name })
            }, contentType: "application/vnd.pypi.simple.v1+json");
            return Results.Text(Page(string.Join("", names.Select(name =>
                "<a href=\"" + WebUtility.HtmlEncode(name) + "/\">" + WebUtility.HtmlEncode(name) + "</a>\n"))),
                "application/vnd.pypi.simple.v1+html");
        });

        app.MapMethods("/feeds/{feedId}/pypi/simple/{name}/", ["GET", "HEAD"], async (
            string feedId, string name, Settings cfg, IPackageDatabase db, BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            var normalized = PyPiEndpoints.Normalize(name);
            if (normalized is null) return Results.NotFound();
            var entries = db.FeedPackageAssets(feedId, "pypi", normalized);
            if (entries.Count == 0) return Results.NotFound();
            var files = new List<Dictionary<string, object?>>();
            try
            {
                foreach (var entry in entries)
                {
                    var package = await FeedPackageStore.ReadAsync(feedId, "pypi", entry.Path,
                        db, blobs, cfg.MaxArtifactBytes, ctx.RequestAborted);
                    if (package is null || !ValidMetadata(package.Metadata, entry, package.Payload)) return Results.StatusCode(503);
                    var filename = package.Metadata.GetProperty("filename").GetString()!;
                    files.Add(new Dictionary<string, object?>
                    {
                        ["filename"] = filename,
                        ["url"] = FileUrl(cfg, feedId, normalized, filename),
                        ["hashes"] = new { sha256 = package.Metadata.GetProperty("sha256").GetString() },
                        ["size"] = package.Payload.LongLength,
                        ["requires-python"] = package.Metadata.GetProperty("requiresPython").GetString()
                    });
                }
            }
            catch (InvalidDataException) { return Results.StatusCode(503); }
            ctx.Response.Headers.CacheControl = "private, no-store";
            if (WantsJson(ctx)) return Results.Json(new
            {
                meta = new Dictionary<string, string> { ["api-version"] = "1.1" }, name = normalized,
                files, versions = entries.Select(e => e.Version).Distinct(StringComparer.Ordinal).ToArray()
            }, contentType: "application/vnd.pypi.simple.v1+json");
            var links = files.Select(file =>
            {
                var hash = JsonSerializer.SerializeToElement(file["hashes"]).GetProperty("sha256").GetString();
                var requirement = file["requires-python"] as string;
                return "<a href=\"" + WebUtility.HtmlEncode((string)file["url"]!) + "#sha256=" + hash + "\"" +
                    (requirement is null ? "" : " data-requires-python=\"" + WebUtility.HtmlEncode(requirement) + "\"") +
                    ">" + WebUtility.HtmlEncode((string)file["filename"]!) + "</a>\n";
            });
            return Results.Text(Page(string.Join("", links)), "application/vnd.pypi.simple.v1+html");
        });

        app.MapMethods("/feeds/{feedId}/pypi/files/{name}/{filename}", ["GET", "HEAD"], async (
            string feedId, string name, string filename, Settings cfg, IPackageDatabase db,
            BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            if (PyPiEndpoints.Normalize(name) != name || !PyPiEndpoints.IsFilename(filename)) return Results.NotFound();
            var path = AssetPath(name, filename);
            var entry = db.GetFeedAsset(feedId, "pypi", path);
            if (entry is null) return Results.NotFound();
            try
            {
                var package = await FeedPackageStore.ReadAsync(feedId, "pypi", path, db, blobs,
                    cfg.MaxArtifactBytes, ctx.RequestAborted);
                if (package is null || !ValidMetadata(package.Metadata, entry, package.Payload)) return Results.StatusCode(503);
                ctx.Response.Headers.CacheControl = "private, no-store";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                var etag = "\"" + package.Metadata.GetProperty("sha256").GetString() + "\"";
                ctx.Response.Headers.ETag = etag;
                if (ctx.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(304);
                return Results.Bytes(package.Payload, "application/octet-stream", enableRangeProcessing: true);
            }
            catch (InvalidDataException) { return Results.StatusCode(503); }
        });

        app.MapPost("/feeds/{feedId}/pypi/legacy/", async (
            string feedId, Settings cfg, IPackageDatabase db, BlobStore blobs,
            ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.RequireFeed(ctx, cfg, feedId, publish: true) is { } denied) return denied;
            if (!Active(db, feedId)) return Results.NotFound();
            if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart form" });
            try
            {
                var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
                // Repeated coordinate fields are ambiguous and are never concatenated.
                foreach (var field in new[] { "name", "version", ":action", "protocol_version", "sha256_digest", "requires_python" })
                    if (form[field].Count > 1) return Results.BadRequest(new { error = "Duplicate distribution field" });
                var name = PyPiEndpoints.Normalize(form["name"].ToString());
                var version = form["version"].ToString();
                if (name is null || !IsVersion(version) || form.Files.Count != 1 ||
                    form[":action"] != "file_upload" || form["protocol_version"] != "1")
                    return Results.BadRequest(new { error = "Invalid distribution coordinates or upload action" });
                var file = form.Files[0];
                if (file.Name != "content" || !ValidFilename(name, version, file.FileName))
                    return Results.BadRequest(new { error = "Invalid or mismatched distribution filename" });
                if (file.Length <= 0 || file.Length > cfg.MaxArtifactBytes) return Results.StatusCode(413);
                await using var stream = file.OpenReadStream();
                var bytes = await BlobStore.ReadBoundedAsync(stream, cfg.MaxArtifactBytes, ctx.RequestAborted);
                var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
                var claimed = form["sha256_digest"].ToString();
                if (!ArtifactDigests.ValidSha256(claimed.ToLowerInvariant()) || !string.Equals(claimed, sha, StringComparison.OrdinalIgnoreCase))
                    return Results.BadRequest(new { error = "Distribution SHA256 digest mismatch" });
                var core = InspectDistribution(bytes, file.FileName, name, version, cfg.MaxArtifactBytes);
                if (core is null) return Results.BadRequest(new { error = "Invalid distribution archive or core metadata" });
                var requiresPython = CoreValue(core, "Requires-Python");
                var declaredRequires = form["requires_python"].ToString();
                if (declaredRequires.Length > 0 && declaredRequires != requiresPython)
                    return Results.BadRequest(new { error = "Requires-Python metadata mismatch" });
                var metadata = JsonSerializer.SerializeToElement(new
                {
                    name, version, filename = file.FileName, sha256 = sha, size = bytes.LongLength, requiresPython
                });
                if (!await FeedPackageStore.PublishAsync(feedId, "pypi", AssetPath(name, file.FileName),
                    name, version, metadata, bytes, db, blobs, locks,
                    cfg.MaxArtifactBytes, Access.Actor(ctx), ctx.RequestAborted))
                    return Results.Conflict(new { error = "Immutable distribution filename exists" });
                ctx.Response.Headers.CacheControl = "private, no-store";
                return Results.Text("Distribution uploaded", "text/plain");
            }
            catch (InvalidDataException) { return Results.BadRequest(new { error = "Invalid distribution upload" }); }
            catch (DecoderFallbackException) { return Results.BadRequest(new { error = "Invalid distribution metadata encoding" }); }
            catch (PayloadTooLargeException) { return Results.StatusCode(413); }
        }).RequireRateLimiting("publish");
    }

    public static bool ValidFilename(string name, string version, string filename)
    {
        if (!PyPiEndpoints.IsFilename(filename)) return false;
        if (filename.EndsWith(".whl", StringComparison.Ordinal))
        {
            var parts = filename[..^4].Split('-');
            return parts.Length is 5 or 6 && PyPiEndpoints.Normalize(parts[0]) == name &&
                parts[1] == version && parts.Skip(2).All(p => p.Length > 0);
        }
        var stem = filename.EndsWith(".tar.gz", StringComparison.Ordinal) ? filename[..^7] : filename[..^4];
        return stem.EndsWith("-" + version, StringComparison.Ordinal) &&
            PyPiEndpoints.Normalize(stem[..^(version.Length + 1)]) == name;
    }

    private static bool WantsJson(HttpContext ctx) => ctx.Request.Headers.Accept.ToString()
        .Contains("application/vnd.pypi.simple.v1+json", StringComparison.OrdinalIgnoreCase);

    private static string Page(string body) => "<!DOCTYPE html><html><head><meta name=\"pypi:repository-version\" content=\"1.1\"></head><body>\n" + body + "</body></html>";

    private static string FileUrl(Settings cfg, string feed, string name, string filename) =>
        cfg.Base + "/feeds/" + feed + "/pypi/files/" + Uri.EscapeDataString(name) + "/" + Uri.EscapeDataString(filename);

    private static bool ValidMetadata(JsonElement value, FeedAssetEntry entry, byte[] payload) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty("name", out var name) && name.GetString() == entry.Name &&
        value.TryGetProperty("version", out var version) && version.GetString() == entry.Version &&
        value.TryGetProperty("filename", out var filename) && AssetPath(entry.Name, filename.GetString()!) == entry.Path &&
        value.TryGetProperty("sha256", out var hash) && hash.GetString() == Convert.ToHexStringLower(SHA256.HashData(payload)) &&
        value.TryGetProperty("size", out var size) && size.TryGetInt64(out var length) && length == payload.LongLength &&
        value.TryGetProperty("requiresPython", out var requirement) && requirement.ValueKind is JsonValueKind.Null or JsonValueKind.String;

    private static bool SafeMember(string path) => path.Length is > 0 and <= 1024 &&
        !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl) &&
        path.TrimEnd('/').Split('/').All(part => part.Length > 0 && part is not ("." or ".."));

    private static string? InspectDistribution(byte[] bytes, string filename, string name, string version, long maxBytes)
    {
        string? core = null;
        var limit = Math.Min(maxBytes * 4, 128L * 1024 * 1024);
        var utf8 = new UTF8Encoding(false, true);
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (filename.EndsWith(".tar.gz", StringComparison.Ordinal))
        {
            using var gzip = new GZipStream(new MemoryStream(bytes, writable: false), CompressionMode.Decompress);
            using var expanded = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = gzip.Read(buffer)) != 0)
            {
                if (expanded.Length + count > limit) return null;
                expanded.Write(buffer, 0, count);
            }
            expanded.Position = 0;
            using var tar = new TarReader(expanded);
            TarEntry? entry;
            while ((entry = tar.GetNextEntry()) is not null)
            {
                if (names.Count >= 10000 || !names.Add(entry.Name) || !SafeMember(entry.Name) ||
                    entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory)) return null;
                if (entry.Name.Split('/') is [var root, "PKG-INFO"] && RootMatches(root, name, version))
                {
                    if (core is not null || entry.DataStream is null || entry.Length is <= 0 or > 262144) return null;
                    core = ReadCore(entry.DataStream, utf8);
                }
            }
        }
        else
        {
            using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (names.Count >= 10000 || !names.Add(entry.FullName) || !SafeMember(entry.FullName) ||
                    ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || entry.Length > limit - total) return null;
                total += entry.Length;
                var pieces = entry.FullName.Split('/');
                var isCore = filename.EndsWith(".whl", StringComparison.Ordinal)
                    ? pieces is [var directory, "METADATA"] && directory.EndsWith(".dist-info", StringComparison.Ordinal) &&
                        RootMatches(directory[..^10], name, version)
                    : pieces is [var root, "PKG-INFO"] && RootMatches(root, name, version);
                if (!isCore) continue;
                if (core is not null || entry.Length is <= 0 or > 262144) return null;
                using var input = entry.Open();
                core = ReadCore(input, utf8);
            }
        }
        return core is not null && PrivateDependenciesOnly(core) &&
            PyPiEndpoints.Normalize(CoreValue(core, "Name")) == name &&
            CoreValue(core, "Version") == version ? core : null;
    }

    private static bool RootMatches(string root, string name, string version) =>
        root.EndsWith("-" + version, StringComparison.Ordinal) &&
        PyPiEndpoints.Normalize(root[..^(version.Length + 1)]) == name;

    private static string ReadCore(Stream input, Encoding encoding)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            if (memory.Length + count > 262144) throw new InvalidDataException("Distribution core metadata exceeds its limit.");
            memory.Write(buffer, 0, count);
        }
        return encoding.GetString(memory.ToArray());
    }

    private static string? CoreValue(string core, string key)
    {
        var headers = ReadCoreHeaders(core);
        if (headers is null) return null;
        var values = headers.Where(h => h.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
        return values.Length == 1 ? values[0].Value : null;
    }

    private static bool PrivateDependenciesOnly(string core)
    {
        var headers = ReadCoreHeaders(core);
        if (headers is null) return false;
        // pip follows PEP 508 direct references from wheel METADATA, bypassing
        // --index-url. A private hosted feed must not silently publish a URL
        // dependency which later makes a native consumer contact another origin.
        // This profile allows ordinary version/marker requirements only.
        return headers.Where(h => h.Key.Equals("Requires-Dist", StringComparison.OrdinalIgnoreCase))
            .All(h => !h.Value.Contains('@') && !h.Value.Contains("://", StringComparison.Ordinal));
    }

    private static List<KeyValuePair<string, string>>? ReadCoreHeaders(string core)
    {
        var normalized = core.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (normalized.Contains('\r')) return null;
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in normalized.Split("\n\n", 2)[0].Split('\n'))
        {
            if (line.Length == 0) break;
            if (line.Any(c => char.IsControl(c) && c != '\t')) return null;
            if (line[0] is ' ' or '\t')
            {
                if (headers.Count == 0) return null;
                var previous = headers[^1];
                headers[^1] = new(previous.Key, previous.Value + " " + line.Trim());
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0 || !line[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return null;
            headers.Add(new(line[..colon], line[(colon + 1)..].Trim()));
        }
        return headers;
    }
}
