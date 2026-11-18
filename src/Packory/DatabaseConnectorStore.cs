using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Packory;

public sealed record DatabaseConnectorAudit(string ConnectorId, string? Query, string Action, string Actor, long CreatedAt);

/// <summary>Private administrative definitions and encrypted passwords, separate from the artifact catalog.</summary>
public sealed class DatabaseConnectorStore(Settings settings, DatabaseConnectorPolicy policy, McpConnectorSecrets secrets)
{
    private readonly object sync = new();

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
            using var busy = Sql(db, "PRAGMA busy_timeout=5000");
            busy.ExecuteNonQuery();
        }
        return db;
    }

    private static DbCommand Sql(DbConnection db, string statement, params (string Name, object? Value)[] values)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = statement;
        foreach (var (name, value) in values)
        {
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(parameter);
        }
        return cmd;
    }

    public void Initialize()
    {
        lock (sync)
        {
            using var db = Open();
            using var lease = db is NpgsqlConnection postgres ? PostgresSchemaLock.Acquire(postgres) : null;
            using var tx = db.BeginTransaction();
            using var create = Sql(db, """
                CREATE TABLE IF NOT EXISTS database_connector_schema (
                    slot INTEGER PRIMARY KEY CHECK(slot=1), version INTEGER NOT NULL);
                INSERT INTO database_connector_schema(slot,version) VALUES(1,1) ON CONFLICT(slot) DO NOTHING;
                CREATE TABLE IF NOT EXISTS database_connector_ids (id TEXT PRIMARY KEY);
                CREATE TABLE IF NOT EXISTS database_connectors (
                    id TEXT PRIMARY KEY, revision BIGINT NOT NULL, definition_json TEXT NOT NULL,
                    secret_ciphertext TEXT, updated_at BIGINT NOT NULL);
                CREATE TABLE IF NOT EXISTS database_connector_audit (
                    id TEXT PRIMARY KEY, connector_id TEXT NOT NULL, query_name TEXT,
                    action TEXT NOT NULL, actor TEXT NOT NULL, created_at BIGINT NOT NULL);
                CREATE INDEX IF NOT EXISTS database_connector_audit_latest
                    ON database_connector_audit(connector_id,created_at,id);
                """);
            create.Transaction = tx;
            create.ExecuteNonQuery();
            using var version = Sql(db, "SELECT version FROM database_connector_schema WHERE slot=1");
            version.Transaction = tx;
            if (Convert.ToInt32(version.ExecuteScalar()) != 1)
                throw new InvalidOperationException("Unsupported database connector schema; reviewed migration required.");
            tx.Commit();
        }
    }

    private static DatabaseConnectorRecord Read(DbDataReader row) => new(
        SkillAiJson.Read<DatabaseConnectorDefinition>(row.GetString(0), 49152),
        row.IsDBNull(1) ? null : row.GetString(1), row.GetInt64(2));

    private static DatabaseConnectorRecord? Find(DbConnection db, string id, DbTransaction? tx = null)
    {
        using var command = Sql(db,
            "SELECT definition_json,secret_ciphertext,updated_at FROM database_connectors WHERE id=@id", ("id", id));
        command.Transaction = tx;
        using var row = command.ExecuteReader();
        return row.Read() ? Read(row) : null;
    }

    public DatabaseConnectorRecord? Get(string id)
    {
        if (!McpConnectorPolicy.ValidId(id)) return null;
        lock (sync) { using var db = Open(); return Find(db, id); }
    }

    public IReadOnlyList<DatabaseConnectorRecord> List()
    {
        lock (sync)
        {
            using var db = Open();
            using var command = Sql(db,
                "SELECT definition_json,secret_ciphertext,updated_at FROM database_connectors ORDER BY id LIMIT 100");
            using var row = command.ExecuteReader();
            var items = new List<DatabaseConnectorRecord>();
            while (row.Read()) items.Add(Read(row));
            return items;
        }
    }

    public DatabaseConnectorRecord Save(DatabaseConnectorChange change, string actor)
    {
        if (change.Definition is null || (change.ClearPassword && change.Password is not null))
            throw new InvalidDataException("Invalid database connector change.");
        var definition = change.Definition;
        policy.Validate(definition);
        if (change.Password is not null && (Encoding.UTF8.GetByteCount(change.Password) is < 1 or > 2048 ||
            change.Password.Contains('\0')))
            throw new InvalidDataException("Invalid database password length or encoding.");
        lock (sync)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            var old = Find(db, definition.Id, tx);
            if ((old is null && definition.Revision != 0) ||
                (old is not null && old.Definition.Revision != definition.Revision))
                throw new InvalidOperationException("Database connector revision is stale.");
            if (old is null)
            {
                using var count = Sql(db, "SELECT COUNT(*) FROM database_connectors");
                count.Transaction = tx;
                if (Convert.ToInt64(count.ExecuteScalar()) >= 100)
                    throw new InvalidOperationException("Database connector capacity reached.");
                using var reserve = Sql(db,
                    "INSERT INTO database_connector_ids(id) VALUES(@id) ON CONFLICT(id) DO NOTHING", ("id", definition.Id));
                reserve.Transaction = tx;
                if (reserve.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("Deleted connector identities cannot be reused.");
            }
            var context = DatabaseConnectorPolicy.CredentialContext(definition);
            var cipher = change.ClearPassword || (old is not null &&
                DatabaseConnectorPolicy.CredentialContext(old.Definition) != context) ? null : old?.SecretCiphertext;
            if (change.Password is not null)
            {
                var passwordBytes = Encoding.UTF8.GetBytes(change.Password);
                try { cipher = secrets.Protect(Convert.ToBase64String(passwordBytes), "db:" + definition.Id, context); }
                finally { CryptographicOperations.ZeroMemory(passwordBytes); }
            }
            if (definition.Enabled && (cipher is null || definition.Queries.Length == 0))
                throw new InvalidDataException("An enabled connector needs a password and at least one approved query.");
            var saved = definition with { Revision = checked(definition.Revision + 1) };
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using var write = old is null
                ? Sql(db, """
                    INSERT INTO database_connectors(id,revision,definition_json,secret_ciphertext,updated_at)
                    VALUES(@id,@revision,@definition,@secret,@now)
                    """, ("id", saved.Id), ("revision", saved.Revision),
                    ("definition", JsonSerializer.Serialize(saved, SkillAiJson.Options)), ("secret", cipher), ("now", now))
                : Sql(db, """
                    UPDATE database_connectors SET revision=@revision,definition_json=@definition,
                    secret_ciphertext=@secret,updated_at=@now WHERE id=@id AND revision=@previous
                    """, ("id", saved.Id), ("revision", saved.Revision),
                    ("definition", JsonSerializer.Serialize(saved, SkillAiJson.Options)),
                    ("secret", cipher), ("now", now), ("previous", definition.Revision));
            write.Transaction = tx;
            if (write.ExecuteNonQuery() != 1) throw new InvalidOperationException("Database connector revision is stale.");
            Audit(db, tx, saved.Id, null, old is null ? "created" : "updated", actor);
            tx.Commit();
            return new(saved, cipher, now);
        }
    }

    public bool Delete(string id, long revision, string actor)
    {
        if (!McpConnectorPolicy.ValidId(id) || revision < 1) return false;
        lock (sync)
        {
            using var db = Open();
            using var tx = db.BeginTransaction();
            using var delete = Sql(db, "DELETE FROM database_connectors WHERE id=@id AND revision=@revision",
                ("id", id), ("revision", revision));
            delete.Transaction = tx;
            var deleted = delete.ExecuteNonQuery() == 1;
            if (deleted) Audit(db, tx, id, null, "deleted", actor);
            tx.Commit();
            return deleted;
        }
    }

    public string Password(DatabaseConnectorRecord connector)
    {
        if (connector.SecretCiphertext is null) throw new UnauthorizedAccessException("Connector has no credential.");
        var bytes = Convert.FromBase64String(secrets.Unprotect(connector.SecretCiphertext,
            "db:" + connector.Definition.Id, DatabaseConnectorPolicy.CredentialContext(connector.Definition)));
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void Audit(DbConnection db, DbTransaction? tx, string id, string? query, string action, string actor)
    {
        using var cmd = Sql(db, """
            INSERT INTO database_connector_audit(id,connector_id,query_name,action,actor,created_at)
            VALUES(@audit,@id,@query,@action,@actor,@now)
            """, ("audit", Guid.NewGuid().ToString("N")), ("id", id), ("query", query),
            ("action", action), ("actor", actor), ("now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    public void Record(string id, string? query, string action, string actor)
    {
        if (!McpConnectorPolicy.ValidId(id) || (query is not null && !DatabaseConnectorPolicy.ValidName(query))) return;
        lock (sync) { using var db = Open(); Audit(db, null, id, query, action, actor); }
    }

    public IReadOnlyList<DatabaseConnectorAudit> History(string id)
    {
        lock (sync)
        {
            using var db = Open();
            using var cmd = Sql(db, """
                SELECT connector_id,query_name,action,actor,created_at FROM database_connector_audit
                WHERE connector_id=@id ORDER BY created_at DESC,id DESC LIMIT 100
                """, ("id", id));
            using var row = cmd.ExecuteReader();
            var items = new List<DatabaseConnectorAudit>();
            while (row.Read()) items.Add(new(row.GetString(0), row.IsDBNull(1) ? null : row.GetString(1),
                row.GetString(2), row.GetString(3), row.GetInt64(4)));
            return items;
        }
    }
}
