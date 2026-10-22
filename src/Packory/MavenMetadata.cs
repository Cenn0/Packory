using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Packory;

/// <summary>
/// Exact publisher metadata bytes live in one database transaction. They are
/// mutable pointers to immutable Maven files, never mutable artifact blobs.
/// An empty scope is the legacy global repository; private scopes are feed IDs.
/// </summary>
public sealed record MavenMetadataEntry(string Scope, string Path,
    string ContentBase64, string Sha256, long Size, string UpdatedAt);

public static class MavenMetadata
{
    public const int MaxBytes = 16 * 1024 * 1024;
    public const int MaxEntries = 4096;
    public sealed record Coordinates(string GroupId, string ArtifactId, string? Version);
    public sealed record SnapshotVersion(string? Classifier, string Extension,
        string Value, string Updated, string Path);
    public sealed record Document(Coordinates Coordinates, IReadOnlyList<string> Versions,
        string? Latest, string? Release, string LastUpdated, string? Timestamp,
        int BuildNumber, IReadOnlyList<SnapshotVersion> SnapshotVersions);

    public static bool TryParsePath(string? path, out Coordinates? coordinates)
    {
        // Prefer the conventional snapshot interpretation for callers that only
        // inspect a path. XML identity or existing ledgers must disambiguate wire requests.
        coordinates = PathCandidates(path).LastOrDefault();
        return coordinates is not null;
    }

    public static IReadOnlyList<Coordinates> PathCandidates(string? path)
    {
        var candidates = new List<Coordinates>();
        if (TryPath(path, snapshot: false, out var artifact)) candidates.Add(artifact!);
        if (TryPath(path, snapshot: true, out var snapshot)) candidates.Add(snapshot!);
        return candidates;
    }

    private static bool TryPath(string? path, bool snapshot, out Coordinates? coordinates)
    {
        coordinates = null;
        if (path is null || path.Length > 1024 || path.Contains('%') ||
            path.Contains('\\') || path.StartsWith('/') || path.Any(char.IsControl) ||
            !path.EndsWith("/maven-metadata.xml", StringComparison.Ordinal)) return false;
        var parts = path.Split('/');
        if (parts.Length is < 3 or > 20 || parts[..^1].Any(x => !MavenCoordinates.ValidSegment(x)))
            return false;
        if (snapshot && (parts.Length < 4 || !MavenCoordinates.IsSnapshot(parts[^2]))) return false;
        var groupParts = snapshot ? parts[..^3] : parts[..^2];
        if (groupParts.Any(x => x.Contains('.'))) return false;
        var group = string.Join('.', groupParts);
        if (!MavenCoordinates.ValidGroup(group)) return false;
        coordinates = new Coordinates(group, parts[snapshot ? ^3 : ^2], snapshot ? parts[^2] : null);
        return true;
    }

    public static bool TryParseAtPath(ReadOnlyMemory<byte> bytes, string path, out Document? document)
    {
        foreach (var candidate in PathCandidates(path))
            if (TryParse(bytes, candidate, out document)) return true;
        document = null;
        return false;
    }

    public static string PathFor(string group, string artifact, string? version = null) =>
        group.Replace('.', '/') + "/" + artifact +
        (version is null ? "" : "/" + version) + "/maven-metadata.xml";

    public static bool ValidRecord(MavenMetadataEntry? entry)
    {
        if (entry is null || entry.Scope != "" && !FeedDefinition.ValidId(entry.Scope) ||
            !TryParsePath(entry.Path, out var coordinates) || coordinates is null ||
            entry.Size is <= 0 or > MaxBytes || !ArtifactDigests.ValidSha256(entry.Sha256) ||
            !DateTimeOffset.TryParse(entry.UpdatedAt, out _) ||
            entry.ContentBase64.Length > ((MaxBytes + 2L) / 3L) * 4L) return false;
        try
        {
            var bytes = Convert.FromBase64String(entry.ContentBase64);
            return bytes.LongLength == entry.Size &&
                Convert.ToBase64String(bytes) == entry.ContentBase64 &&
                Convert.ToHexStringLower(SHA256.HashData(bytes)) == entry.Sha256 &&
                (TryParseAtPath(bytes, entry.Path, out _) ||
                    MavenGroupMetadata.TryParseAtPath(bytes, entry.Path, out _));
        }
        catch (FormatException) { return false; }
    }

