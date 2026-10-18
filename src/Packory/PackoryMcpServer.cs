using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using YamlDotNet.RepresentationModel;

namespace Packory;

/// <summary>
/// MCP server features are deliberately read-only. All MCP paths use the exact
/// server-side security gate used by the REST download endpoint.
/// </summary>
public static class PackoryMcpCatalog
{
    public sealed record SkillFile(string Path, byte[] Bytes);
    public sealed record SkillSnapshot(string Name, string Version, JsonObject Frontmatter,
        IReadOnlyList<SkillFile> Files, IReadOnlyList<string> Directories);

    public static IReadOnlyList<SkillEntry> Approved(IPackageDatabase db, SkillSecurityStore security,
        Settings settings) =>
        db.Skills().Where(x => !settings.SkillSecurityEnforced ||
                security.IsApproved(x.Name, x.Version)).ToArray();

    public static string UriFor(string version, string name, string path = "SKILL.md") =>
        $"skill://packory/{version}/{name}/{path}";

    public static bool TryParseUri(string? uri, out string version, out string name)
    {
        version = name = "";
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != "skill" || parsed.Host != "packory" ||
            !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
            return false;
        var segments = parsed.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 3 || segments[2] != "SKILL.md" ||
            !Names.Version(segments[0]) || !Names.Skill(segments[1]))
            return false;
        version = segments[0];
        name = segments[1];
        return true;
    }

    public static SkillSnapshot? Get(string name, string version, IPackageDatabase db,
        BlobStore blobs, SkillSecurityStore security, Settings settings)
    {
        if (!Names.Skill(name) || !Names.Version(version)) return null;
        if (settings.SkillSecurityEnforced && !security.IsApproved(name, version)) return null;
        if (!db.Skills(name).Any(s => s.Version == version)) return null;
        // The approval state's SHA-256 must still match the exact archive
        // being parsed. Never trust a cached S3/file blob merely because
        // the database record says APPROVED.
        var publishedBytes = SkillEndpoints.ReadVerifiedPublishedBytes(
            name, version, blobs, security, settings);
        if (publishedBytes is null) return null;
        try
        {
            using var archive = new ZipArchive(
                new MemoryStream(publishedBytes, writable: false), ZipArchiveMode.Read);
            var files = new List<SkillFile>();
            var directories = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var prefix = archive.Entries.Any(e => e.FullName == name + "/SKILL.md")
                ? name + "/" : "";
            foreach (var entry in archive.Entries)
            {
                var original = entry.FullName;
                if (!original.StartsWith(prefix, StringComparison.Ordinal)) return null;
                var isDirectory = original.EndsWith('/');
                // Many ZIP producers include the enclosing skill folder as
                // an explicit entry. It represents this skill's own root,
                // not a child directory and must not invalidate the archive.
                if (isDirectory && prefix.Length > 0 && original == prefix)
                    continue;
                var path = original[prefix.Length..];
                if (isDirectory) path = path[..^1];
                if (path.Length == 0 || path.Length > 512 ||
                    path.StartsWith('/') || path.Contains('\\') || path.Contains(':') ||
                    path.Any(char.IsControl) ||
                    path.Split('/').Any(s => s is "." or ".." or "") ||
                    !unique.Add(path) ||
                    ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    return null;
                // An explicit directory is significant even when it has no
                // files. Implicit ancestors of all files are directories too.
                if (isDirectory)
                {
                    if (entry.Length != 0) return null;
                    directories.Add(path);
                }
                else
                {
                    if (entry.Length > 16 * 1024 * 1024) return null;
                    total += entry.Length;
                    if (total > 16 * 1024 * 1024 || files.Count >= 512) return null;
                    using var stream = entry.Open();
                    using var buffer = new MemoryStream();
                    stream.CopyTo(buffer);
                    if (buffer.Length != entry.Length) return null;
                    files.Add(new SkillFile(path, buffer.ToArray()));
                }
                for (var end = path.LastIndexOf('/'); end > 0; end = path.LastIndexOf('/', end - 1))
                    directories.Add(path[..end]);
                if (directories.Count > 4096) return null;
            }
            // File/directory name collisions have no portable semantics.
            if (files.Any(f => directories.Contains(f.Path))) return null;
            var manifest = files.SingleOrDefault(f => f.Path == "SKILL.md");
            if (manifest is null) return null;
            var frontmatter = ReadFrontmatter(Encoding.UTF8.GetString(manifest.Bytes));
            if (frontmatter["name"]?.GetValue<string>() != name ||
                frontmatter["description"]?.GetValue<string>() is not { Length: > 0 })
                return null;
            return new SkillSnapshot(name, version, frontmatter, files,
                directories.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException
            or ArgumentException or InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            return null;
        }
    }

    private static JsonObject ReadFrontmatter(string markdown)
    {
        var normalized = markdown.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            throw new InvalidDataException("Missing frontmatter.");
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0 || end > 256 * 1024)
            throw new InvalidDataException("Invalid frontmatter.");
        var yaml = new YamlStream();
        yaml.Load(new StringReader(normalized[4..end]));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode mapping)
            throw new InvalidDataException("Invalid YAML mapping.");
        return (JsonObject)ToNode(mapping, 0);
    }

    private static JsonNode ToNode(YamlNode node, int depth)
    {
        if (depth > 12) throw new InvalidDataException("Deep frontmatter is unsupported.");
        if (node is YamlMappingNode map)
        {
            if (map.Children.Count > 100) throw new InvalidDataException("Oversized frontmatter.");
            var obj = new JsonObject();
            foreach (var (key, value) in map.Children)
            {
                if (key is not YamlScalarNode scalar || scalar.Value is not { } text ||
                    text.Length > 128 || obj.ContainsKey(text))
                    throw new InvalidDataException("Invalid or duplicate YAML key.");
                obj[text] = ToNode(value, depth + 1);
            }
            return obj;
        }
        if (node is YamlSequenceNode seq)
        {
            if (seq.Children.Count > 100) throw new InvalidDataException("Oversized list.");
            var arr = new JsonArray();
            foreach (var child in seq.Children) arr.Add(ToNode(child, depth + 1));
            return arr;
        }
        if (node is YamlScalarNode s)
        {
            if (s.Value?.Length > 65536) throw new InvalidDataException("Oversized scalar.");
            var value = s.Value ?? "";
            if (s.Style == YamlDotNet.Core.ScalarStyle.Plain)
            {
                if (value == "true") return JsonValue.Create(true)!;
                if (value == "false") return JsonValue.Create(false)!;
                if (value is "null" or "~") return JsonValue.Create("")!;
                if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var number))
                    return JsonValue.Create(number)!;
            }
            return JsonValue.Create(value)!;
        }
        throw new InvalidDataException("Unsupported YAML alias/tag.");
    }

    public static JsonObject? Entry(string name, string version, IPackageDatabase db,
        BlobStore blobs, SkillSecurityStore security, Settings settings)
    {
        var snapshot = Get(name, version, db, blobs, security, settings);
        if (snapshot is null) return null;
        var files = new JsonArray();
        foreach (var file in snapshot.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            files.Add(new JsonObject
            {
                ["uri"] = UriFor(version, name, file.Path),
                ["digest"] = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(file.Bytes)),
                ["size"] = file.Bytes.Length
            });
        }
        return new JsonObject
        {
            ["uri"] = UriFor(version, name),
            ["frontmatter"] = snapshot.Frontmatter.DeepClone(),
            ["resources"] = files
        };
    }

    /// <summary>
    /// The optional Skills Extension directory method returns only direct
    /// children of a verified approved archive. Reject malformed or encoded
    /// path separators, traversal, URI suffixes and nonexistent directories.
    /// </summary>
    public static JsonObject? ReadDirectory(string? uri, IPackageDatabase db,
        BlobStore blobs, SkillSecurityStore security, Settings settings)
    {
        const string origin = "skill://packory/";
        if (uri is null || uri.Length > 2048 ||
            !uri.StartsWith(origin, StringComparison.Ordinal) ||
            uri.EndsWith("/", StringComparison.Ordinal) ||
            uri.Contains('?') || uri.Contains('#') || uri.Contains('\\') ||
            uri.Any(char.IsControl))
            return null;

        // Split BEFORE percent-unescaping: %2f and %5c must not create
        // synthetic directory levels, and encoded dot-segments must be denied.
        var raw = uri[origin.Length..].Split('/');
        if (raw.Length < 2 || raw.Length > 256 ||
            !Names.Version(raw[0]) || !Names.Skill(raw[1]))
            return null;
        var decoded = new List<string>();
        foreach (var item in raw.Skip(2))
        {
            var part = Uri.UnescapeDataString(item);
            if (string.IsNullOrEmpty(part) || part is "." or ".." ||
                part.Any(char.IsControl) || part.Contains('/') ||
                part.Contains('\\') || part.Contains(':') ||
                part.Contains('?') || part.Contains('#'))
                return null;
            // URI encoding may be used for spaces/unicode in archive names,
            // but only canonical escaped path segments may contain '%'.
            if (item.Contains('%') &&
                !string.Equals(Uri.EscapeDataString(part), item,
                    StringComparison.OrdinalIgnoreCase))
                return null;
            decoded.Add(part);
        }
        var directory = string.Join("/", decoded);
        if (directory.Length > 512) return null;
        var snapshot = Get(raw[1], raw[0], db, blobs, security, settings);
        if (snapshot is null ||
            (directory.Length > 0 && !snapshot.Directories.Contains(directory,
                StringComparer.Ordinal))) return null;

        var prefix = directory.Length == 0 ? "" : directory + "/";
        var children = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var folder in snapshot.Directories)
        {
            if (!folder.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var relative = folder[prefix.Length..];
            if (relative.Length > 0 && !relative.Contains('/'))
                children[relative] = true;
        }
        foreach (var file in snapshot.Files)
        {
            if (!file.Path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var relative = file.Path[prefix.Length..];
            if (relative.Length > 0 && !relative.Contains('/'))
            {
                if (!children.TryAdd(relative, false)) return null;
            }
        }
        var resources = new JsonArray();
        var baseUri = origin + raw[0] + "/" + raw[1] +
            (directory.Length == 0 ? "" : "/" + string.Join("/",
                decoded.Select(Uri.EscapeDataString)));
        foreach (var (child, isDirectory) in children)
        {
            var mime = isDirectory ? "inode/directory" :
                child.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? "text/markdown" :
                child.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" :
                child.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "text/plain" :
                "application/octet-stream";
            resources.Add(new JsonObject
            {
                ["uri"] = baseUri + "/" + Uri.EscapeDataString(child),
                ["name"] = child,
                ["mimeType"] = mime
            });
        }
        return new JsonObject
        {
            ["resultType"] = "complete",
            ["resources"] = resources,
            ["cacheScope"] = "private",
            ["ttlMs"] = 0
        };
    }

}

