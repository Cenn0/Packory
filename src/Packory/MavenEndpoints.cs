using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Packory;

/// <summary>
/// Hosted Maven release and timestamped snapshot files with native metadata PUT.
/// Global and private adapters share wire behavior, while their authorization and
/// catalog identities stay independent. There is no upstream proxy.
/// </summary>
public static class MavenEndpoints
{
    private static readonly HashSet<string> Extensions = new(StringComparer.Ordinal)
        { "jar", "pom", "module", "zip", "war", "aar", "tar.gz" };

    public static string Key(string path) => "maven/" + path;

    public static string? AssetMediaType(string extension) => MediaType(extension);

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
            (!artifact.Version.EndsWith("-SNAPSHOT", StringComparison.OrdinalIgnoreCase) ||
                MavenCoordinates.IsSnapshot(artifact.Version)) &&
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
    // Maven permits groupId/version inheritance from an explicit parent. Verify
    // the effective coordinates without fetching parent files from untrusted URLs.
    // A timestamped filename still carries the base SNAPSHOT version inside its POM.
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
            if (root?.Name.LocalName != "project" ||
                root.Name.NamespaceName is not ("" or "http://maven.apache.org/POM/4.0.0"))
                return false;
            string? Field(string name)
            {
                var fields = root.Elements().Where(x => x.Name.LocalName == name).ToArray();
                return fields.Length == 1 && fields[0].Name.Namespace == root.Name.Namespace
                    ? fields[0].Value : null;
            }
            var parents = root.Elements().Where(x => x.Name.LocalName == "parent").ToArray();
            if (parents.Length > 1 || parents.Any(x => x.Name.Namespace != root.Name.Namespace))
                return false;
            var parent = parents.SingleOrDefault();
            string? ParentField(string name)
            {
                if (parent is null) return null;
                var fields = parent.Elements().Where(x => x.Name.LocalName == name).ToArray();
                return fields.Length == 1 && fields[0].Name.Namespace == root.Name.Namespace &&
                    !fields[0].HasElements ? fields[0].Value : null;
            }
            // Reject malformed/incomplete parents and duplicate or foreign identity nodes.
            if (parent is not null && (!MavenCoordinates.ValidGroup(ParentField("groupId")) ||
                !MavenCoordinates.ValidSegment(ParentField("artifactId")) ||
                !MavenCoordinates.ValidSegment(ParentField("version"))))
                return false;
            foreach (var name in new[] { "groupId", "artifactId", "version" })
            {
                var nodes = root.Elements().Where(x => x.Name.LocalName == name).ToArray();
                if (nodes.Length > 1 || nodes.Any(x => x.Name.Namespace != root.Name.Namespace ||
                    x.HasElements)) return false;
            }
            foreach (var name in new[] { "groupId", "artifactId", "version" })
                if (parent is not null && parent.Elements().Count(x => x.Name.LocalName == name) != 1)
                    return false;
            return (Field("groupId") ?? ParentField("groupId")) == coordinates.GroupId &&
                Field("artifactId") == coordinates.ArtifactId &&
                (Field("version") ?? ParentField("version")) == coordinates.Version;
        }
        catch (XmlException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private sealed record StoredAsset(string Sha256, long Size, string MediaType, bool Valid);

    private static StoredAsset? Asset(IPackageDatabase db, string scope, string path)
    {
        if (scope == "")
        {
            var asset = db.GetMavenAsset(path);
            return asset is null ? null : new StoredAsset(asset.Sha256, asset.Size,
                asset.MediaType, ValidRecord(asset));
        }
        var feedAsset = db.GetFeedAsset(scope, "maven", path);
        return feedAsset is null ? null : new StoredAsset(feedAsset.Sha256,
            feedAsset.Size, feedAsset.MediaType, FeedAssetEntry.Valid(feedAsset));
    }

    private static string StorageKey(string scope, string path) =>
        scope == "" ? Key(path) : "feeds/" + scope + "/maven/" + path;

    private static string LayoutLockPath(string metadataPath)
    {
        var directory = metadataPath[..^"/maven-metadata.xml".Length];
        var parts = directory.Split('/');
        // Ambiguous GA/GAV paths share the first -SNAPSHOT directory. Lock
        // that prefix to prevent two first publications racing the collision check.
        var ambiguous = Array.FindIndex(parts, MavenCoordinates.IsSnapshot);
        return ambiguous >= 0 ? string.Join('/', parts[..(ambiguous + 1)]) : directory;
    }

    private static (string Path, string? Algorithm) SplitChecksum(string path)
    {
        foreach (var algorithm in new[] { "sha512", "sha256", "sha1", "md5" })
        {
            var suffix = "." + algorithm;
            if (path.EndsWith(suffix, StringComparison.Ordinal))
                return (path[..^suffix.Length], algorithm);
        }
        return (path, null);
    }

    private static string Digest(byte[] bytes, string algorithm) =>
        Convert.ToHexStringLower(algorithm switch
        {
            "sha512" => SHA512.HashData(bytes),
            "sha256" => SHA256.HashData(bytes),
            "sha1" => SHA1.HashData(bytes),
            "md5" => MD5.HashData(bytes),
            _ => throw new ArgumentException("Invalid Maven checksum algorithm")
        });

    private static async Task<string> ChecksumAsync(string path, string algorithm,
        CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = algorithm switch
        {
            "sha512" => await SHA512.HashDataAsync(stream, ct),
            "sha256" => await SHA256.HashDataAsync(stream, ct),
            "sha1" => await SHA1.HashDataAsync(stream, ct),
            "md5" => await MD5.HashDataAsync(stream, ct),
            _ => throw new ArgumentException("Invalid Maven checksum algorithm")
        };
        return Convert.ToHexStringLower(bytes);
    }

    private static async Task<string?> VerifiedFile(string scope, string path,
        StoredAsset asset, Settings cfg, BlobStore blobs, CancellationToken ct)
    {
        if (!asset.Valid || asset.Size > cfg.MaxArtifactBytes) return null;
        var key = StorageKey(scope, path);
        if (!await ArtifactDigests.MatchesRemoteAsync(blobs, key, "sha256",
            Convert.FromHexString(asset.Sha256), asset.Size, cfg.MaxArtifactBytes, ct)) return null;
        var local = blobs.Existing(key);
        return local is not null && await ArtifactDigests.MatchesSha256Async(
            local, asset.Sha256, asset.Size, ct) ? local : null;
    }

    private static IReadOnlyList<string> ListedVersions(IPackageDatabase db, string scope,
        MavenMetadata.Coordinates coordinates)
    {
        var versions = scope == "" ? db.MavenVersions(coordinates.GroupId, coordinates.ArtifactId) :
            db.FeedMavenVersions(scope, coordinates.GroupId, coordinates.ArtifactId);
        return versions.Where(x => !MavenCoordinates.IsSnapshot(x) ||
            db.GetMavenMetadata(scope, MavenMetadata.PathFor(coordinates.GroupId,
                coordinates.ArtifactId, x)) is { } metadata &&
            MavenMetadata.ValidRecord(metadata) &&
            MavenMetadata.TryParseAtPath(Convert.FromBase64String(metadata.ContentBase64),
                metadata.Path, out var document) && document!.Coordinates ==
                new MavenMetadata.Coordinates(coordinates.GroupId, coordinates.ArtifactId, x) &&
            MavenMetadata.ReferencesValid(metadata, db)).ToArray();
    }

    internal static async Task<IResult> Read(string scope, string requestedPath,
        Settings cfg, IPackageDatabase db, BlobStore blobs, HttpContext ctx)
    {
        ctx.Response.Headers.CacheControl = "private, no-store";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        var (path, algorithm) = SplitChecksum(requestedPath);
        if (MavenMetadata.TryParsePath(path, out var coordinates) && coordinates is not null)
        {
            var stored = db.GetMavenMetadata(scope, path);
            byte[] bytes;
            if (stored is not null)
            {
                if (!MavenMetadata.ValidRecord(stored) || stored.Size > cfg.MaxMetadataBytes ||
                    !MavenMetadata.ReferencesValid(stored, db))
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                bytes = Convert.FromBase64String(stored.ContentBase64);
                MavenMetadata.TryParseAtPath(bytes, path, out var document);
                coordinates = document!.Coordinates;
            }
            else
            {
                coordinates = MavenMetadata.PathCandidates(path).FirstOrDefault(x =>
                    x.Version is null && MavenMetadata.HasInventory(db, scope, x));
                if (coordinates is null) return Results.NotFound();
                // The legacy generated listing survives upgrades, but unpublished
                // snapshot directories are not advertised as resolvable versions.
                var versions = ListedVersions(db, scope, coordinates);
                if (versions.Count == 0) return Results.NotFound();
                if (versions.Count > MavenMetadata.MaxEntries)
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                bytes = MavenMetadata.GeneratedVersions(coordinates, versions);
                if (bytes.Length > cfg.MaxMetadataBytes)
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            if (MavenMetadata.PathConflicts(db, scope, path, coordinates))
                return Results.Conflict(new { error = "Maven metadata path has conflicting identities" });
            if (algorithm is not null) return Results.Text(Digest(bytes, algorithm), "text/plain");
            ctx.Response.Headers.ETag = "\"" + Digest(bytes, "sha256") + "\"";
            if (ctx.Request.Headers.IfNoneMatch.ToString() == ctx.Response.Headers.ETag.ToString())
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(bytes, "application/xml");
        }
        if (!ValidAssetPath(path, out _))
            return Results.BadRequest(new { error = "Invalid Maven artifact path" });
        var asset = Asset(db, scope, path);
        if (asset is null) return Results.NotFound();
        var local = await VerifiedFile(scope, path, asset, cfg, blobs, ctx.RequestAborted);
        if (local is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (algorithm is not null)
            return Results.Text(algorithm == "sha256" ? asset.Sha256 :
                await ChecksumAsync(local, algorithm, ctx.RequestAborted), "text/plain");
        ctx.Response.Headers.ETag = "\"" + asset.Sha256 + "\"";
        if (ctx.Request.Headers.IfNoneMatch.ToString() == ctx.Response.Headers.ETag.ToString())
            return Results.StatusCode(StatusCodes.Status304NotModified);
        return Results.File(local, asset.MediaType, enableRangeProcessing: true);
    }

    private static IResult? UploadLength(HttpContext ctx, long maximum)
    {
        if (ctx.Request.ContentLength is null)
            return Results.StatusCode(StatusCodes.Status411LengthRequired);
        if (ctx.Request.ContentLength <= 0)
            return Results.BadRequest(new { error = "Empty Maven upload" });
        return ctx.Request.ContentLength > maximum
            ? Results.StatusCode(StatusCodes.Status413PayloadTooLarge) : null;
    }

    private static async Task<IResult> VerifySidecar(string scope, string path,
        string algorithm, bool metadata, Settings cfg, IPackageDatabase db,
        BlobStore blobs, HttpContext ctx)
    {
        if (UploadLength(ctx, 130) is { } denied) return denied;
        byte[] bytes;
        try { bytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body, 130, ctx.RequestAborted); }
        catch (PayloadTooLargeException)
        { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        var supplied = Encoding.ASCII.GetString(bytes);
        if (supplied.EndsWith("\r\n", StringComparison.Ordinal)) supplied = supplied[..^2];
        else if (supplied.EndsWith("\n", StringComparison.Ordinal)) supplied = supplied[..^1];
        var length = algorithm switch { "sha512" => 128, "sha256" => 64, "sha1" => 40, _ => 32 };
        if (supplied.Length != length || !supplied.All(Uri.IsHexDigit))
            return Results.BadRequest(new { error = "Invalid Maven checksum" });
        string expected;
        if (metadata)
        {
            var entry = db.GetMavenMetadata(scope, path);
            if (entry is null) return Results.NotFound();
            if (!MavenMetadata.ValidRecord(entry) || entry.Size > cfg.MaxMetadataBytes ||
                !MavenMetadata.ReferencesValid(entry, db))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            MavenMetadata.TryParseAtPath(Convert.FromBase64String(entry.ContentBase64), path, out var document);
            if (MavenMetadata.PathConflicts(db, scope, path, document!.Coordinates))
                return Results.Conflict(new { error = "Maven metadata path has conflicting identities" });
            expected = Digest(Convert.FromBase64String(entry.ContentBase64), algorithm);
        }
        else
        {
            var asset = Asset(db, scope, path);
            if (asset is null) return Results.NotFound();
            var local = await VerifiedFile(scope, path, asset, cfg, blobs, ctx.RequestAborted);
            if (local is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            expected = algorithm == "sha256" ? asset.Sha256 :
                await ChecksumAsync(local, algorithm, ctx.RequestAborted);
        }
        // Sidecars validate committed bytes; they never become an independent
        // source of truth or a mutable object in the blob store.
        return string.Equals(expected, supplied, StringComparison.OrdinalIgnoreCase)
            ? Results.NoContent() : Results.Conflict(new { error = "Checksum differs from committed Maven bytes" });
    }

    private static async Task<IResult> PutMetadata(string scope, string path,
        Settings cfg, IPackageDatabase db,
        BlobStore blobs, ArtifactLocks locks, HttpContext ctx)
    {
        var maximum = Math.Min(cfg.MaxMetadataBytes, MavenMetadata.MaxBytes);
        if (UploadLength(ctx, maximum) is { } denied) return denied;
        byte[] bytes;
        try { bytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body, maximum, ctx.RequestAborted); }
        catch (PayloadTooLargeException)
        { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        if (!MavenMetadata.TryParseAtPath(bytes, path, out var document) || document is null)
            return Results.BadRequest(new { error = "Invalid Maven repository metadata" });
        var coordinates = document.Coordinates;
        if (scope == "" && Access.RequireNamespace(ctx, cfg, "maven",
            coordinates.GroupId + "/" + coordinates.ArtifactId) is { } namespaceDenied)
            return namespaceDenied;
        var entry = new MavenMetadataEntry(scope, path, Convert.ToBase64String(bytes),
            Digest(bytes, "sha256"), bytes.Length, DateTimeOffset.UtcNow.ToString("O"));
        using var gate = await locks.AcquireAsync("maven-layout", scope, LayoutLockPath(path), ctx.RequestAborted);
        var previous = db.GetMavenMetadata(scope, path);
        if (previous is not null && !MavenMetadata.ValidRecord(previous))
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (MavenMetadata.PathConflicts(db, scope, path, coordinates))
            return Results.Conflict(new { error = "Maven metadata path has conflicting identities" });
        var match = ctx.Request.Headers.IfMatch.ToString();
        if (match != "" && (previous is null || match != "\"" + previous.Sha256 + "\""))
            return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
        if (ctx.Request.Headers.IfNoneMatch.ToString() == "*" && previous is not null)
            return Results.StatusCode(StatusCodes.Status412PreconditionFailed);
        if (previous is not null)
        {
            MavenMetadata.TryParseAtPath(Convert.FromBase64String(previous.ContentBase64), path, out var old);
            if (!MavenMetadata.Advances(old!, document))
                return Results.Conflict(new { error = "Maven metadata would remove or rewind published coordinates" });
        }
        else if (coordinates.Version is null &&
            ListedVersions(db, scope, coordinates).Except(document.Versions, StringComparer.Ordinal).Any())
            return Results.Conflict(new { error = "Maven metadata would remove existing repository versions" });
        if (!MavenMetadata.TryReferencedPaths(entry, db, out var references))
            return Results.Conflict(new { error = "Maven metadata references unavailable committed artifacts" });
        foreach (var reference in references)
        {
            var asset = Asset(db, scope, reference);
            if (asset is null || await VerifiedFile(scope, reference, asset, cfg, blobs,
                ctx.RequestAborted) is null)
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        if (!db.PutMavenMetadata(entry, Access.Actor(ctx), previous?.Sha256))
            return Results.Conflict(new { error = "Maven metadata changed concurrently" });
        ctx.Response.Headers.ETag = "\"" + entry.Sha256 + "\"";
        return previous is null ? Results.StatusCode(StatusCodes.Status201Created) : Results.NoContent();
    }

    internal static async Task<IResult> Write(string scope, string requestedPath,
        Settings cfg, IPackageDatabase db, BlobStore blobs, ArtifactLocks locks,
        HttpContext ctx)
    {
        var (path, algorithm) = SplitChecksum(requestedPath);
        var metadata = MavenMetadata.TryParsePath(path, out _);
        MavenCoordinates.Artifact? coordinate = null;
        if (!metadata && !ValidAssetPath(path, out coordinate))
            return Results.BadRequest(new { error = "Unsupported Maven path" });
        if (metadata)
        {
            // Artifact names ending in -SNAPSHOT are valid. Resolve metadata
            // identity from exact XML, or an existing record for sidecars.
            if (scope == "")
            {
                var candidates = MavenMetadata.PathCandidates(path);
                var stored = db.GetMavenMetadata(scope, path);
                if (algorithm is not null && stored is not null && MavenMetadata.ValidRecord(stored))
                {
                    MavenMetadata.TryParseAtPath(Convert.FromBase64String(stored.ContentBase64), path, out var doc);
                    candidates = [doc!.Coordinates];
                }
                IResult? namespaceDenied = null;
                var permitted = false;
                foreach (var candidate in candidates)
                {
                    namespaceDenied = Access.RequireNamespace(ctx, cfg, "maven",
                        candidate.GroupId + "/" + candidate.ArtifactId);
                    if (namespaceDenied is null) { permitted = true; break; }
                }
                if (!permitted) return namespaceDenied!;
            }
            return algorithm is not null
                ? await VerifySidecar(scope, path, algorithm, true, cfg, db, blobs, ctx)
                : await PutMetadata(scope, path, cfg, db, blobs, locks, ctx);
        }
        var group = coordinate!.GroupId;
        var name = coordinate.ArtifactId;
        if (scope == "" && Access.RequireNamespace(ctx, cfg, "maven", group + "/" + name) is { } denied)
            return denied;
        if (algorithm is not null)
            return await VerifySidecar(scope, path, algorithm, false, cfg, db, blobs, ctx);
        if (UploadLength(ctx, cfg.MaxArtifactBytes) is { } lengthDenied) return lengthDenied;
        byte[]? pomBytes = null;
        if (coordinate!.Extension == "pom")
        {
            try
            {
                pomBytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body,
                    Math.Min(cfg.MaxMetadataBytes, (int)Math.Min(cfg.MaxArtifactBytes, int.MaxValue)),
                    ctx.RequestAborted);
            }
            catch (PayloadTooLargeException)
            { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
            if (!PomIdentityMatches(pomBytes, coordinate))
                return Results.BadRequest(new { error = "POM identity differs from Maven coordinates" });
        }
        var key = StorageKey(scope, path);
        var lockPath = MavenMetadata.PathFor(group, name,
            MavenCoordinates.IsSnapshot(coordinate.Version) ? coordinate.Version : null);
        using var gate = await locks.AcquireAsync("maven-layout", scope, LayoutLockPath(lockPath), ctx.RequestAborted);
        var gaCoordinates = new MavenMetadata.Coordinates(group, name, null);
        if (MavenMetadata.PathConflicts(db, scope, MavenMetadata.PathFor(group, name), gaCoordinates) ||
            MavenCoordinates.IsSnapshot(coordinate.Version) && MavenMetadata.PathConflicts(db, scope,
                MavenMetadata.PathFor(group, name, coordinate.Version),
                new MavenMetadata.Coordinates(group, name, coordinate.Version)))
            return Results.Conflict(new { error = "Maven artifact would introduce a metadata path collision" });
        if (Asset(db, scope, path) is not null || blobs.Contains(key))
            return Results.Conflict(new { error = "Immutable Maven artifact exists" });
        db.BeginPublication(key, scope == "" ? "maven" : "feed-maven",
            scope == "" ? path : scope + "|" + path, coordinate.Version);
        try
        {
            await using var buffered = pomBytes is null ? null : new MemoryStream(pomBytes, writable: false);
            if (!await blobs.PutAsync(key, buffered ?? ctx.Request.Body,
                cfg.MaxArtifactBytes, ctx.RequestAborted))
                return Results.Conflict(new { error = "Immutable Maven blob collision" });
            var stored = blobs.Existing(key) ?? throw new InvalidDataException("Maven blob missing after write");
            var size = new FileInfo(stored).Length;
            if (size <= 0 || size > cfg.MaxArtifactBytes)
                throw new InvalidDataException("Invalid Maven artifact length");
            var sha256 = await ChecksumAsync(stored, "sha256", ctx.RequestAborted);
            var at = DateTimeOffset.UtcNow.ToString("O");
            var published = scope == ""
                ? db.PublishMavenAsset(new MavenAssetEntry(path, group, name, coordinate.Version,
                    sha256, size, MediaType(coordinate.Extension)!, at), Access.Actor(ctx))
                : db.PublishFeedAsset(new FeedAssetEntry(scope, "maven", path, group + ":" + name,
                    coordinate.Version, MediaType(coordinate.Extension)!, sha256, size, at), Access.Actor(ctx));
            if (!published) return Results.Conflict(new { error = "Maven artifact conflict" });
            db.CompletePublication(key);
            return Results.StatusCode(StatusCodes.Status201Created);
        }
        catch (PayloadTooLargeException)
        { return Results.StatusCode(StatusCodes.Status413PayloadTooLarge); }
        catch (StorageQuotaExceededException) { return Results.StatusCode(507); }
        // Unexpected cross-store failures retain the intent for reconciliation.
    }

    public static void Map(WebApplication app)
    {
        app.MapMethods("/maven/{**path}", ["GET", "HEAD"], async (
            string path, Settings cfg, IPackageDatabase db, BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.RequireGlobalRaw(ctx, cfg) is { } denied) return denied;
            return await Read("", path, cfg, db, blobs, ctx);
        });
        app.MapPut("/maven/{**path}", async (string path, Settings cfg,
            IPackageDatabase db, BlobStore blobs, ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.Require(ctx, cfg, write: true) is { } denied) return denied;
            return await Write("", path, cfg, db, blobs, locks, ctx);
        }).RequireRateLimiting("publish");
    }
}