    private static bool Fields(XElement parent, params string[] allowed) =>
        parent.Nodes().OfType<XText>().All(x => string.IsNullOrWhiteSpace(x.Value)) &&
        parent.Elements().All(x => x.Name.Namespace == parent.Name.Namespace &&
            allowed.Contains(x.Name.LocalName, StringComparer.Ordinal)) &&
        parent.Elements().GroupBy(x => x.Name.LocalName).All(x => x.Count() == 1);

    private static string? Value(XElement parent, string name)
    {
        var element = parent.Element(parent.Name.Namespace + name);
        return element is not null && !element.HasElements ? element.Value : null;
    }

    private static bool ValidTimestamp(string? timestamp) => timestamp is { Length: 14 } &&
        timestamp.All(c => c is >= '0' and <= '9') &&
        DateTime.TryParseExact(timestamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _);

    private static bool ValidVersion(string? version) => MavenCoordinates.ValidSegment(version) &&
        (!version!.EndsWith("-SNAPSHOT", StringComparison.OrdinalIgnoreCase) ||
            MavenCoordinates.IsSnapshot(version));

    public static bool TryParse(ReadOnlyMemory<byte> bytes, Coordinates coordinates,
        out Document? document)
    {
        document = null;
        if (bytes.IsEmpty || bytes.Length > MaxBytes) return false;
        try
        {
            using var input = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaxBytes, MaxCharactersFromEntities = 0,
                IgnoreComments = true
            });
            var xml = XDocument.Load(reader, LoadOptions.None);
            var root = xml.Root;
            var leaves = new[] { "groupId", "artifactId", "version", "latest", "release",
                "lastUpdated", "timestamp", "buildNumber", "localCopy", "classifier",
                "extension", "value", "updated" };
            if (root?.Name.LocalName != "metadata" ||
                root.Name.NamespaceName is not ("" or "http://maven.apache.org/METADATA/1.1.0") ||
                !Fields(root, "groupId", "artifactId", "version", "versioning") ||
                root.Descendants().Any(x => x.Name.Namespace != root.Name.Namespace ||
                    x.Attributes().Any(a => !a.IsNamespaceDeclaration)) ||
                root.Descendants().Take(50001).Count() > 50000 ||
                root.Descendants().Any(x => x.Ancestors().Take(17).Count() > 16 ||
                    leaves.Contains(x.Name.LocalName, StringComparer.Ordinal) && x.HasElements) ||
                root.Attributes().Any(a => !a.IsNamespaceDeclaration &&
                    !(a.Name == "modelVersion" && a.Value is "1.0.0" or "1.1.0") &&
                    a.Name != XName.Get("schemaLocation", "http://www.w3.org/2001/XMLSchema-instance")) ||
                Value(root, "groupId") != coordinates.GroupId ||
                Value(root, "artifactId") != coordinates.ArtifactId ||
                Value(root, "version") != coordinates.Version)
                return false;
            var ns = root.Name.Namespace;
            var versioning = root.Element(ns + "versioning");
            if (versioning is null) return false;
            var updated = Value(versioning, "lastUpdated");
            if (!ValidTimestamp(updated)) return false;
            if (coordinates.Version is null)
            {
                if (!Fields(versioning, "latest", "release", "versions", "lastUpdated")) return false;
                var versionsElement = versioning.Element(ns + "versions");
                if (versionsElement is null || versionsElement.Elements().Any(x =>
                    x.Name != ns + "version" || x.HasElements) ||
                    versionsElement.Nodes().OfType<XText>().Any(x => !string.IsNullOrWhiteSpace(x.Value))) return false;
                var versions = versionsElement.Elements().Select(x => x.Value).ToArray();
                if (versions.Length is 0 or > MaxEntries || versions.Any(x => !ValidVersion(x)) ||
                    versions.Distinct(StringComparer.Ordinal).Count() != versions.Length) return false;
                var latest = Value(versioning, "latest");
                var release = Value(versioning, "release");
                if (latest is not null && !versions.Contains(latest, StringComparer.Ordinal) ||
                    release is not null && (!versions.Contains(release, StringComparer.Ordinal) ||
                        MavenCoordinates.IsSnapshot(release))) return false;
                document = new Document(coordinates, versions, latest, release, updated!, null, 0, []);
                return true;
            }
            if (!MavenCoordinates.IsSnapshot(coordinates.Version) ||
                !Fields(versioning, "snapshot", "snapshotVersions", "lastUpdated")) return false;
            var snapshot = versioning.Element(ns + "snapshot");
            var entries = versioning.Element(ns + "snapshotVersions");
            if (snapshot is null || entries is null ||
                !Fields(snapshot, "timestamp", "buildNumber", "localCopy") ||
                Value(snapshot, "localCopy") is not (null or "false" or "0")) return false;
            var timestamp = Value(snapshot, "timestamp");
            var buildText = Value(snapshot, "buildNumber");
            var resolved = coordinates.Version[..^9] + "-" + timestamp + "-" + buildText;
            if (!MavenCoordinates.TrySnapshotVersion(coordinates.Version, resolved,
                out var validTimestamp, out var build)) return false;
            var children = entries.Elements().ToArray();
            if (children.Length is 0 or > MaxEntries || children.Any(x => x.Name != ns + "snapshotVersion") ||
                entries.Nodes().OfType<XText>().Any(x => !string.IsNullOrWhiteSpace(x.Value)))
                return false;
            var variants = new List<SnapshotVersion>();
            var identities = new HashSet<(string?, string)>();
            foreach (var entry in children)
            {
                if (!Fields(entry, "classifier", "extension", "value", "updated")) return false;
                var classifier = Value(entry, "classifier");
                if (classifier == "") classifier = null;
                var extension = Value(entry, "extension");
                var value = Value(entry, "value");
                var at = Value(entry, "updated");
                if (extension is null || value is null || !ValidTimestamp(at) ||
                    classifier is not null && !MavenCoordinates.ValidSegment(classifier) ||
                    MavenEndpoints.AssetMediaType(extension) is null ||
                    !MavenCoordinates.TrySnapshotVersion(coordinates.Version, value,
                        out var entryTimestamp, out var entryBuild) ||
                    CompareSnapshot(entryTimestamp, entryBuild, validTimestamp, build) > 0 ||
                    !identities.Add((classifier, extension))) return false;
                var path = MavenCoordinates.RepositoryPath(new MavenCoordinates.Artifact(
                    coordinates.GroupId, coordinates.ArtifactId, coordinates.Version,
                    classifier, extension, value));
                if (!MavenEndpoints.ValidAssetPath(path, out _)) return false;
                variants.Add(new SnapshotVersion(classifier, extension, value, at!, path));
            }
            if (!variants.Any(x => x.Classifier is null && x.Extension == "pom" && x.Value == resolved))
                return false;
            document = new Document(coordinates, [], null, null, updated!, validTimestamp, build, variants);
            return true;
        }
        catch (XmlException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static int CompareSnapshot(string aTimestamp, int aBuild, string bTimestamp, int bBuild)
    {
        var comparison = StringComparer.Ordinal.Compare(aTimestamp, bTimestamp);
        return comparison == 0 ? aBuild.CompareTo(bBuild) : comparison;
    }

    /// <summary>Reject stale metadata rather than silently removing published versions or variants.</summary>
    public static bool Advances(Document previous, Document next)
    {
        if (previous.Coordinates != next.Coordinates ||
            StringComparer.Ordinal.Compare(next.LastUpdated, previous.LastUpdated) < 0) return false;
        if (next.Coordinates.Version is null)
        {
            if (previous.Versions.Except(next.Versions, StringComparer.Ordinal).Any()) return false;
            if (previous.Latest is not null && previous.Latest != next.Latest &&
                (next.Latest is null || previous.Versions.Contains(next.Latest, StringComparer.Ordinal)))
                return false;
            if (previous.Release is not null && previous.Release != next.Release &&
                (next.Release is null || previous.Versions.Contains(next.Release, StringComparer.Ordinal)))
                return false;
            return true;
        }
        if (CompareSnapshot(next.Timestamp!, next.BuildNumber,
            previous.Timestamp!, previous.BuildNumber) < 0 || next.BuildNumber < previous.BuildNumber)
            return false;
        foreach (var old in previous.SnapshotVersions)
        {
            var current = next.SnapshotVersions.SingleOrDefault(x =>
                x.Classifier == old.Classifier && x.Extension == old.Extension);
            if (current is null || StringComparer.Ordinal.Compare(current.Updated, old.Updated) < 0)
                return false;
            MavenCoordinates.TrySnapshotVersion(next.Coordinates.Version, current.Value,
                out var newTimestamp, out var newBuild);
            MavenCoordinates.TrySnapshotVersion(next.Coordinates.Version, old.Value,
                out var oldTimestamp, out var oldBuild);
            if (CompareSnapshot(newTimestamp, newBuild, oldTimestamp, oldBuild) < 0) return false;
        }
        return true;
    }

    public static bool ReferencesValid(MavenMetadataEntry entry, IPackageDatabase db) =>
        TryReferencedPaths(entry, db, out _);

    /// <summary>Expand only committed, same-scope identities, with one aggregate work bound.</summary>
    public static bool TryReferencedPaths(MavenMetadataEntry entry, IPackageDatabase db,
        out IReadOnlyList<string> paths)
    {
        paths = [];
        if (!ValidRecord(entry) || entry.Scope != "" &&
            db.Feed(entry.Scope) is not { Kind: "maven", Mode: "hosted", Visibility: "private" })
            return false;
        if (MavenGroupMetadata.TryDocument(entry, out _))
            return MavenGroupMetadata.TryReferencedPaths(entry, db, out paths);
        TryParseAtPath(Convert.FromBase64String(entry.ContentBase64), entry.Path, out var document);
        var coordinates = document!.Coordinates;
        var references = new HashSet<string>(StringComparer.Ordinal);
        if (coordinates!.Version is not null)
        {
            foreach (var variant in document!.SnapshotVersions) references.Add(variant.Path);
        }
        else foreach (var version in document!.Versions)
        {
            if (!MavenCoordinates.IsSnapshot(version))
            {
                references.Add(MavenCoordinates.RepositoryPath(new MavenCoordinates.Artifact(
                    coordinates.GroupId, coordinates.ArtifactId, version, null, "pom")));
            }
            else
            {
                var snapshot = db.GetMavenMetadata(entry.Scope,
                    PathFor(coordinates.GroupId, coordinates.ArtifactId, version));
                if (!ValidRecord(snapshot)) return false;
                TryParseAtPath(Convert.FromBase64String(snapshot!.ContentBase64), snapshot.Path, out var snapshotDoc);
                if (snapshotDoc!.Coordinates != new Coordinates(coordinates.GroupId,
                    coordinates.ArtifactId, version)) return false;
                foreach (var variant in snapshotDoc!.SnapshotVersions) references.Add(variant.Path);
            }
            if (references.Count > MaxEntries) return false;
        }
        foreach (var path in references)
        {
            if (entry.Scope == "")
            {
                if (db.GetMavenAsset(path) is not { } asset || !MavenEndpoints.ValidRecord(asset)) return false;
            }
            else if (db.GetFeedAsset(entry.Scope, "maven", path) is not { } asset ||
                !FeedAssetEntry.Valid(asset)) return false;
        }
        paths = references.ToArray();
        return true;
    }

    public static bool HasInventory(IPackageDatabase db, string scope, Coordinates coordinates)
    {
        var versions = scope == "" ? db.MavenVersions(coordinates.GroupId, coordinates.ArtifactId) :
            db.FeedMavenVersions(scope, coordinates.GroupId, coordinates.ArtifactId);
        return coordinates.Version is null ? versions.Count > 0 :
            versions.Contains(coordinates.Version, StringComparer.Ordinal);
    }

    /// <summary>A single wire metadata file cannot represent two distinct GA/GAV identities.</summary>
    public static bool PathConflicts(IPackageDatabase db, string scope, string path, Coordinates selected)
    {
        if (PathCandidates(path).Any(candidate => candidate != selected && HasInventory(db, scope, candidate)))
            return true;
        var stored = db.GetMavenMetadata(scope, path);
        return stored is not null && (!ValidRecord(stored) ||
            !TryParseAtPath(Convert.FromBase64String(stored.ContentBase64), path, out var document) ||
            document!.Coordinates != selected);
    }

    public static byte[] GeneratedVersions(Coordinates coordinates, IReadOnlyList<string> versions) =>
        Encoding.UTF8.GetBytes(new XDocument(new XElement("metadata",
            new XElement("groupId", coordinates.GroupId),
            new XElement("artifactId", coordinates.ArtifactId),
            new XElement("versioning", new XElement("versions",
                versions.Select(x => new XElement("version", x))))))
            .ToString(SaveOptions.DisableFormatting));
}