/// <summary>Explicit MCP Skills Extension handlers, with native C# SDK serving
/// tools, resources, prompts and Streamable HTTP transport.</summary>
public sealed class PackoryMcpOptionsConfigurator(
    IPackageDatabase db, BlobStore blobs, SkillSecurityStore security, Settings settings)
    : IConfigureOptions<McpServerOptions>
{
#pragma warning disable MCPEXP002
    public void Configure(McpServerOptions options)
    {
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Extensions ??= new Dictionary<string, object>();
        // The SDK's source-generated protocol serializer cannot serialize arbitrary anonymous objects.
        // A JsonElement is a supported DOM value and produces the exact advertised extension shape.
        options.Capabilities.Extensions["io.modelcontextprotocol/skills"] =
            JsonSerializer.SerializeToElement(new Dictionary<string, bool> { ["directoryRead"] = true });
        options.RequestHandlers ??= new List<McpServerRequestHandler>();
        options.RequestHandlers.Add(new McpServerRequestHandler
        {
            Method = "skills/list",
            Handler = (request, ct) =>
            {
                var cursor = request.Params?["cursor"]?.GetValue<string>();
                var start = 0;
                if (cursor is not null && (!int.TryParse(cursor, out start) || start < 0))
                    throw new ArgumentException("Invalid skills cursor.");
                var ordered = PackoryMcpCatalog.Approved(db, security, settings)
                    .OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Version, StringComparer.Ordinal).ToArray();
                var array = new JsonArray();
                foreach (var skill in ordered.Skip(start).Take(25))
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = PackoryMcpCatalog.Entry(skill.Name, skill.Version, db, blobs, security, settings);
                    if (entry is not null) array.Add(entry);
                }
                var response = new JsonObject
                {
                    ["resultType"] = "complete",
                    ["skills"] = array,
                    ["cacheScope"] = "private",
                    ["ttlMs"] = 0
                };
                if (start + 25 < ordered.Length) response["nextCursor"] = (start + 25).ToString();
                return ValueTask.FromResult<JsonNode?>(response);
            }
        });
        options.RequestHandlers.Add(new McpServerRequestHandler
        {
            Method = "skills/get",
            Handler = (request, ct) =>
            {
                var uri = request.Params?["uri"]?.GetValue<string>();
                if (!PackoryMcpCatalog.TryParseUri(uri, out var version, out var name))
                    throw new ArgumentException("Unknown skill URI.");
                var entry = PackoryMcpCatalog.Entry(name, version, db, blobs, security, settings)
                    ?? throw new ArgumentException("Unknown or unpublished skill.");
                return ValueTask.FromResult<JsonNode?>(new JsonObject
                {
                    ["resultType"] = "complete",
                    ["skill"] = entry,
                    ["cacheScope"] = "private",
                    ["ttlMs"] = 0
                });
            }
        });
        options.RequestHandlers.Add(new McpServerRequestHandler
        {
            Method = "resources/directory/read",
            Handler = (request, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                // The method applies to real directories only. A revoked
                // Skill or file URI must never yield listing metadata.
                var uri = request.Params?["uri"]?.GetValue<string>();
                var directory = PackoryMcpCatalog.ReadDirectory(
                    uri, db, blobs, security, settings)
                    ?? throw new ArgumentException("Unknown or unavailable skill directory.");
                return ValueTask.FromResult<JsonNode?>(directory);
            }
        });
    }
