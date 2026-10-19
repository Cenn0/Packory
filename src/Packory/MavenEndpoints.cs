using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Packory;

/// <summary>
/// Experimental global hosted Maven release artifact transport.
/// A native Maven deploy operation ALSO writes mutable repository metadata/checksums;
/// that write protocol is deliberately NOT advertised as supported here.
/// No upstream proxy, SNAPSHOT publication or per-feed isolation.
/// </summary>
public static class MavenEndpoints
{
    private static readonly HashSet<string> Extensions = new(StringComparer.Ordinal)
        { "jar", "pom", "module", "zip", "war", "aar", "tar.gz" };

    public static string Key(string path) => "maven/" + path;

    private static string? MediaType(string extension) => extension switch
    {
        "pom" => "application/xml",
        "module" => "application/json",
        "jar" or "war" or "aar" => "application/java-archive",
        "zip" => "application/zip",
        "tar.gz" => "application/gzip",
        _ => null
    };

    public static bool ValidAssetPath(string? path, out MavenCoordinates.Artifact? artifact)
    {
        artifact = null;
        return MavenCoordinates.TryParseArtifactPath(path, out artifact) &&
            artifact is not null && Extensions.Contains(artifact.Extension) &&
            !artifact.Version.EndsWith("-SNAPSHOT", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(MavenCoordinates.RepositoryPath(artifact), path, StringComparison.Ordinal);
    }

    public static bool ValidRecord(MavenAssetEntry asset) =>
        ValidAssetPath(asset.Path, out var parsed) && parsed is not null &&
        string.Equals(parsed.GroupId, asset.GroupId, StringComparison.Ordinal) &&
        string.Equals(parsed.ArtifactId, asset.ArtifactId, StringComparison.Ordinal) &&
        string.Equals(parsed.Version, asset.Version, StringComparison.Ordinal) &&
        string.Equals(MediaType(parsed.Extension), asset.MediaType, StringComparison.Ordinal) &&
        ArtifactDigests.ValidSha256(asset.Sha256) && asset.Size > 0 &&
        DateTimeOffset.TryParse(asset.CreatedAt, out _);

    // Maven POMs are XML. Parsing is bounded and DTD/external entity resolution is prohibited.
    // This initial release slice requires explicit GAV coordinates; parent-inherited G/V
    // and mutable version metadata require separate Maven interoperability acceptance.
    public static bool PomIdentityMatches(ReadOnlyMemory<byte> bytes,
        MavenCoordinates.Artifact coordinates)
    {
        if (bytes.IsEmpty || bytes.Length > 16 * 1024 * 1024) return false;
        try
        {
            using var input = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 16 * 1024 * 1024,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            var root = document.Root;
            if (root?.Name.LocalName != "project") return false;
            string? Field(string name)
            {
                var fields = root.Elements().Where(x => x.Name.LocalName == name).ToArray();
                return fields.Length == 1 ? fields[0].Value : null;
            }
            return Field("groupId") == coordinates.GroupId &&
                Field("artifactId") == coordinates.ArtifactId &&
                Field("version") == coordinates.Version;
        }
        catch (XmlException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool TryMetadataCoordinates(string? path, out string groupId,
        out string artifactId, out string? checksum)
    {
        groupId = artifactId = "";
        checksum = null;
        if (path is null || path.Length > 1024 || path.Contains('%') ||
            path.Contains('\\') || path.StartsWith('/') || path.Any(char.IsControl))
            return false;
        var basePath = path;
        foreach (var suffix in new[] { ".sha256", ".sha1", ".md5" })
        {
            if (!basePath.EndsWith(suffix, StringComparison.Ordinal)) continue;
            checksum = suffix;
            basePath = basePath[..^suffix.Length];
            break;
        }
        if (!basePath.EndsWith("/maven-metadata.xml", StringComparison.Ordinal))
            return false;
        var parts = basePath.Split('/');
        if (parts.Length is < 3 or > 20 ||
            parts[..^2].Any(x => !MavenCoordinates.ValidSegment(x) || x.Contains('.')) ||
            !MavenCoordinates.ValidSegment(parts[^2]))
            return false;
        groupId = string.Join('.', parts[..^2]);
        artifactId = parts[^2];
        return MavenCoordinates.ValidGroup(groupId);
    }

    private static string MetadataXml(string groupId, string artifactId,
        IReadOnlyList<string> versions)
    {
        return new XDocument(new XElement("metadata",
            new XElement("groupId", groupId),
            new XElement("artifactId", artifactId),
            new XElement("versioning",
                new XElement("versions",
                    versions.Select(version => new XElement("version", version)))))
            ).ToString(SaveOptions.DisableFormatting);
    }

    private static async Task<string> ChecksumAsync(string path, string algorithm,
        CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = algorithm switch
        {
            "sha256" => await SHA256.HashDataAsync(stream, ct),
            "sha1" => await SHA1.HashDataAsync(stream, ct),
            "md5" => await MD5.HashDataAsync(stream, ct),
            _ => throw new ArgumentException("Invalid requested checksum")
        };
        return Convert.ToHexStringLower(bytes);
    }

    public static void Map(WebApplication app)
    {
        app.MapMethods("/maven/{**path}", ["GET", "HEAD", "PUT"], async (
            string path, Settings cfg, IPackageDatabase db, BlobStore blobs,
            ArtifactLocks locks, HttpContext ctx) =>
        {
            if (ctx.Request.Method is "GET" or "HEAD")
            {
                if (Access.RequireGlobalRaw(ctx, cfg) is { } denied) return denied;
                if (TryMetadataCoordinates(path, out var group, out var name, out var metadataChecksum))
                {
                    var versions = db.MavenVersions(group, name);
                    if (versions.Count == 0) return Results.NotFound();
                    var bytes = Encoding.UTF8.GetBytes(MetadataXml(group, name, versions));
                    ctx.Response.Headers.CacheControl = "private, no-store";
                    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    if (metadataChecksum is not null)
                    {
                        var digest = metadataChecksum switch
                        {
                            ".sha256" => Convert.ToHexStringLower(SHA256.HashData(bytes)),
                            ".sha1" => Convert.ToHexStringLower(SHA1.HashData(bytes)),
                            _ => Convert.ToHexStringLower(MD5.HashData(bytes))
                        };
                        return Results.Text(digest, "text/plain");
                    }
                    return Results.Bytes(bytes, "application/xml");
                }
                var checksum = path.EndsWith(".sha256", StringComparison.Ordinal) ? "sha256" :
                    path.EndsWith(".sha1", StringComparison.Ordinal) ? "sha1" :
                    path.EndsWith(".md5", StringComparison.Ordinal) ? "md5" : null;
                var assetPath = checksum is null ? path : path[..^(checksum.Length + 1)];
                if (!ValidAssetPath(assetPath, out _))
                    return Results.BadRequest(new { error = "Invalid Maven release path" });
                var asset = db.GetMavenAsset(assetPath);
                if (asset is null) return Results.NotFound();
                if (!ValidRecord(asset) || asset.Size > cfg.MaxArtifactBytes)
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                var local = blobs.Existing(Key(assetPath));
                if (local is null || !await ArtifactDigests.MatchesSha256Async(
                    local, asset.Sha256, asset.Size, ctx.RequestAborted))
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

                ctx.Response.Headers.CacheControl = "private, no-store";
                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (checksum is not null)
                {
                    var digest = checksum == "sha256"
                        ? asset.Sha256
                        : await ChecksumAsync(local, checksum, ctx.RequestAborted);
                    return Results.Text(digest, "text/plain");
                }
                ctx.Response.Headers.ETag = "\"" + asset.Sha256 + "\"";
                if (ctx.Request.Headers.IfNoneMatch.ToString() == "\"" + asset.Sha256 + "\"")
                    return Results.StatusCode(StatusCodes.Status304NotModified);
                return Results.File(local, asset.MediaType, enableRangeProcessing: true);
            }

            if (Access.Require(ctx, cfg, write: true) is { } writeDenied) return writeDenied;
            if (!ValidAssetPath(path, out var coord) || coord is null)
                return Results.BadRequest(new { error = "Unsupported Maven release artifact path" });
            if (Access.RequireNamespace(ctx, cfg, "maven",
                coord.GroupId + "/" + coord.ArtifactId) is { } namespaceDenied)
                return namespaceDenied;
            if (ctx.Request.ContentLength is <= 0)
                return Results.BadRequest(new { error = "Empty Maven upload" });
            if (ctx.Request.ContentLength > cfg.MaxArtifactBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

            byte[]? pomBytes = null;
            if (coord.Extension == "pom")
            {
                try
                {
                    pomBytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body,
                        Math.Min(cfg.MaxMetadataBytes, (int)Math.Min(cfg.MaxArtifactBytes, int.MaxValue)),
                        ctx.RequestAborted);
                }
                catch (PayloadTooLargeException)
                { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
                if (!PomIdentityMatches(pomBytes, coord))
                    return Results.BadRequest(new { error = "POM identity does not match repository path" });
            }
            using var gate = await locks.AcquireAsync("maven", path, "", ctx.RequestAborted);
            var key = Key(path);
            if (db.GetMavenAsset(path) is not null || blobs.Contains(key))
                return Results.Conflict(new { error = "Immutable Maven file already exists" });
            db.BeginPublication(key, "maven", path, coord.Version);
            try
            {
                await using var buffered = pomBytes is null ? null :
                    new MemoryStream(pomBytes, writable: false);
                if (!await blobs.PutAsync(key, buffered ?? ctx.Request.Body,
                    cfg.MaxArtifactBytes, ctx.RequestAborted))
                    return Results.Conflict(new { error = "Immutable Maven file collision" });
                var local = blobs.Existing(key) ??
                    throw new InvalidDataException("Maven file missing after storage acknowledgement");
                var size = new FileInfo(local).Length;
                if (size <= 0) throw new InvalidDataException("Empty Maven artifact after upload");
                await using var input = new FileStream(local, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var digest = Convert.ToHexStringLower(
                    await SHA256.HashDataAsync(input, ctx.RequestAborted));
                var asset = new MavenAssetEntry(path, coord.GroupId,
                    coord.ArtifactId, coord.Version, digest, size, MediaType(coord.Extension)!,
                    DateTimeOffset.UtcNow.ToString("O"));
                if (!db.PublishMavenAsset(asset, Access.Actor(ctx)))
                    return Results.Conflict(new { error = "Maven artifact already published" });
                db.CompletePublication(key);
                return Results.StatusCode(StatusCodes.Status201Created);
            }
            catch (PayloadTooLargeException)
            { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            catch (StorageQuotaExceededException)
            { return Results.StatusCode(507); }
            // Cross-store errors deliberately retain intents for explicit recovery.
        }).RequireRateLimiting("publish");
    }
}
