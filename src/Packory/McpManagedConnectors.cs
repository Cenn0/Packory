using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Packory;

// Connector definitions are inert administrative policy. No submitted SQL,
// database URL or credential is accepted from an MCP caller.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpManagedQuery
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Sql { get; init; } = "";
    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.Ordinal);
    public int MaxRows { get; init; } = 25;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpManagedConnector
{
    public string Id { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Enabled { get; init; }
    public long Revision { get; init; }
    // OIDC issuer is pinned by Packory; subjects are opaque IDs, not emails.
    public string[] AllowedSubjects { get; init; } = [];
    public McpManagedQuery[] Queries { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpConnectorChange(
    McpManagedConnector Definition, string? ConnectionString, bool ClearCredential, string Reason);

public sealed class McpConnectorDeployment
{
    public string? KeyRingFile { get; init; }
    public string[] AllowedHosts { get; init; } = [];

    public static McpConnectorDeployment FromEnvironment() => new()
    {
        KeyRingFile = Environment.GetEnvironmentVariable("PACKORY_MCP_CONNECTOR_KEYRING_FILE"),
        AllowedHosts = (Environment.GetEnvironmentVariable("PACKORY_MCP_CONNECTOR_HOSTS") ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    };

    public void Validate()
    {
        if (AllowedHosts.Length is < 1 or > 32 ||
            AllowedHosts.Distinct(StringComparer.OrdinalIgnoreCase).Count() != AllowedHosts.Length ||
            AllowedHosts.Any(host => host.Length > 253 ||
                !Regex.IsMatch(host, @"\A[a-zA-Z0-9][a-zA-Z0-9.-]*\z",
                    RegexOptions.CultureInvariant)) ||
            string.IsNullOrWhiteSpace(KeyRingFile) || !Path.IsPathFullyQualified(KeyRingFile))
            throw new InvalidOperationException("MCP connector host allowlist and absolute keyring path are required.");
    }
}

public static class McpConnectorPolicy
{
    private static bool Name(string? value) => value is { Length: >= 1 and <= 48 } &&
        Regex.IsMatch(value, @"\A[a-z][a-z0-9_-]*\z", RegexOptions.CultureInvariant);

    public static void Validate(McpManagedConnector definition)
    {
        if (definition is null || !Name(definition.Id) || definition.Revision < 0 ||
            definition.Description is null || definition.Description.Length > 256 ||
            definition.AllowedSubjects is null || definition.Queries is null ||
            definition.AllowedSubjects.Length > 32 ||
            definition.AllowedSubjects.Any(s => s is null ||
                !Regex.IsMatch(s, @"\A[A-Za-z0-9._:-]{1,128}\z", RegexOptions.CultureInvariant)) ||
            definition.AllowedSubjects.Distinct(StringComparer.Ordinal).Count() != definition.AllowedSubjects.Length ||
            definition.Queries.Length is < 1 or > 12 ||
            (definition.Enabled && definition.AllowedSubjects.Length == 0) ||
            definition.Queries.Any(q => q is null || !Name(q.Name) ||
                q.Description is null || q.Description.Length is < 1 or > 256 ||
                q.Sql is null || q.Sql.Length is < 8 or > 2048 ||
                !Regex.IsMatch(q.Sql, @"\A\s*SELECT\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                q.Sql.Contains(';') || q.Sql.Contains("--", StringComparison.Ordinal) ||
                q.Sql.Contains("/*", StringComparison.Ordinal) ||
                q.Sql.Contains("*/", StringComparison.Ordinal) ||
                q.MaxRows is < 1 or > 50 || q.Parameters is null ||
                q.Parameters.Count > 8 ||
                q.Parameters.Any(p => !Name(p.Key) ||
                    p.Value is not ("text" or "int64" or "bool" or "uuid")) ||
                !Regex.Matches(q.Sql, @"(?<![A-Za-z0-9_])@([a-z][a-z0-9_]*)",
                    RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(q.Parameters.Keys)) ||
            definition.Queries.Select(q => q.Name).Distinct(StringComparer.Ordinal).Count() !=
                definition.Queries.Length)
            throw new InvalidDataException("Invalid managed connector policy or query template.");
    }

    // Only allow the narrow credential fields needed for PostgreSQL. Rebuild
    // the string rather than trusting arbitrary Npgsql options such as
    // search_path, passfiles, TLS downgrades or custom startup parameters.
    public static string NormalizeConnection(string source, McpConnectorDeployment deployment)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Length > 4096 ||
            source.Any(c => c == '\0' || c == '\r' || c == '\n'))
            throw new InvalidDataException("Invalid connector credential.");
        NpgsqlConnectionStringBuilder input;
        try { input = new NpgsqlConnectionStringBuilder(source); }
        catch (ArgumentException) { throw new InvalidDataException("Invalid connector credential."); }
        if (input.Host is null || input.Host.Contains(',') ||
            !deployment.AllowedHosts.Contains(input.Host, StringComparer.OrdinalIgnoreCase) ||
            input.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(input.Database) || input.Database.Length > 128 ||
            string.IsNullOrWhiteSpace(input.Username) || input.Username.Length > 128 ||
            string.IsNullOrEmpty(input.Password) || input.Password.Length > 2048 ||
            input.SslMode != SslMode.VerifyFull)
            throw new InvalidDataException("Connector host, TLS mode or database identity is not allowed.");
        return new NpgsqlConnectionStringBuilder
        {
            Host = input.Host,
            Port = input.Port,
            Database = input.Database,
            Username = input.Username,
            Password = input.Password,
            SslMode = SslMode.VerifyFull,
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 4,
            IncludeErrorDetail = false
        }.ConnectionString;
    }
}

// Independent AEAD context: Skill AI credentials and connector credentials
// cannot be swapped, even if an operator reuses a keyring by mistake.
public sealed class McpConnectorSecrets(McpConnectorDeployment deployment)
{
    private sealed record Ring(string Active, Dictionary<string, string> Keys);
    private sealed record Envelope(string KeyId, string Nonce, string Ciphertext, string Tag);

    private Ring Load()
    {
        var path = deployment.KeyRingFile ?? throw new InvalidDataException("Connector keyring is unavailable.");
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Connector keyring is unavailable.");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                         UnixFileMode.OtherRead | UnixFileMode.OtherWrite |
                         UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidDataException("Connector keyring must be private to its owner.");
        }
        var ring = SkillAiJson.Read<Ring>(File.ReadAllText(path), 16384);
        if (ring.Active is null || ring.Keys is null || ring.Keys.Count is < 1 or > 8 ||
            !ring.Keys.ContainsKey(ring.Active) ||
            ring.Keys.Any(k => k.Key is null || !Regex.IsMatch(k.Key, @"\A[A-Za-z0-9_-]{1,64}\z") ||
                Convert.FromBase64String(k.Value).Length != 32))
            throw new InvalidDataException("Invalid connector keyring.");
        return ring;
    }

    public void VerifyReady() => _ = Load();

    private static byte[] Context(string connectorId) =>
        Encoding.UTF8.GetBytes("packory.mcp.connector/1\n" + connectorId);

    public string Encrypt(string secret, string connectorId)
    {
        var ring = Load();
        var clear = Encoding.UTF8.GetBytes(secret);
        var key = Convert.FromBase64String(ring.Keys[ring.Active]);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[clear.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, clear, ciphertext, tag, Context(connectorId));
            return JsonSerializer.Serialize(new Envelope(ring.Active, Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag)), SkillAiJson.Options);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public string Decrypt(string envelopeJson, string connectorId)
    {
        var ring = Load();
        var envelope = SkillAiJson.Read<Envelope>(envelopeJson, 16384);
        if (!ring.Keys.TryGetValue(envelope.KeyId, out var encoded))
            throw new InvalidDataException("Connector credential key is unavailable.");
        var key = Convert.FromBase64String(encoded);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var clear = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(envelope.Nonce), ciphertext,
                Convert.FromBase64String(envelope.Tag), clear, Context(connectorId));
            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(key);
        }
    }
}

/// <summary>
/// Durable policy and audit. Uses the same SQLite/PostgreSQL metadata backend
/// and single-writer fence as Packory; no separate unmanaged local sidecar.
/// </summary>
public sealed class McpConnectorStore(Settings settings, McpConnectorSecrets secrets,
    McpConnectorDeployment deployment)
{
    public sealed record Stored(McpManagedConnector Definition, string? Ciphertext);
    public sealed record Public(McpManagedConnector Definition, bool HasCredential);
    public sealed record AuditRecord(string ConnectorId, string QueryName, string Decision,
        string PrincipalDigest, long CreatedAt);

    private readonly object sync = new();
    // Serializes execution with operator revocation and credential rotation.
    // Small initial profile; wider concurrency requires per-connector fencing.
    public SemaphoreSlim PolicyGate { get; } = new(1, 1);
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

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
        if (db is SqliteConnection)
        {
            using var pragma = Command(db, "PRAGMA busy_timeout=5000");
            pragma.ExecuteNonQuery();
        }
        return db;
    }

    private static DbCommand Command(DbConnection db, string sql, params (string Key, object? Value)[] values)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (key, value) in values)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = key;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    public void Initialize()
    {
        lock (sync)
        {
            using var db = Open();
            using var lease = db is NpgsqlConnection postgres ? PostgresSchemaLock.Acquire(postgres) : null;
            using var cmd = Command(db, """
                CREATE TABLE IF NOT EXISTS mcp_managed_connectors (
                    id TEXT PRIMARY KEY, revision BIGINT NOT NULL, definition_json TEXT NOT NULL,
                    secret_ciphertext TEXT, updated_at BIGINT NOT NULL);
                CREATE TABLE IF NOT EXISTS mcp_connector_audit (
                    id TEXT PRIMARY KEY, connector_id TEXT NOT NULL, query_name TEXT NOT NULL,
                    decision TEXT NOT NULL, principal_digest TEXT NOT NULL, created_at BIGINT NOT NULL);
                CREATE INDEX IF NOT EXISTS mcp_connector_audit_latest
                    ON mcp_connector_audit(created_at);
                """);
            cmd.ExecuteNonQuery();
        }
    }

    private static Stored? Find(DbConnection db, string id)
    {
        using var command = Command(db,
            "SELECT definition_json,secret_ciphertext FROM mcp_managed_connectors WHERE id=@id", ("id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new Stored(SkillAiJson.Read<McpManagedConnector>(reader.GetString(0), 32768),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    public Stored? Get(string id)
    {
        lock (sync) { using var db = Open(); return Find(db, id); }
    }

    public IReadOnlyList<Public> List()
    {
        lock (sync)
        {
            using var db = Open();
            using var cmd = Command(db,
                "SELECT definition_json,secret_ciphertext FROM mcp_managed_connectors ORDER BY id LIMIT 33");
            using var reader = cmd.ExecuteReader();
            var items = new List<Public>();
            while (reader.Read())
                items.Add(new(SkillAiJson.Read<McpManagedConnector>(reader.GetString(0), 32768),
                    !reader.IsDBNull(1)));
            return items;
        }
    }

    public Public Save(string id, McpConnectorChange change, string actor)
    {
        if (change?.Definition is null || change.Definition.Id != id ||
            change.Reason is null || change.Reason.Length is < 3 or > 256 ||
            (change.ClearCredential && change.ConnectionString is not null))
            throw new InvalidDataException("Invalid connector change.");
        McpConnectorPolicy.Validate(change.Definition);
        lock (sync)
        {
            using var db = Open();
            var old = Find(db, id);
            if ((old is null && change.Definition.Revision != 0) ||
                (old is not null && old.Definition.Revision != change.Definition.Revision))
                throw new InvalidOperationException("Connector revision changed.");
            if (old is null && List().Count >= 32)
                throw new InvalidOperationException("Connector capacity reached.");
            var ciphertext = change.ClearCredential ? null : old?.Ciphertext;
            if (change.ConnectionString is not null)
                ciphertext = secrets.Encrypt(McpConnectorPolicy.NormalizeConnection(
                    change.ConnectionString, deployment), id);
            if (change.Definition.Enabled && ciphertext is null)
                throw new InvalidDataException("An enabled connector requires a credential.");
            var current = change.Definition with { Revision = checked(change.Definition.Revision + 1) };
            using var tx = db.BeginTransaction();
            using var cmd = old is null
                ? Command(db, """
                    INSERT INTO mcp_managed_connectors(id,revision,definition_json,secret_ciphertext,updated_at)
                    VALUES(@id,@rev,@definition,@secret,@now)
                    """, ("id", id), ("rev", current.Revision),
                    ("definition", JsonSerializer.Serialize(current, SkillAiJson.Options)),
                    ("secret", ciphertext), ("now", Now))
                : Command(db, """
                    UPDATE mcp_managed_connectors
                    SET revision=@rev,definition_json=@definition,secret_ciphertext=@secret,updated_at=@now
                    WHERE id=@id AND revision=@previous
                    """, ("id", id), ("rev", current.Revision),
                    ("definition", JsonSerializer.Serialize(current, SkillAiJson.Options)),
                    ("secret", ciphertext), ("now", Now), ("previous", old.Definition.Revision));
            cmd.Transaction = tx;
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Connector revision changed.");
            WriteAudit(db, tx, id, "", "policy_changed", actor);
            tx.Commit();
            return new(current, ciphertext is not null);
        }
    }

    public bool Revoke(string id, string actor)
    {
        lock (sync)
        {
            using var db = Open();
            var old = Find(db, id);
            if (old is null) return false;
            var disabled = old.Definition with { Enabled = false, Revision = old.Definition.Revision + 1 };
            using var tx = db.BeginTransaction();
            using var cmd = Command(db, """
                UPDATE mcp_managed_connectors
                SET revision=@rev,definition_json=@definition,secret_ciphertext=NULL,updated_at=@now
                WHERE id=@id AND revision=@previous
                """, ("id", id), ("rev", disabled.Revision),
                ("definition", JsonSerializer.Serialize(disabled, SkillAiJson.Options)),
                ("now", Now), ("previous", old.Definition.Revision));
            cmd.Transaction = tx;
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Connector revision changed.");
            WriteAudit(db, tx, id, "", "revoked", actor);
            tx.Commit();
            return true;
        }
    }

    private static void WriteAudit(DbConnection db, DbTransaction? tx,
        string connectorId, string query, string decision, string principal)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(principal)))[..20];
        using var cmd = Command(db, """
            INSERT INTO mcp_connector_audit(id,connector_id,query_name,decision,principal_digest,created_at)
            VALUES(@id,@connector,@query,@decision,@principal,@now)
            """, ("id", Guid.NewGuid().ToString("N")), ("connector", connectorId),
            ("query", query), ("decision", decision), ("principal", digest), ("now", Now));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    public void Audit(string id, string query, string decision, string principal)
    {
        lock (sync)
        {
            using var db = Open();
            WriteAudit(db, null, id, query, decision, principal);
        }
    }

    public IReadOnlyList<AuditRecord> History()
    {
        lock (sync)
        {
            using var db = Open();
            using var cmd = Command(db, """
                SELECT connector_id,query_name,decision,principal_digest,created_at
                FROM mcp_connector_audit ORDER BY created_at DESC,id DESC LIMIT 100
                """);
            using var r = cmd.ExecuteReader();
            var items = new List<AuditRecord>();
            while (r.Read()) items.Add(new(r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetInt64(4)));
            return items;
        }
    }
}