#pragma warning restore MCPEXP002
}

[McpServerToolType]
public sealed class PackoryMcpTools
{
    [McpServerTool(Name = "search_artifacts", ReadOnly = true)]
    [Description("Search Packory's locally indexed NuGet, npm, Cargo, PyPI, OCI and approved Skill revisions. No remote fetch or execution.")]
    public static object SearchArtifacts(string query, IPackageDatabase db,
        SkillSecurityStore security, Settings settings, int limit = 30)
    {
        var items = db.Search(query, null, Math.Clamp(limit, 1, 100))
            .Where(x => x.Kind != "skill" || !settings.SkillSecurityEnforced ||
                        security.IsApproved(x.Name, x.Version)).ToArray();
        return new { items };
    }

    [McpServerTool(Name = "list_approved_skills", ReadOnly = true)]
    [Description("Discover versioned, security-approved agent Skills available to read.")]
    public static object ListApprovedSkills(IPackageDatabase db, SkillSecurityStore security, Settings settings) =>
        new { items = PackoryMcpCatalog.Approved(db, security, settings) };

    [McpServerTool(Name = "verify_skill_integrity", ReadOnly = true)]
    [Description("Verify that a published, reviewed Skill ZIP still matches its immutable approved SHA-256. Does not execute or certify Skill instructions.")]
    public static object VerifySkillIntegrity(string name, string version, IPackageDatabase db,
        BlobStore blobs, SkillSecurityStore security, Settings settings)
    {
        if (!Names.Skill(name) || !Names.Version(version) ||
            !db.Skills(name).Any(x => x.Version == version) ||
            (settings.SkillSecurityEnforced && !security.IsApproved(name, version)))
            return new { status = "unavailable" } as object;
        var bytes = SkillEndpoints.ReadVerifiedPublishedBytes(name, version, blobs, security, settings);
        if (bytes is null)
            return new { status = "integrity_unavailable" };
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new
        {
            status = "digest_verified", name, version, sha256 = digest,
            approvedUnderPolicy = settings.SkillSecurityEnforced,
            safeToExecute = false,
            note = "Integrity and human release decision only; not malware-free or safe-to-execute certification."
        };
    }

