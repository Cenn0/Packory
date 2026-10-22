using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Packory;

/// <summary>
/// Maven G-level metadata maps plugin prefixes to artifact IDs. It shares the
/// durable scoped metadata ledger with A/V-level metadata, but has separate
/// identity and collision checks. Publisher XML is retained byte-for-byte.
/// </summary>
public static class MavenGroupMetadata
{
    public sealed record Plugin(string Name, string Prefix, string ArtifactId);
    public sealed record Document(string GroupId, IReadOnlyList<Plugin> Plugins);

    public static bool TryParsePath(string? path, out string? group)
    {
        group = null;
        if (path is null || path.Length > 1024 || path.StartsWith('/') ||
            path.Contains('%') || path.Contains('\\') || path.Any(char.IsControl) ||
            !path.EndsWith("/maven-metadata.xml", StringComparison.Ordinal)) return false;
        var parts = path[..^"/maven-metadata.xml".Length].Split('/');
        if (parts.Length is < 1 or > 18 || parts.Any(x =>
            !MavenCoordinates.ValidSegment(x) || x.Contains('.'))) return false;
        var candidate = string.Join('.', parts);
        if (!MavenCoordinates.ValidGroup(candidate)) return false;
        group = candidate;
        return true;
    }

    public static bool TryParseAtPath(ReadOnlyMemory<byte> bytes, string path, out Document? document)
    {
        document = null;
        if (!TryParsePath(path, out var group) || bytes.IsEmpty ||
            bytes.Length > MavenMetadata.MaxBytes) return false;
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MavenMetadata.MaxBytes,
                MaxCharactersFromEntities = 0
            });
            var xml = XDocument.Load(reader, LoadOptions.None);
            var root = xml.Root;
            if (root is null || root.Name.LocalName != "metadata" ||
                root.Name.NamespaceName is not ("" or "http://maven.apache.org/METADATA/1.1.0") ||
                root.Attributes().Any(x => !x.IsNamespaceDeclaration &&
                    !(x.Name.LocalName == "modelVersion" && x.Value is "1.0.0" or "1.1.0")) ||
                root.Elements().Any(x => x.Name.Namespace != root.Name.Namespace ||
                    x.Name.LocalName is not ("groupId" or "plugins")) ||
                root.Elements().GroupBy(x => x.Name.LocalName).Any(x => x.Count() != 1) ||
                root.Nodes().OfType<XText>().Any(x => !string.IsNullOrWhiteSpace(x.Value)))
                return false;
            var ns = root.Name.Namespace;
            var suppliedGroup = root.Element(ns + "groupId");
            if (suppliedGroup is not null &&
                (suppliedGroup.HasElements || suppliedGroup.Value != group)) return false;
            var plugins = root.Element(ns + "plugins");
            if (plugins is null ||
                plugins.Nodes().OfType<XText>().Any(x => !string.IsNullOrWhiteSpace(x.Value))) return false;
            var nodes = plugins.Elements().ToArray();
            if (nodes.Length is < 1 or > MavenMetadata.MaxEntries ||
                nodes.Any(x => x.Name != ns + "plugin")) return false;
            var output = new List<Plugin>(nodes.Length);
            var prefixes = new HashSet<string>(StringComparer.Ordinal);
            var artifacts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                if (node.Attributes().Any(x => !x.IsNamespaceDeclaration) ||
                    node.Nodes().OfType<XText>().Any(x => !string.IsNullOrWhiteSpace(x.Value)) ||
                    node.Elements().Any(x => x.Name.Namespace != ns ||
                        x.Name.LocalName is not ("name" or "prefix" or "artifactId")) ||
                    node.Elements().Count() is < 2 or > 3 ||
                    node.Elements().GroupBy(x => x.Name.LocalName).Any(x => x.Count() != 1) ||
                    node.Elements().Any(x => x.HasElements || x.HasAttributes))
                    return false;
                var name = node.Element(ns + "name")?.Value ?? "";
                var prefix = node.Element(ns + "prefix")?.Value;
                var artifact = node.Element(ns + "artifactId")?.Value;
                if (name.Length > 256 || name.Any(char.IsControl) ||
                    !MavenCoordinates.ValidSegment(prefix) ||
                    !MavenCoordinates.ValidSegment(artifact) ||
                    !prefixes.Add(prefix!) || !artifacts.Add(artifact!)) return false;
                output.Add(new Plugin(name, prefix!, artifact!));
            }
            document = new Document(group!, output);
            return true;
        }
        catch (XmlException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public static bool TryDocument(MavenMetadataEntry? entry, out Document? document)
    {
        document = null;
        if (entry is null || entry.Size <= 0 || entry.Size > MavenMetadata.MaxBytes ||
            !ArtifactDigests.ValidSha256(entry.Sha256)) return false;
        try
        {
            var bytes = Convert.FromBase64String(entry.ContentBase64);
            return bytes.LongLength == entry.Size &&
                Convert.ToBase64String(bytes) == entry.ContentBase64 &&
                Convert.ToHexStringLower(SHA256.HashData(bytes)) == entry.Sha256 &&
                TryParseAtPath(bytes, entry.Path, out document);
        }
        catch (FormatException) { return false; }
    }

    public static bool Advances(Document old, Document next)
    {
        if (old.GroupId != next.GroupId) return false;
        return old.Plugins.All(plugin => next.Plugins.Any(current =>
            current.Prefix == plugin.Prefix && current.ArtifactId == plugin.ArtifactId));
    }

    public static bool PathConflicts(IPackageDatabase db, string scope, string path)
    {
        if (MavenMetadata.PathCandidates(path).Any(candidate =>
            MavenMetadata.HasInventory(db, scope, candidate))) return true;
        var previous = db.GetMavenMetadata(scope, path);
        return previous is not null && !TryDocument(previous, out _);
    }

    public static bool TryReferencedPaths(MavenMetadataEntry entry, IPackageDatabase db,
        out IReadOnlyList<string> paths)
    {
        paths = [];
        if (!TryDocument(entry, out var document)) return false;
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plugin in document!.Plugins)
        {
            var versions = entry.Scope == "" ?
                db.MavenVersions(document.GroupId, plugin.ArtifactId) :
                db.FeedMavenVersions(entry.Scope, document.GroupId, plugin.ArtifactId);
            if (versions.Count == 0 || versions.Count > MavenMetadata.MaxEntries) return false;
            var matched = false;
            foreach (var version in versions)
            {
                if (MavenCoordinates.IsSnapshot(version)) continue;
                var path = MavenCoordinates.RepositoryPath(new MavenCoordinates.Artifact(
                    document.GroupId, plugin.ArtifactId, version, null, "pom"));
                if (entry.Scope == "" ? db.GetMavenAsset(path) is { } a &&
                    MavenEndpoints.ValidRecord(a) : db.GetFeedAsset(entry.Scope, "maven", path)
                    is { } f && FeedAssetEntry.Valid(f))
                {
                    files.Add(path);
                    matched = true;
                    break;
                }
            }
            if (!matched) return false;
        }
        paths = files.ToArray();
        return true;
    }
}
