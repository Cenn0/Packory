using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Packory;

/// <summary>
/// Experimental GLOBAL hosted Maven 2 repository layout for immutable releases.
/// Native Maven/Gradle consumption is the target; snapshot publication, remote
/// proxying, generic PUT metadata and Maven Deploy Plugin integration are not.
/// Never execute user jars or resolve arbitrary remote URLs.
/// </summary>
public static class MavenEndpoints
{
    public sealed record Coordinates(string RelativePath, string GroupId,
        string ArtifactId, string Version, string FileName, string Extension);

    private static readonly Regex GroupPart = new(
        @"\A[a-z][a-z0-9_-]{0,63}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ArtifactPart = new(
        @"\A[a-z0-9][a-z0-9._-]{0,79}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ReleasePart = new(
        @"\A[0-9A-Za-z][0-9A-Za-z._-]{0,99}\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ClassifierPart = new(
        @"\A[a-z0-9][a-z0-9_-]{0,39}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string BlobKey(string relativePath) => "maven/" + relativePath;

    private static bool TryCoordinates(string[] groupPath, string artifact,
        out string groupId)
    {
        groupId = "";
        if (groupPath.Length is < 1 or > 12 || groupPath.Any(x => !GroupPart.IsMatch(x)) ||
            !ArtifactPart.IsMatch(artifact) || artifact.Contains("..", StringComparison.Ordinal))
            return false;
        groupId = string.Join('.', groupPath);
        return groupId.Length <= 220;
    }

    public static bool TryParseAsset(string? path, out Coordinates coordinates)
    {
        coordinates = null!;
        if (path is null || path.Length > 450 || path.Contains('\\') ||
            path.Contains('%') || path.Contains("..", StringComparison.Ordinal))
            return false;
        var parts = path.Split('/');
        if (parts.Length is < 4 or > 15) return false;
        var artifact = parts[^3];
        var version = parts[^2];
        var filename = parts[^1];
        if (!TryCoordinates(parts[..^3], artifact, out var group) ||
            !ReleasePart.IsMatch(version) ||
            version.EndsWith("-SNAPSHOT", StringComparison.OrdinalIgnoreCase) ||
            version.EndsWith('.') || version.EndsWith('-'))
            return false;
        var prefix = artifact + "-" + version;
        string extension;
        if (filename is null || filename.Length > 240)
            return false;
        if (filename == prefix + ".pom") extension = "pom";
        else if (filename == prefix + ".jar") extension = "jar";
        else if (filename.StartsWith(prefix + "-", StringComparison.Ordinal) &&
            filename.EndsWith(".jar", StringComparison.Ordinal) &&
            ClassifierPart.IsMatch(filename[(prefix.Length + 1)..^4]))
            extension = "jar";
        else return false;
        coordinates = new Coordinates(path, group, artifact, version, filename, extension);
        return true;
    }

    private static bool TryParseMetadata(string path, out string group, out string artifact)
    {
        group = artifact = "";
        if (path.Contains('\\') || path.Contains('%') || path.Contains("..", StringComparison.Ordinal))
            return false;
        var parts = path.Split('/');
        if (parts.Length is < 3 or > 14 || parts[^1] != "maven-metadata.xml")
            return false;
        artifact = parts[^2];
        return TryCoordinates(parts[..^2], artifact, out group);
    }

    private static bool PomMatches(byte[] pomBytes, Coordinates asset)
    {
        if (asset.Extension != "pom" || pomBytes.Length == 0) return false;
        try
        {
            using var stream = new MemoryStream(pomBytes, writable: false);
            using var xml = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersFromEntities = 0,
                MaxCharactersInDocument = pomBytes.LongLength
            });
            var doc = XDocument.Load(xml);
            if (doc.Root is null || doc.Root.Name.LocalName != "project" ||
                doc.Root.Name.NamespaceName is not ("" or "http://maven.apache.org/POM/4.0.0"))
                return false;
            string? RootValue(string key)
            {
                var matching = doc.Root.Elements().Where(e =>
                    e.Name.LocalName == key && e.Name.NamespaceName == doc.Root.Name.NamespaceName)
                    .ToArray();
                return matching.Length == 1 ? matching[0].Value.Trim() : null;
            }
            // Explicit coordinates only for the initial hosted release adapter.
            return RootValue("groupId") == asset.GroupId &&
                   RootValue("artifactId") == asset.ArtifactId &&
                   RootValue("version") == asset.Version;
        }
        catch (Exception error) when (error is XmlException or InvalidOperationException
            or ArgumentException or IOException)
        { return false; }
    }

    private static byte[] VersionMetadata(string group, string artifact,
        IReadOnlyList<string> versions)
    {
        // Do not invent Maven ComparableVersion sorting or a "latest" tag from
        // lexicographic database order. Maven handles pinned-version retrieval.
        var doc = new XDocument(new XElement("metadata",
            new XElement("groupId", group),
            new XElement("artifactId", artifact),
            new XElement("versioning", new XElement("versions",
                versions.Select(version => new XElement("version", version))))));
        return System.Text.Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting));
    }

    private static async Task<IResult> Download(HttpContext ctx, Settings cfg,
        IPackageDatabase db, BlobStore blobs, string relativePath)
    {
        if (Access.RequireGlobalArtifactRead(ctx, cfg) is { } denied) return denied;
        if (relativePath.EndsWith("/maven-metadata.xml", StringComparison.Ordinal))
        {
            if (!TryParseMetadata(relativePath, out var group, out var artifact))
                return Results.BadRequest(new { error = "Invalid Maven metadata path" });
            var versions = db.MavenVersions(group, artifact);
            if (versions.Count == 0) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "private, no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Bytes(VersionMetadata(group, artifact, versions), "application/xml");
        }

        var path = relativePath;
        var checksumType = "";
        if (path.EndsWith(".sha256", StringComparison.Ordinal))
        {
            path = path[..^7];
            checksumType = "sha256";
        }
        else if (path.EndsWith(".sha1", StringComparison.Ordinal))
        {
            path = path[..^5];
            checksumType = "sha1";
        }
        if (!TryParseAsset(path, out var asset))
            return Results.BadRequest(new { error = "Invalid Maven artifact path" });
        var row = db.MavenAsset(path);
        if (row is null) return Results.NotFound();
        if (row.GroupId != asset.GroupId || row.ArtifactId != asset.ArtifactId ||
            row.Version != asset.Version || row.Size <= 0 ||
            row.Size > cfg.MaxArtifactBytes || !ArtifactDigests.ValidSha256(row.Sha256))
            return Results.StatusCode(503);
        var file = blobs.Existing(BlobKey(path));
        if (file is null || !await ArtifactDigests.MatchesSha256Async(
            file, row.Sha256, row.Size, ctx.RequestAborted))
            return Results.StatusCode(503);
        ctx.Response.Headers.CacheControl = "private, no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (checksumType.Length != 0)
        {
            if (checksumType == "sha256")
                return Results.Text(row.Sha256, "text/plain");
            await using var input = File.OpenRead(file);
            var sha1 = Convert.ToHexStringLower(await SHA1.HashDataAsync(input, ctx.RequestAborted));
            // SHA-1 sidecars are ONLY Maven compatibility; SHA-256 is authoritative.
            return Results.Text(sha1, "text/plain");
        }
        ctx.Response.Headers.ETag = "\"" + row.Sha256 + "\"";
        if (ctx.Request.Headers.IfNoneMatch.ToString() == "\"" + row.Sha256 + "\"")
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.File(file,
            asset.Extension == "pom" ? "application/xml" : "application/java-archive",
            enableRangeProcessing: true);
    }

    private static async Task<IResult> Upload(HttpContext ctx, Settings cfg,
        IPackageDatabase db, BlobStore blobs, ArtifactLocks locks, string relativePath)
    {
        if (Access.Require(ctx, cfg, write: true) is { } denied) return denied;
        if (!TryParseAsset(relativePath, out var asset))
            return Results.BadRequest(new { error = "Only canonical immutable Maven release files are accepted" });
        if (Access.RequireNamespace(ctx, cfg, "maven",
            asset.GroupId + "/" + asset.ArtifactId) is { } deniedNamespace) return deniedNamespace;
        if (ctx.Request.ContentLength is 0)
            return Results.BadRequest(new { error = "Artifact is empty" });
        if (ctx.Request.ContentLength > cfg.MaxArtifactBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        byte[]? pom = null;
        if (asset.Extension == "pom")
        {
            try
            {
                pom = await BlobStore.ReadBoundedAsync(ctx.Request.Body,
                    Math.Min(cfg.MaxMetadataBytes, cfg.MaxArtifactBytes), ctx.RequestAborted);
            }
            catch (PayloadTooLargeException)
            { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            if (!PomMatches(pom, asset))
                return Results.BadRequest(new { error = "POM coordinates do not match canonical artifact path" });
        }
        using var gate = await locks.AcquireAsync("maven", asset.GroupId + "/" +
            asset.ArtifactId, relativePath, ctx.RequestAborted);
        var key = BlobKey(relativePath);
        if (db.MavenAsset(relativePath) is not null || blobs.Contains(key))
            return Results.Conflict(new { error = "Immutable Maven file already exists" });
        db.BeginPublication(key, "maven-asset", relativePath, asset.Version);
        try
        {
            await using var fileBody = pom is null
                ? ctx.Request.Body : new MemoryStream(pom, writable: false);
            if (!await blobs.PutAsync(key, fileBody, cfg.MaxArtifactBytes, ctx.RequestAborted))
                return Results.Conflict(new { error = "Immutable blob collision" });
            var file = blobs.Existing(key) ??
                throw new InvalidDataException("Maven file missing after storage acknowledgement");
            var size = new FileInfo(file).Length;
            if (size <= 0) throw new InvalidDataException("Empty Maven artifact");
            await using var stream = File.OpenRead(file);
            var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ctx.RequestAborted));
            var row = new MavenAssetEntry(relativePath, asset.GroupId, asset.ArtifactId,
                asset.Version, sha, size, DateTimeOffset.UtcNow.ToString("O"));
            if (!db.PublishMaven(row, Access.Actor(ctx)))
                return Results.Conflict(new { error = "Maven asset version already exists" });
            db.CompletePublication(key);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { groupId = asset.GroupId,
                artifactId = asset.ArtifactId, asset.Version, asset.FileName,
                sha256 = sha, size }, statusCode: StatusCodes.Status201Created);
        }
        catch (PayloadTooLargeException)
        { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        catch (StorageQuotaExceededException)
        { return Results.StatusCode(507); }
        // Incomplete publications keep their journal for operator recovery.
    }

    public static void Map(WebApplication app)
    {
        app.MapMethods("/maven/{**resource}", ["GET", "HEAD", "PUT"], async (
            string? resource, Settings cfg, IPackageDatabase db, BlobStore blobs,
            ArtifactLocks locks, HttpContext ctx) =>
        {
            if (string.IsNullOrWhiteSpace(resource) || resource.Length > 460 ||
                resource.StartsWith('/') || resource.Contains('?') ||
                resource.Contains('#')) return Results.BadRequest();
            if (HttpMethods.IsPut(ctx.Request.Method))
                return await Upload(ctx, cfg, db, blobs, locks, resource);
            return await Download(ctx, cfg, db, blobs, resource);
        }).RequireRateLimiting("publish");
    }
}