    [McpServerTool(Name = "skill_metadata", ReadOnly = true)]
    [Description("Retrieve an approved skill's description, integrity digest and approval state; never executes it.")]
    public static object SkillMetadata(string name, string version, IPackageDatabase db,
        SkillSecurityStore security, Settings settings)
    {
        if (!Names.Skill(name) || !Names.Version(version)) return new { error = "Invalid skill identity" } as object;
        var skill = PackoryMcpCatalog.Approved(db, security, settings)
            .FirstOrDefault(x => x.Name == name && x.Version == version);
        if (skill is null) return new { error = "Skill is not available" };
        return new { skill.Name, skill.Version, skill.Description, manifestUri =
            PackoryMcpCatalog.UriFor(version, name), sha256 = security.Get(name, version)?.Sha256,
            approval = settings.SkillSecurityEnforced ? "APPROVED" : "NOT_ENFORCED" };
    }

    [McpServerTool(Name = "artifact_versions", ReadOnly = true)]
    [Description("List known versions of a named registry package, with skill versions filtered by security approval.")]
    public static object ArtifactVersions(string kind, string name, IPackageDatabase db,
        SkillSecurityStore security, Settings settings)
    {
        if (kind is not ("nuget" or "npm" or "cargo" or "pypi" or "oci" or "skill") ||
            name.Length > 214) return new { error = "Invalid identity" } as object;
        var versions = db.Versions(kind, name)
            .Where(v => kind != "skill" || !settings.SkillSecurityEnforced || security.IsApproved(name, v))
            .ToArray();
        return new { kind, name, versions };
    }
}

