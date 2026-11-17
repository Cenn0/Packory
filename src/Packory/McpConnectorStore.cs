using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Packory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpConnectorChange(
    string Id, string Name, string Endpoint, bool Enabled, string[] AllowedTools,
    string? BearerToken, bool ClearBearerToken, long Revision);

public sealed record McpConnectorRecord(
    string Id, string Name, string Endpoint, bool Enabled, string[] AllowedTools,
    long Revision, long UpdatedAt, [property: JsonIgnore] string? SecretCiphertext)
{
    public object Summary() => new
    {
        id = Id, name = Name, endpoint = Endpoint, enabled = Enabled,
        allowedTools = AllowedTools, hasBearerToken = SecretCiphertext is not null,
        revision = Revision, updatedAt = UpdatedAt
    };
}

/// <summary>Durable operator-only connector definitions, optimistic revisions, and audit.</summary>
public sealed class McpConnectorStore(Settings settings, McpConnectorPolicy policy,
    McpConnectorSecrets secrets)
{
    private readonly object sync = new();

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
        params (string Name, object? Value)[] values)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = statement;
        foreach (var (name, value) in values)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
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
            using var lease = db is NpgsqlConnection postgres
                ? PostgresSchemaLock.Acquire(postgres) : null;
            using var cmd = Sql(db, """
                CREATE TABLE IF NOT EXISTS mcp_connectors (
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, endpoint TEXT NOT NULL,
                    enabled INTEGER NOT NULL, allowed_tools TEXT NOT NULL,
                    secret_ciphertext TEXT, revision BIGINT NOT NULL, updated_at BIGINT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS mcp_connector_audit (
                    id TEXT PRIMARY KEY, connector_id TEXT NOT NULL,
                    action TEXT NOT NULL, actor TEXT NOT NULL, tool TEXT,
                    created_at BIGINT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS mcp_connector_audit_latest
                    ON mcp_connector_audit(connector_id,created_at);
                """);
            cmd.ExecuteNonQuery();
        }
    }

    private static McpConnectorRecord Read(DbDataReader row) => new(
        row.GetString(0), row.GetString(1), row.GetString(2),
        row.GetInt32(3) == 1,
        SkillAiJson.Read<string[]>(row.GetString(4), 16384),
        row.GetInt64(5), row.GetInt64(6),
        row.IsDBNull(7) ? null : row.GetString(7));

    private static McpConnectorRecord? Find(DbConnection db, string id, DbTransaction? tx = null)
    {
        using var cmd = Sql(db, """
            SELECT id,name,endpoint,enabled,allowed_tools,revision,updated_at,secret_ciphertext
            FROM mcp_connectors WHERE id=@id
            """, ("id", id));
        cmd.Transaction = tx;
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public McpConnectorRecord? Get(string id)
    {
        if (!McpConnectorPolicy.ValidId(id)) return null;
        lock (sync) { using var db = Open(); return Find(db, id); }
    }

    public IReadOnlyList<McpConnectorRecord> List()
    {
        lock (sync)
        {
            using var db = Open();
            using var cmd = Sql(db, """
                SELECT id,name,endpoint,enabled,allowed_tools,revision,updated_at,secret_ciphertext
                FROM mcp_connectors ORDER BY id LIMIT 101
                """);
            using var reader = cmd.ExecuteReader();
            var entries = new List<McpConnectorRecord>();
            while (reader.Read()) entries.Add(Read(reader));
            return entries;
        }
    }

    private static void Audit(DbConnection db, DbTransaction? tx, string id, string action,
        string actor, string? tool = null)
    {
        using var cmd = Sql(db, """
            INSERT INTO mcp_connector_audit(id,connector_id,action,actor,tool,created_at)
            VALUES(@audit,@id,@action,@actor,@tool,@now)
            """, ("audit", Guid.NewGuid().ToString("N")), ("id", id),
            ("action", action), ("actor", actor), ("tool", tool),
            ("now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    public McpConnectorRecord Save(McpConnectorChange change, string actor)
    {
        Validate(change);
        var endpoint = policy.ValidateEndpoint(change.Endpoint).AbsoluteUri;
        lock (sync)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            var previous = Find(db, change.Id, tx);
            if ((previous is null && change.Revision != 0) ||
                (previous is not null && previous.Revision != change.Revision))
                throw new InvalidOperationException("Connector revision is stale.");
            if (previous is null)
            {
                using var count = Sql(db, "SELECT COUNT(*) FROM mcp_connectors");
                count.Transaction = tx;
                if (Convert.ToInt64(count.ExecuteScalar()) >= 100)
                    throw new InvalidOperationException("Connector capacity reached.");
            }
            var cipher = previous?.SecretCiphertext;
            if (change.ClearBearerToken || (previous is not null && previous.Endpoint != endpoint))
                cipher = null;
            if (change.BearerToken is not null)
                cipher = secrets.Protect(change.BearerToken, change.Id, endpoint);
            var revision = checked((previous?.Revision ?? 0) + 1);
            var updated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var allowed = JsonSerializer.Serialize(change.AllowedTools);
            if (previous is null)
            {
                using var insert = Sql(db, """
                    INSERT INTO mcp_connectors
                    (id,name,endpoint,enabled,allowed_tools,secret_ciphertext,revision,updated_at)
                    VALUES(@id,@name,@endpoint,@enabled,@tools,@secret,@revision,@updated)
                    """, ("id", change.Id), ("name", change.Name), ("endpoint", endpoint),
                    ("enabled", change.Enabled ? 1 : 0), ("tools", allowed),
                    ("secret", cipher), ("revision", revision), ("updated", updated));
                insert.Transaction = tx;
                insert.ExecuteNonQuery();
            }
            else
            {
                using var update = Sql(db, """
                    UPDATE mcp_connectors SET name=@name, endpoint=@endpoint, enabled=@enabled,
                      allowed_tools=@tools, secret_ciphertext=@secret, revision=@next, updated_at=@updated
                    WHERE id=@id AND revision=@previous
                    """, ("id", change.Id), ("name", change.Name), ("endpoint", endpoint),
                    ("enabled", change.Enabled ? 1 : 0), ("tools", allowed),
                    ("secret", cipher), ("next", revision), ("updated", updated),
                    ("previous", change.Revision));
                update.Transaction = tx;
                if (update.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Connector revision is stale.");
            }
            Audit(db, tx, change.Id, previous is null ? "create" : "update", actor);
            tx.Commit();
            return new(change.Id, change.Name, endpoint, change.Enabled,
                change.AllowedTools, revision, updated, cipher);
        }
    }

    public bool Delete(string id, long revision, string actor)
    {
        if (!McpConnectorPolicy.ValidId(id) || revision < 1) return false;
        lock (sync)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            using var cmd = Sql(db,
                "DELETE FROM mcp_connectors WHERE id=@id AND revision=@revision",
                ("id", id), ("revision", revision));
            cmd.Transaction = tx;
            var deleted = cmd.ExecuteNonQuery() == 1;
            if (deleted) Audit(db, tx, id, "delete", actor);
            tx.Commit();
            return deleted;
        }
    }

    public void RecordCall(string id, string tool, string actor)
    {
        lock (sync)
        {
            using var db = Open();
            Audit(db, null, id, "call", actor, tool);
        }
    }

    private static void Validate(McpConnectorChange change)
    {
        if (!McpConnectorPolicy.ValidId(change.Id) ||
            change.Name is null || change.Name.Length is < 1 or > 120 ||
            change.Name != change.Name.Trim() || change.Name.Any(char.IsControl) ||
            change.Revision < 0 || change.AllowedTools is null ||
            change.AllowedTools.Length > 32 ||
            change.AllowedTools.Any(t => !McpConnectorPolicy.ValidTool(t)) ||
            change.AllowedTools.Distinct(StringComparer.Ordinal).Count() != change.AllowedTools.Length ||
            (change.ClearBearerToken && change.BearerToken is not null))
            throw new InvalidDataException("Invalid MCP connector configuration.");
    }
}
