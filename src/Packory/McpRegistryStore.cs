using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Packory;

/// <summary>
/// A deliberately narrow, remote-only subset of the MCP Registry server.json
/// format. Metadata is inert: Packory never connects to, proxies or executes
/// the server referenced by a manifest. Operator review is not ownership proof.
/// </summary>
public sealed record McpRegistryManifest(
    string Name, string Version, string Title, string Description,
    string RemoteUrl, string Json, string Sha256)
{
    public const string Schema = "https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json";
    private static readonly Regex NamePattern = new(
        @"\A(?:[a-z][a-z0-9-]*\.)+[a-z][a-z0-9-]*/[a-z0-9][a-z0-9._-]{0,63}\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex VersionPattern = new(
        @"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool ValidName(string? name) =>
        name is { Length: >= 5 and <= 180 } && NamePattern.IsMatch(name);
    public static bool ValidVersion(string? version) =>
        version is { Length: >= 5 and <= 128 } && VersionPattern.IsMatch(version);

    private static void UniqueProperties(JsonElement element, int depth = 0)
    {
        if (depth > 16) throw new InvalidDataException("Invalid manifest structure.");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate manifest field.");
                UniqueProperties(property.Value, depth + 1);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) UniqueProperties(child, depth + 1);
    }

    private static string ReadString(JsonElement obj, string key, int maxLength)
    {
        if (!obj.TryGetProperty(key, out var element) ||
            element.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Missing or invalid manifest field.");
        var value = element.GetString()!;
        if (value.Length == 0 || value.Length > maxLength ||
            value.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) ==
                System.Globalization.UnicodeCategory.Format))
            throw new InvalidDataException("Unsafe manifest text.");
        return value;
    }

    private static void Only(JsonElement obj, params string[] allowed)
    {
        if (obj.ValueKind != JsonValueKind.Object ||
            obj.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)))
            throw new InvalidDataException("Unsupported manifest property.");
    }

    private static bool SafeRemote(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.Query) ||
            uri.HostNameType != UriHostNameType.Dns || uri.Host.Length > 253 ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Contains('_') || uri.Host.Contains('%') ||
            !uri.Host.Contains('.') || IPAddress.TryParse(uri.Host, out _) ||
            url.Any(char.IsControl))
            return false;
        // No network calls are made here. A DNS name can still later resolve to
        // a private address; consumers must implement their own SSRF defenses.
        return true;
    }

    public static McpRegistryManifest Parse(byte[] bytes)
    {
        if (bytes.Length is < 10 or > 16384)
            throw new InvalidDataException("Manifest must be 10..16384 bytes.");
        try
        {
            var json = new UTF8Encoding(false, true).GetString(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                MaxDepth = 16, AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            var root = document.RootElement;
            UniqueProperties(root);
            Only(root, "$schema", "name", "version", "title", "description",
                "remotes", "repository");
            if (ReadString(root, "$schema", 160) != Schema)
                throw new InvalidDataException("Unsupported server.json schema.");
            var name = ReadString(root, "name", 180);
            var version = ReadString(root, "version", 128);
            if (!ValidName(name) || !ValidVersion(version))
                throw new InvalidDataException("Invalid MCP name or version.");
            var description = ReadString(root, "description", 100);
            var title = root.TryGetProperty("title", out _) ?
                ReadString(root, "title", 120) : name;
            if (!root.TryGetProperty("remotes", out var remotes) ||
                remotes.ValueKind != JsonValueKind.Array ||
                remotes.GetArrayLength() is < 1 or > 4)
                throw new InvalidDataException("Only bounded remote-only manifests are supported.");
            string? first = null;
            foreach (var remote in remotes.EnumerateArray())
            {
                Only(remote, "type", "url");
                if (ReadString(remote, "type", 32) != "streamable-http")
                    throw new InvalidDataException("Only Streamable HTTP remotes are supported.");
                var url = ReadString(remote, "url", 512);
                if (!SafeRemote(url))
                    throw new InvalidDataException("Unsafe remote endpoint.");
                first ??= url;
            }
            if (root.TryGetProperty("repository", out var repo))
            {
                Only(repo, "source", "url");
                if (ReadString(repo, "source", 32) != "github")
                    throw new InvalidDataException("Unsupported repository source.");
                var repoUrl = ReadString(repo, "url", 512);
                if (!Uri.TryCreate(repoUrl, UriKind.Absolute, out var repository) ||
                    repository.Scheme != "https" ||
                    !string.Equals(repository.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrEmpty(repository.Query) ||
                    !string.IsNullOrEmpty(repository.Fragment) ||
                    !string.IsNullOrEmpty(repository.UserInfo) ||
                    repository.AbsolutePath.Trim('/').Split('/').Length != 2)
                    throw new InvalidDataException("Invalid repository reference.");
            }
            return new McpRegistryManifest(name, version, title, description, first!,
                json, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (Exception e) when (e is JsonException or DecoderFallbackException)
        {
            throw new InvalidDataException("Invalid server.json JSON or UTF-8.");
        }
    }
}

public sealed record McpRegistryEntry(
    string Name, string Version, string Title, string Description, string RemoteUrl,
    string Json, string Sha256, string State, string UpdatedAt)
{
    public object Summary => new
    {
        name = Name, version = Version, title = Title, description = Description,
        remoteType = "streamable-http", remoteUrl = RemoteUrl,
        sha256 = Sha256, reviewState = State, publisherVerified = false,
        safeToConnect = false
    };

    public object Detail
    {
        get
        {
            using var doc = JsonDocument.Parse(Json);
            return new
            {
                server = doc.RootElement.Clone(), sha256 = Sha256,
                reviewState = State, publisherVerified = false, safeToConnect = false
            };
        }
    }
}

/// <summary>
/// Persistent immutable, versioned metadata for approved discovery.
/// A pending or revoked entry never appears in MCP/REST public searches.
/// Each change is an atomic DB transaction and requires a server-side admin.
/// </summary>
public sealed class McpRegistryStore(Settings settings)
{
    private DbConnection Open()
    {
        DbConnection db = settings.MetadataProvider == "postgresql"
            ? new NpgsqlConnection(settings.PostgresConnectionString)
            : new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Path.GetFullPath(settings.DataDirectory), "packory.sqlite"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            }.ToString());
        db.Open();
        if (db is SqliteConnection)
        {
            using var busy = db.CreateCommand();
            busy.CommandText = "PRAGMA busy_timeout=5000;";
            busy.ExecuteNonQuery();
        }
        return db;
    }

    private static DbCommand Sql(DbConnection db, string statement,
        params (string Key, object Value)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = statement;
        foreach (var (key, value) in args)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = key;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    public void Initialize()
    {
        using var db = Open();
        using var cmd = Sql(db, """
            CREATE TABLE IF NOT EXISTS mcp_registry_manifests (
                name TEXT NOT NULL, version TEXT NOT NULL,
                title TEXT NOT NULL, description TEXT NOT NULL,
                remote_url TEXT NOT NULL, document_json TEXT NOT NULL,
                sha256 TEXT NOT NULL, state TEXT NOT NULL,
                updated_at TEXT NOT NULL, PRIMARY KEY(name,version)
            );
            CREATE INDEX IF NOT EXISTS mcp_registry_search_idx
                ON mcp_registry_manifests(state,name,version);
            CREATE TABLE IF NOT EXISTS mcp_registry_review_audit (
                name TEXT NOT NULL, version TEXT NOT NULL, sha256 TEXT NOT NULL,
                old_state TEXT NOT NULL, new_state TEXT NOT NULL,
                actor TEXT NOT NULL, reason TEXT NOT NULL, at TEXT NOT NULL
            );
            """);
        cmd.ExecuteNonQuery();
    }

    private static McpRegistryEntry Read(DbDataReader row) => new(
        row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3),
        row.GetString(4), row.GetString(5), row.GetString(6), row.GetString(7),
        row.GetString(8));

    public bool Stage(McpRegistryManifest manifest)
    {
        using var db = Open();
        using var cmd = Sql(db, """
            INSERT INTO mcp_registry_manifests
                (name,version,title,description,remote_url,document_json,sha256,state,updated_at)
            VALUES (@name,@version,@title,@description,@url,@json,@digest,'PENDING',@now)
            ON CONFLICT (name,version) DO NOTHING
            """,
            ("name", manifest.Name), ("version", manifest.Version),
            ("title", manifest.Title), ("description", manifest.Description),
            ("url", manifest.RemoteUrl), ("json", manifest.Json),
            ("digest", manifest.Sha256), ("now", DateTimeOffset.UtcNow.ToString("O")));
        return cmd.ExecuteNonQuery() == 1;
    }

    public bool Decide(string name, string version, string sha256,
        string action, string reason, string actor)
    {
        if (!McpRegistryManifest.ValidName(name) ||
            !McpRegistryManifest.ValidVersion(version) ||
            !ArtifactDigests.ValidSha256(sha256) ||
            reason.Length is < 3 or > 256 ||
            reason.Any(char.IsControl) ||
            action is not ("publish" or "revoke")) return false;
        var previous = action == "publish" ? "PENDING" : "PUBLISHED";
        var next = action == "publish" ? "PUBLISHED" : "REVOKED";
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var update = Sql(db, """
            UPDATE mcp_registry_manifests SET state=@next, updated_at=@now
            WHERE name=@name AND version=@version AND sha256=@sha AND state=@previous
            """,
            ("next", next), ("now", now), ("name", name), ("version", version),
            ("sha", sha256), ("previous", previous));
        update.Transaction = tx;
        if (update.ExecuteNonQuery() != 1) return false;
        using var audit = Sql(db, """
            INSERT INTO mcp_registry_review_audit
                (name,version,sha256,old_state,new_state,actor,reason,at)
            VALUES(@name,@version,@sha,@old,@next,@actor,@reason,@now)
            """,
            ("name", name), ("version", version), ("sha", sha256),
            ("old", previous), ("next", next), ("actor", actor),
            ("reason", reason), ("now", now));
        audit.Transaction = tx;
        audit.ExecuteNonQuery();
        tx.Commit();
        return true;
    }

    public McpRegistryEntry? Find(string name, string version, bool admin = false)
    {
        if (!McpRegistryManifest.ValidName(name) || !McpRegistryManifest.ValidVersion(version))
            return null;
        using var db = Open();
        using var cmd = Sql(db, """
            SELECT name,version,title,description,remote_url,document_json,sha256,state,updated_at
            FROM mcp_registry_manifests
            WHERE name=@name AND version=@version AND
                  (@admin=1 OR state='PUBLISHED')
            """,
            ("name", name), ("version", version), ("admin", admin ? 1 : 0));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<McpRegistryEntry> List(string? query, int limit, int skip = 0,
        bool admin = false)
    {
        if (query is { Length: > 100 } || skip is < 0 or > 10000 ||
            (query is not null && query.Any(char.IsControl)))
            return [];
        using var db = Open();
        using var cmd = Sql(db, """
            SELECT name,version,title,description,remote_url,document_json,sha256,state,updated_at
            FROM mcp_registry_manifests
            WHERE (@admin=1 OR state='PUBLISHED')
                AND (@query='' OR name LIKE @pattern ESCAPE '\'
                    OR title LIKE @pattern ESCAPE '\')
            ORDER BY name,version LIMIT @limit OFFSET @skip
            """,
            ("admin", admin ? 1 : 0), ("query", query ?? ""),
            ("pattern", "%" + (query ?? "").Replace(@"\", @"\\").Replace("%", @"\%")
                .Replace("_", @"\_") + "%"),
            ("limit", Math.Clamp(limit, 1, 101)), ("skip", skip));
        using var reader = cmd.ExecuteReader();
        var results = new List<McpRegistryEntry>();
        while (reader.Read()) results.Add(Read(reader));
        return results;
    }
}
