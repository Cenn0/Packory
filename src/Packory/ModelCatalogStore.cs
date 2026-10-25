using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Packory;

public sealed record ModelDescriptor(string Name, string Version, string Format,
    string Sha256, long SizeBytes, string License, string Publisher,
    string SourceUrl, string? ProvenanceUrl, string Description,
    string Json, string DescriptorSha256)
{
    private static readonly Regex NamePattern = new(
        @"\A[a-z0-9][a-z0-9._-]{0,63}/[a-z0-9][a-z0-9._-]{0,63}\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex LicensePattern = new(
        @"\A[A-Za-z0-9][A-Za-z0-9.+-]{0,63}\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool ValidName(string? name) => name is { Length: <= 129 } && NamePattern.IsMatch(name);

    private static bool PublicHttps(string? address)
    {
        if (address is null || address.Length > 1024 ||
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Port != 443 ||
            uri.HostNameType != UriHostNameType.Dns || !uri.IdnHost.Contains('.') ||
            uri.IdnHost.EndsWith('.') || uri.IdnHost is "localhost" ||
            uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath == "/") return false;
        return true;
    }

    public static ModelDescriptor Parse(byte[] bytes)
    {
        if (bytes.Length is < 32 or > 16384) throw new InvalidDataException("Invalid model descriptor size.");
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            using var document = JsonDocument.Parse(bytes,
                new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected model descriptor object.");
            var allowed = new HashSet<string>(StringComparer.Ordinal)
            { "name", "version", "format", "sha256", "sizeBytes", "license", "publisher", "sourceUrl", "provenanceUrl", "description" };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                    throw new InvalidDataException("Unknown or duplicate model descriptor field.");
            if (seen.Count is not (9 or 10)) throw new InvalidDataException("Missing model descriptor field.");
            static string Required(JsonElement root, string key) =>
                root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()! : throw new InvalidDataException("Invalid model descriptor field.");
            var name = Required(root, "name");
            var version = Required(root, "version");
            var format = Required(root, "format");
            var sha256 = Required(root, "sha256");
            var license = Required(root, "license");
            var publisher = Required(root, "publisher");
            var sourceUrl = Required(root, "sourceUrl");
            var description = Required(root, "description");
            string? provenanceUrl = null;
            if (root.TryGetProperty("provenanceUrl", out var provenance))
            {
                if (provenance.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid provenance URL.");
                provenanceUrl = provenance.GetString();
            }
            if (!root.TryGetProperty("sizeBytes", out var rawSize) ||
                rawSize.ValueKind != JsonValueKind.Number || !rawSize.TryGetInt64(out var size) ||
                size is < 1 or > 1_099_511_627_776L || !ValidName(name) || !Names.Version(version) ||
                format is not ("gguf" or "safetensors" or "onnx" or "other") ||
                !ArtifactDigests.ValidSha256(sha256) || !LicensePattern.IsMatch(license) ||
                publisher.Length is < 1 or > 120 || publisher.Any(char.IsControl) ||
                description.Length is < 1 or > 1024 || description.Any(char.IsControl) ||
                !PublicHttps(sourceUrl) || (provenanceUrl is not null && !PublicHttps(provenanceUrl)))
                throw new InvalidDataException("Unsupported or unsafe model descriptor.");
            return new ModelDescriptor(name, version, format, sha256, size, license,
                publisher, sourceUrl, provenanceUrl, description, text,
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or RegexMatchTimeoutException)
        {
            throw new InvalidDataException("Invalid model descriptor JSON or UTF-8.", error);
        }
    }
}

public sealed record ModelCatalogEntry(ModelDescriptor Descriptor, string State, string UpdatedAt)
{
    public object Summary => new
    {
        Descriptor.Name, Descriptor.Version, Descriptor.Format, Descriptor.Sha256,
        Descriptor.SizeBytes, Descriptor.License, Descriptor.Publisher,
        Descriptor.SourceUrl, Descriptor.ProvenanceUrl, Descriptor.Description,
        descriptorSha256 = Descriptor.DescriptorSha256, reviewState = State,
        publisherVerified = false, bytesVerified = false, UpdatedAt
    };
}

public sealed record ModelReviewEvent(string OldState, string NewState,
    string Actor, string Reason, string At);

public sealed class ModelCatalogStore(Settings settings)
{
    private DbConnection Open()
    {
        DbConnection db = settings.MetadataProvider == "postgresql"
            ? new NpgsqlConnection(settings.PostgresConnectionString)
            : new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Path.GetFullPath(settings.DataDirectory), "packory.sqlite"),
                Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared
            }.ToString());
        db.Open();
        if (db is SqliteConnection sqlite)
        {
            using var busy = sqlite.CreateCommand();
            busy.CommandText = "PRAGMA busy_timeout=5000;"; busy.ExecuteNonQuery();
        }
        return db;
    }

    private static DbCommand Command(DbConnection db, string sql,
        params (string Key, object? Value)[] args)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in args)
        {
            var parameter = cmd.CreateParameter(); parameter.ParameterName = key;
            parameter.Value = value ?? DBNull.Value; cmd.Parameters.Add(parameter);
        }
        return cmd;
    }

    public void Initialize()
    {
        using var db = Open();
        using var lease = db is NpgsqlConnection postgres ? PostgresSchemaLock.Acquire(postgres) : null;
        using var cmd = Command(db, """
            CREATE TABLE IF NOT EXISTS model_catalog_descriptors (
                name TEXT NOT NULL, version TEXT NOT NULL, document_json TEXT NOT NULL,
                descriptor_sha256 TEXT NOT NULL, state TEXT NOT NULL,
                updated_at TEXT NOT NULL, PRIMARY KEY(name,version)
            );
            CREATE INDEX IF NOT EXISTS model_catalog_state_name
                ON model_catalog_descriptors(state,name,version);
            CREATE TABLE IF NOT EXISTS model_catalog_review_audit (
                id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                name TEXT NOT NULL, version TEXT NOT NULL,
                descriptor_sha256 TEXT NOT NULL, old_state TEXT NOT NULL,
                new_state TEXT NOT NULL, actor TEXT NOT NULL,
                reason TEXT NOT NULL, at TEXT NOT NULL
            );
            """.Replace("id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY",
                db is SqliteConnection ? "id INTEGER PRIMARY KEY AUTOINCREMENT"
                    : "id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY"));
        cmd.ExecuteNonQuery();
    }

    private static ModelCatalogEntry? Read(DbDataReader row)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(row.GetString(2));
            var descriptor = ModelDescriptor.Parse(bytes);
            return descriptor.Name == row.GetString(0) && descriptor.Version == row.GetString(1) &&
                   descriptor.DescriptorSha256 == row.GetString(3)
                ? new ModelCatalogEntry(descriptor, row.GetString(4), row.GetString(5)) : null;
        }
        catch (InvalidDataException) { return null; }
    }

    public bool Stage(ModelDescriptor descriptor, string actor)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = Command(db, """
            INSERT INTO model_catalog_descriptors
                (name,version,document_json,descriptor_sha256,state,updated_at)
            VALUES(@name,@version,@json,@digest,'PENDING',@now)
            ON CONFLICT(name,version) DO NOTHING
            """, ("name", descriptor.Name), ("version", descriptor.Version),
            ("json", descriptor.Json), ("digest", descriptor.DescriptorSha256),
            ("now", DateTimeOffset.UtcNow.ToString("O")));
        cmd.Transaction = tx;
        if (cmd.ExecuteNonQuery() != 1) return false;
        using var audit = Command(db, """
            INSERT INTO model_catalog_review_audit
                (name,version,descriptor_sha256,old_state,new_state,actor,reason,at)
            VALUES(@name,@version,@digest,'','PENDING',@actor,'Descriptor staged',@now)
            """, ("name", descriptor.Name), ("version", descriptor.Version),
            ("digest", descriptor.DescriptorSha256), ("actor", actor),
            ("now", DateTimeOffset.UtcNow.ToString("O")));
        audit.Transaction = tx; audit.ExecuteNonQuery(); tx.Commit(); return true;
    }

    public bool Decide(string name, string version, string digest,
        string action, string reason, string actor)
    {
        if (!ModelDescriptor.ValidName(name) || !Names.Version(version) ||
            !ArtifactDigests.ValidSha256(digest) || action is not ("publish" or "revoke") ||
            reason.Length is < 3 or > 256 || reason.Any(char.IsControl)) return false;
        var previous = action == "publish" ? "PENDING" : "PUBLISHED";
        var next = action == "publish" ? "PUBLISHED" : "REVOKED";
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var db = Open(); using var tx = db.BeginTransaction();
        using var cmd = Command(db, """
            UPDATE model_catalog_descriptors SET state=@next,updated_at=@now
            WHERE name=@name AND version=@version AND descriptor_sha256=@digest AND state=@previous
            """, ("next", next), ("now", now), ("name", name), ("version", version),
            ("digest", digest), ("previous", previous));
        cmd.Transaction = tx;
        if (cmd.ExecuteNonQuery() != 1) return false;
        using var audit = Command(db, """
            INSERT INTO model_catalog_review_audit
                (name,version,descriptor_sha256,old_state,new_state,actor,reason,at)
            VALUES(@name,@version,@digest,@previous,@next,@actor,@reason,@now)
            """, ("name", name), ("version", version), ("digest", digest),
            ("previous", previous), ("next", next), ("actor", actor),
            ("reason", reason), ("now", now));
        audit.Transaction = tx; audit.ExecuteNonQuery(); tx.Commit(); return true;
    }

    public ModelCatalogEntry? Find(string name, string version, bool admin = false)
    {
        if (!ModelDescriptor.ValidName(name) || !Names.Version(version)) return null;
        using var db = Open();
        using var cmd = Command(db, """
            SELECT name,version,document_json,descriptor_sha256,state,updated_at
            FROM model_catalog_descriptors WHERE name=@name AND version=@version
              AND (@admin=1 OR state='PUBLISHED')
            """, ("name", name), ("version", version), ("admin", admin ? 1 : 0));
        using var reader = cmd.ExecuteReader(); return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<ModelCatalogEntry> List(bool admin, int limit, int offset)
    {
        if (limit is < 1 or > 101 || offset is < 0 or > 10000) return [];
        using var db = Open();
        using var cmd = Command(db, """
            SELECT name,version,document_json,descriptor_sha256,state,updated_at
            FROM model_catalog_descriptors WHERE @admin=1 OR state='PUBLISHED'
            ORDER BY name COLLATE __ORDINAL__,version COLLATE __ORDINAL__
            LIMIT @limit OFFSET @offset
            """.Replace("__ORDINAL__", db is SqliteConnection ? "BINARY" : "\"C\""),
            ("admin", admin ? 1 : 0), ("limit", limit), ("offset", offset));
        using var reader = cmd.ExecuteReader(); var items = new List<ModelCatalogEntry>();
        while (reader.Read()) { var entry = Read(reader); if (entry is not null) items.Add(entry); }
        return items;
    }

    public IReadOnlyList<ModelReviewEvent> History(string name, string version)
    {
        if (!ModelDescriptor.ValidName(name) || !Names.Version(version)) return [];
        using var db = Open();
        using var cmd = Command(db, """
            SELECT old_state,new_state,actor,reason,at FROM model_catalog_review_audit
            WHERE name=@name AND version=@version ORDER BY id DESC LIMIT 100
            """, ("name", name), ("version", version));
        using var reader = cmd.ExecuteReader(); var items = new List<ModelReviewEvent>();
        while (reader.Read()) items.Add(new ModelReviewEvent(reader.GetString(0),
            reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return items;
    }
}
