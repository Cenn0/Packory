using System.ComponentModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
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
        IReadOnlyList<SkillFile> Files, IReadOnlyList<string> Directories)
    {
        public string ArchiveSha256 { get; init; } = "";
    }

    public static string UriFor(string version, string name, string path = "SKILL.md") =>
        $"skill://packory/{version}/{name}/" +
        string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    public static bool TryParseUri(string? uri, out string version, out string name)
    {
        if (!TryParseResourceUri(uri, out version, out name, out var path) || path != "SKILL.md")
        {
            version = name = "";
            return false;
        }
        return true;
    }

    // Parse the original spelling before System.Uri can normalize dot segments.
    // Decode each segment once: an encoded separator cannot create a new level.
    public static bool TryParseResourceUri(string? uri, out string version, out string name,
        out string path)
    {
        version = name = path = "";
        const string origin = "skill://packory/";
        if (uri is null || uri.Length > 8192 ||
            !uri.StartsWith(origin, StringComparison.Ordinal) || uri.EndsWith('/') ||
            uri.Contains('?') || uri.Contains('#') || uri.Contains('\\') ||
            uri.Any(char.IsControl)) return false;
        var raw = uri[origin.Length..].Split('/');
        if (raw.Length is < 2 or > 258 || !Names.Version(raw[0]) || !Names.Skill(raw[1]))
            return false;
        var parts = new List<string>();
        foreach (var segment in raw.Skip(2))
        {
            var decoded = Uri.UnescapeDataString(segment);
            if (decoded.Length == 0 || decoded is "." or ".." ||
                decoded.Any(char.IsControl) || decoded.Contains('/') || decoded.Contains('\\') ||
                decoded.Contains(':') || !string.Equals(Uri.EscapeDataString(decoded), segment,
                    StringComparison.OrdinalIgnoreCase)) return false;
            parts.Add(decoded);
        }
        var relative = string.Join("/", parts);
        if (relative.Length > 512) return false;
        version = raw[0];
        name = raw[1];
        path = relative;
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
            var frontmatter = ReadFrontmatter(new UTF8Encoding(false, true).GetString(manifest.Bytes));
            if (frontmatter["name"]?.GetValue<string>() != name ||
                frontmatter["description"]?.GetValue<string>() is not { Length: > 0 })
                return null;
            return new SkillSnapshot(name, version, frontmatter, files,
                directories.OrderBy(x => x, StringComparer.Ordinal).ToArray())
            {
                ArchiveSha256 = Convert.ToHexStringLower(SHA256.HashData(publishedBytes))
            };
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
        var remainingNodes = 4096;
        return (JsonObject)ToNode(mapping, 0, ref remainingNodes)!;
    }

    private static JsonNode? ToNode(YamlNode node, int depth, ref int remainingNodes)
    {
        if (depth > 12 || --remainingNodes < 0)
            throw new InvalidDataException("Frontmatter exceeds structural limits.");
        if (node is YamlMappingNode map)
        {
            if (map.Children.Count > 100) throw new InvalidDataException("Oversized frontmatter.");
            var obj = new JsonObject();
            foreach (var (key, value) in map.Children)
            {
                if (key is not YamlScalarNode scalar || scalar.Value is not { } text ||
                    text.Length > 128 || obj.ContainsKey(text))
                    throw new InvalidDataException("Invalid or duplicate YAML key.");
                obj[text] = ToNode(value, depth + 1, ref remainingNodes);
            }
            return obj;
        }
        if (node is YamlSequenceNode seq)
        {
            if (seq.Children.Count > 100) throw new InvalidDataException("Oversized list.");
            var arr = new JsonArray();
            foreach (var child in seq.Children) arr.Add(ToNode(child, depth + 1, ref remainingNodes));
            return arr;
        }
        if (node is YamlScalarNode s)
        {
            if (s.Value?.Length > 65536) throw new InvalidDataException("Oversized scalar.");
            var value = s.Value ?? "";
            var tag = s.Tag.ToString();
            if (tag is "tag:yaml.org,2002:str" or "!") return JsonValue.Create(value);
            var isNull = value is "" or "~" or "null" or "Null" or "NULL";
            var isBoolean = value is "true" or "True" or "TRUE" or "false" or "False" or "FALSE";
            // Explicit tags constrain the value's type. An invalid !!int must
            // never fall through to bool/null/string inference, for example.
            if (tag == "tag:yaml.org,2002:null")
            {
                if (!isNull) throw new InvalidDataException("Invalid YAML null scalar.");
                return null;
            }
            if (tag == "tag:yaml.org,2002:bool")
            {
                if (!isBoolean) throw new InvalidDataException("Invalid YAML boolean scalar.");
                return JsonValue.Create(value[0] is 't' or 'T');
            }
            if (tag is "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float")
            {
                var integerOnly = tag == "tag:yaml.org,2002:int";
                var numeric = NormalizeYamlNumber(value, integerOnly, allowBasedInteger: integerOnly);
                if (numeric is null) throw new InvalidDataException("Invalid JSON-compatible YAML numeric scalar.");
                return JsonNode.Parse(numeric);
            }
            if (s.Style == YamlDotNet.Core.ScalarStyle.Plain)
            {
                if (isNull) return null;
                if (isBoolean) return JsonValue.Create(value[0] is 't' or 'T');
                if (NormalizeYamlNumber(value, false) is { } numeric) return JsonNode.Parse(numeric);
            }
            return JsonValue.Create(value)!;
        }
        throw new InvalidDataException("Unsupported YAML alias/tag.");
    }

    // Convert finite YAML Core numeric spellings to exact JSON lexemes. Never
    // round through decimal/double: +1e-100 and .000...001 must retain their value.
    private static string? NormalizeYamlNumber(string value, bool integerOnly, bool allowBasedInteger = true)
    {
        if (value.Length == 0) return null;
        if (value.StartsWith("0x", StringComparison.Ordinal) || value.StartsWith("0o", StringComparison.Ordinal))
        {
            if (!allowBasedInteger) return null;
            var bits = value[1] == 'x' ? 4 : 3;
            if (value.Length == 2) return null;
            // Pack the digits in linear time, including integers outside Int64.
            var bytes = new byte[((value.Length - 2) * bits + 7) / 8];
            var bit = 0;
            for (var i = value.Length - 1; i >= 2; i--, bit += bits)
            {
                var c = value[i];
                var digit = c is >= '0' and <= '9' ? c - '0' :
                    c is >= 'a' and <= 'f' ? c - 'a' + 10 :
                    c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
                if (digit < 0 || digit >= (1 << bits)) return null;
                bytes[bit / 8] |= (byte)(digit << (bit % 8));
                if (bit % 8 + bits > 8) bytes[bit / 8 + 1] |= (byte)(digit >> (8 - bit % 8));
            }
            return new System.Numerics.BigInteger(bytes, isUnsigned: true).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }
        var index = value[0] is '+' or '-' ? 1 : 0;
        var integerStart = index;
        while (index < value.Length && char.IsAsciiDigit(value[index])) index++;
        var integerEnd = index;
        var fractionStart = -1;
        if (!integerOnly && index < value.Length && value[index] == '.')
        {
            fractionStart = ++index;
            while (index < value.Length && char.IsAsciiDigit(value[index])) index++;
        }
        var fractionEnd = index;
        if (integerStart == integerEnd && (fractionStart < 0 || fractionStart == fractionEnd)) return null;
        var exponentStart = index;
        if (!integerOnly && index < value.Length && value[index] is 'e' or 'E')
        {
            index++;
            if (index < value.Length && value[index] is '+' or '-') index++;
            var exponentDigits = index;
            while (index < value.Length && char.IsAsciiDigit(value[index])) index++;
            if (exponentDigits == index) return null;
        }
        if (index != value.Length) return null;
        while (integerStart < integerEnd && value[integerStart] == '0') integerStart++;
        var integer = integerStart == integerEnd ? "0" : value[integerStart..integerEnd];
        var fraction = fractionStart < 0 ? "" : "." +
            (fractionStart == fractionEnd ? "0" : value[fractionStart..fractionEnd]);
        return (value[0] == '-' ? "-" : "") + integer + fraction + value[exponentStart..];
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
        BlobStore blobs, SkillSecurityStore security, Settings settings, string? cursor = null)
    {
        if (!TryParseResourceUri(uri, out var version, out var name, out var directory))
            return null;
        var snapshot = Get(name, version, db, blobs, security, settings);
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
        var baseUri = directory.Length == 0 ? $"skill://packory/{version}/{name}" :
            UriFor(version, name, directory);
        var cursorScope = "directory:" + Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(baseUri + "\n" + snapshot.ArchiveSha256)));
        var after = McpSkillCursor.Decode(cursor, cursorScope);
        if (after is { } marker && (marker.Second != "" || !children.ContainsKey(marker.First)))
            throw McpSkillCursor.Invalid();
        var page = children.Where(x => after is null ||
            StringComparer.Ordinal.Compare(x.Key, after.Value.First) > 0).Take(101).ToArray();
        foreach (var (child, isDirectory) in page.Take(100))
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
        var response = new JsonObject
        {
            ["resultType"] = "complete",
            ["resources"] = resources,
            ["cacheScope"] = "private",
            ["ttlMs"] = 0
        };
        if (page.Length > 100)
            response["nextCursor"] = McpSkillCursor.Encode(cursorScope, page[99].Key);
        return response;
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
        options.Filters.Request.ReadResourceFilters.Add(next => (request, ct) =>
        {
            var uri = request.Params?.Uri;
            if (uri is null || !uri.StartsWith("skill:", StringComparison.OrdinalIgnoreCase))
                return next(request, ct);
            ct.ThrowIfCancellationRequested();
            if (!PackoryMcpCatalog.TryParseResourceUri(uri, out var version, out var name, out var path) ||
                path.Length == 0)
                throw new McpProtocolException("Invalid skill resource URI.", McpErrorCode.InvalidParams);
            // Use the raw, canonical URI to bind the file. URI-template decoding
            // must not normalize away traversal or decode percent escapes twice.
            return ValueTask.FromResult(new ReadResourceResult
            {
                Contents = [PackoryMcpResources.SkillFile(version, name, path,
                    db, blobs, security, settings)],
                CacheScope = CacheScope.Private,
                TimeToLive = TimeSpan.Zero
            });
        });
        options.RequestHandlers ??= new List<McpServerRequestHandler>();
        options.RequestHandlers.Add(new McpServerRequestHandler
        {
            Method = "skills/list",
            Handler = (request, ct) =>
            {
                var cursor = McpSkillCursor.Parameter(request.Params, "cursor", maximum: 512);
                var after = McpSkillCursor.Decode(cursor, "skills");
                if (after is { } marker && (!Names.Skill(marker.First) || !Names.Version(marker.Second)))
                    throw McpSkillCursor.Invalid();
                var array = new JsonArray();
                // Bound the DB work and content verification per request. Invalid,
                // missing or revoked archives still advance the opaque keyset.
                const int MaxExamined = 200;
                var examined = 0;
                var nextName = after?.First;
                var nextVersion = after?.Second;
                var mayHaveMore = false;
                while (array.Count < 25 && examined < MaxExamined)
                {
                    ct.ThrowIfCancellationRequested();
                    var batchLimit = Math.Min(26, MaxExamined - examined);
                    var candidates = security.ReadablePage(nextName, nextVersion, batchLimit,
                        settings.SkillSecurityEnforced);
                    if (candidates.Count == 0)
                    {
                        mayHaveMore = false;
                        break;
                    }
                    foreach (var skill in candidates)
                    {
                        ct.ThrowIfCancellationRequested();
                        nextName = skill.Name;
                        nextVersion = skill.Version;
                        examined++;
                        var entry = PackoryMcpCatalog.Entry(skill.Name, skill.Version,
                            db, blobs, security, settings);
                        if (entry is not null) array.Add(entry);
                        if (array.Count == 25) break;
                    }
                    // A full DB batch or an early stop due to output capacity
                    // may have more records. Consumers can safely request a
                    // final empty page; a cursor never grants authorization.
                    mayHaveMore = array.Count == 25
                        ? security.ReadablePage(nextName, nextVersion, 1,
                            settings.SkillSecurityEnforced).Count > 0
                        : candidates.Count == batchLimit;
                    if (!mayHaveMore) break;
                }
                var response = new JsonObject
                {
                    ["resultType"] = "complete",
                    ["skills"] = array,
                    ["cacheScope"] = "private",
                    ["ttlMs"] = 0
                };
                if (mayHaveMore && nextName is not null && nextVersion is not null)
                    response["nextCursor"] = McpSkillCursor.Encode("skills", nextName, nextVersion);
                return ValueTask.FromResult<JsonNode?>(response);
            }
        });
        options.RequestHandlers.Add(new McpServerRequestHandler
        {
            Method = "skills/get",
            Handler = (request, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var uri = McpSkillCursor.Parameter(request.Params, "uri", required: true);
                if (!PackoryMcpCatalog.TryParseUri(uri, out var version, out var name))
                    throw new McpProtocolException("Unknown skill URI.", McpErrorCode.InvalidParams);
                var entry = PackoryMcpCatalog.Entry(name, version, db, blobs, security, settings)
                    ?? throw new McpProtocolException("Unknown or unpublished skill.", McpErrorCode.InvalidParams);
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
                var uri = McpSkillCursor.Parameter(request.Params, "uri", required: true);
                var cursor = McpSkillCursor.Parameter(request.Params, "cursor");
                var directory = PackoryMcpCatalog.ReadDirectory(
                    uri, db, blobs, security, settings, cursor)
                    ?? throw new McpProtocolException("Unknown or unavailable skill directory.", McpErrorCode.InvalidParams);
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
    [Description("Search locally indexed artifacts across the implemented ecosystems, including Maven, Raw and approved Skills. Optional kind filter and opaque nextCursor pagination; no remote fetch. Global catalog only; private feed entries are not included.")]
    public static McpArtifactCatalog.SearchPage SearchArtifacts(string query, Settings settings,
        int limit = 30, string? kind = null, string? cursor = null) =>
        McpArtifactCatalog.Search(settings, query, kind, limit, cursor);

    [McpServerTool(Name = "list_approved_skills", ReadOnly = true)]
    [Description("Discover versioned, approved agent Skills from a bounded database-side keyset page. Follow nextCursor to continue. No archive execution.")]
    public static object ListApprovedSkills(SkillSecurityStore security, Settings settings,
        int limit = 50, string? cursor = null)
    {
        if (limit is < 1 or > 99) throw McpSkillCursor.Invalid();
        var marker = McpSkillCursor.Decode(cursor, "approved-skill-tool");
        if (marker is { } selected &&
            (!Names.Skill(selected.First) || !Names.Version(selected.Second)))
            throw McpSkillCursor.Invalid();
        var rows = security.ReadablePage(marker?.First, marker?.Second,
            limit + 1, settings.SkillSecurityEnforced);
        return new
        {
            items = rows.Take(limit).ToArray(),
            nextCursor = rows.Count > limit
                ? McpSkillCursor.Encode("approved-skill-tool",
                    rows[limit - 1].Name, rows[limit - 1].Version)
                : null
        };
    }

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
        var skill = security.Readable(name, version, settings.SkillSecurityEnforced);
        if (skill is null) return new { error = "Skill is not available" };
        return new { skill.Name, skill.Version, skill.Description, manifestUri =
            PackoryMcpCatalog.UriFor(version, name), sha256 = security.Get(name, version)?.Sha256,
            approval = settings.SkillSecurityEnforced ? "APPROVED" : "NOT_ENFORCED" };
    }

    [McpServerTool(Name = "artifact_versions", ReadOnly = true)]
    [Description("List global catalog versions of an exact native package identity (Maven: groupId:artifactId), including approved Skills. Results use lexical version order, not newest-release semantics. Follow nextCursor until absent.")]
    public static McpArtifactCatalog.VersionPage ArtifactVersions(string kind, string name,
        Settings settings, int limit = 100, string? cursor = null) =>
        McpArtifactCatalog.Versions(settings, kind, name, limit, cursor);
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
        if (path.Length > 512 || path.Contains('\\') || path.Contains(':') ||
            path.Any(char.IsControl) || path.StartsWith('/') ||
            path.Split('/').Any(x => x is "." or ".." or "") ||
            !Names.Skill(name) || !Names.Version(version))
            throw new McpProtocolException("Invalid skill resource.", McpErrorCode.InvalidParams);
        var snapshot = PackoryMcpCatalog.Get(name, version, db, blobs, security, settings)
            ?? throw new McpProtocolException("Unknown or unpublished skill.", McpErrorCode.InvalidParams);
        var content = snapshot.Files.SingleOrDefault(x => x.Path == path)
            ?? throw new McpProtocolException("Unknown skill file.", McpErrorCode.InvalidParams);
        var uri = PackoryMcpCatalog.UriFor(version, name, path);
        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return new TextResourceContents
                {
                    Uri = uri,
                    MimeType = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                        ? "text/markdown" : path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                            ? "application/json" : "text/plain",
                    Text = new UTF8Encoding(false, true).GetString(content.Bytes)
                };
            }
            catch (DecoderFallbackException)
            {
                // A filename suffix is not an encoding guarantee. Binary output
                // preserves the exact bytes covered by the manifest digest.
            }
        }
        return BlobResourceContents.FromBytes(content.Bytes, uri, "application/octet-stream");
    }

    [McpServerResource(UriTemplate = "packory://catalog/info", Name = "Packory Catalog", MimeType = "application/json")]
    [Description("Provides implemented registry ecosystem names and scoped capability limits, without secrets or private-feed metadata.")]
    public static string CatalogInfo() =>
        JsonSerializer.Serialize(new { name = "Packory",
            ecosystems = RegistryCapabilities.All.Select(x => x.Ecosystem).ToArray(),
            protocols = RegistryCapabilities.All,
            mode = "read-only Packory catalog; no remote MCP gateway",
            catalogScope = "global hosted/cached metadata; private feeds excluded",
            ordering = "lexical kind/name/version; current visibility rechecked per page" });
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