[McpServerResourceType]
public sealed class PackoryMcpResources
{
    [McpServerResource(UriTemplate = "skill://packory/{version}/{name}/{+path}",
        Name = "Approved Skill File", MimeType = "text/plain")]
    [Description("Read a specific file of an approved immutable skill. No script execution.")]
    public static ResourceContents SkillFile(string version, string name, string path,
        IPackageDatabase db, BlobStore blobs, SkillSecurityStore security, Settings settings)
    {
        if (path.Length > 512 || path.Contains('\\') || path.StartsWith('/') ||
            path.Split('/').Any(x => x is "." or ".." or "") ||
            !Names.Skill(name) || !Names.Version(version))
            throw new ArgumentException("Invalid skill resource.");
        var snapshot = PackoryMcpCatalog.Get(name, version, db, blobs, security, settings)
            ?? throw new ArgumentException("Unknown or unpublished skill.");
        var content = snapshot.Files.SingleOrDefault(x => x.Path == path)
            ?? throw new ArgumentException("Unknown skill file.");
        var uri = PackoryMcpCatalog.UriFor(version, name, path);
        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            return new TextResourceContents
            {
                Uri = uri,
                MimeType = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                    ? "text/markdown" : "text/plain",
                Text = new UTF8Encoding(false, true).GetString(content.Bytes)
            };
        return BlobResourceContents.FromBytes(content.Bytes, uri, "application/octet-stream");
    }

    [McpServerResource(UriTemplate = "packory://catalog/info", Name = "Packory Catalog", MimeType = "application/json")]
    [Description("Provides current registry supported ecosystem names, without secrets.")]
    public static string CatalogInfo() =>
        JsonSerializer.Serialize(new { name = "Packory",
            ecosystems = new[] { "nuget", "npm", "cargo", "pypi", "oci", "skill" },
            mode = "read-only MCP registry gateway; skill approval enforced server-side" });
}

[McpServerPromptType]
public sealed class PackoryMcpPrompts
{
    [McpServerPrompt(Name = "review_skill")]
    [Description("Review a version-pinned Skill's risk and integrity before the client requests consent to use it.")]
    public static ChatMessage ReviewSkill(string name, string version) =>
        new(ChatRole.User, $"Inspect skill {name}@{version} using the Packory skill_metadata tool, "+
            "skills/get and resources/read. Confirm the exact content digests and obtain the user's explicit "+
            "permission before activating any instructions or using any additional tools.");

    [McpServerPrompt(Name = "discover_packages")]
    [Description("Find packages in the local Packory registry without triggering any installation.")]
    public static ChatMessage DiscoverPackages(string query) =>
        new(ChatRole.User, $"Search Packory for '{query}'. Summarize indexed package versions and "+
            "their origin without executing packages or changing registry state.");
}
