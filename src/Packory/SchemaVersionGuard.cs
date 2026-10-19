using Microsoft.Data.Sqlite;
using Npgsql;

namespace Packory;

/// <summary>
/// Explicit forward-only database versions. V1 is the historical global-catalog
/// schema; V2 adds legacy feed inventory; V3 adds immutable NuGet SHA-512
/// publication-time records. No native routes or feed isolation change.
/// </summary>
public static class SchemaVersionGuard
{
    private const int Baseline = 1;
    public const int Current = 5;

    private static int RequireKnown(object? value)
    {
        if (value is null or DBNull)
            throw new InvalidOperationException("Missing Packory metadata schema version");
        var version = Convert.ToInt32(value);
        if (version < Baseline || version > Current)
            throw new InvalidOperationException(
                "Unsupported Packory metadata schema version; reviewed migration required.");
        return version;
    }

    private const string FeedSchema = """
        CREATE TABLE IF NOT EXISTS feeds (
            id TEXT NOT NULL PRIMARY KEY,
            kind TEXT NOT NULL CHECK(kind IN ('nuget','npm','cargo','pypi','oci','skill')),
            mode TEXT NOT NULL CHECK(mode IN ('legacy','hosted','proxy')),
            visibility TEXT NOT NULL CHECK(visibility IN ('runtime','private','public')),
            display_name TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS feeds_kind_id ON feeds(kind,id);
        CREATE TABLE IF NOT EXISTS feed_namespaces (
            feed_id TEXT NOT NULL REFERENCES feeds(id) ON DELETE RESTRICT,
            namespace TEXT NOT NULL,
            rule TEXT NOT NULL CHECK(rule IN ('reserved','allow')),
            PRIMARY KEY(feed_id,namespace)
        );
        CREATE TABLE IF NOT EXISTS feed_policies (
            feed_id TEXT NOT NULL PRIMARY KEY REFERENCES feeds(id) ON DELETE RESTRICT,
            policy_json TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS feed_upstreams (
            feed_id TEXT NOT NULL REFERENCES feeds(id) ON DELETE RESTRICT,
            upstream_id TEXT NOT NULL,
            endpoint TEXT NOT NULL,
            priority INTEGER NOT NULL CHECK(priority>=0),
            enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
            PRIMARY KEY(feed_id,upstream_id),
            UNIQUE(feed_id,priority)
        );
        """;

    private const string NugetDigestSchema = """
        CREATE TABLE IF NOT EXISTS nuget_digests (
            name TEXT NOT NULL, version TEXT NOT NULL,
            sha512 TEXT NOT NULL, size BIGINT NOT NULL CHECK(size>=0),
            origin TEXT NOT NULL CHECK(origin IN ('hosted','mirror')),
            PRIMARY KEY(name,version)
        );
        """;

    // V4: isolated NuGet private hosted feeds. No global package rows, no
    // implicit proxy fallback, no mutable artifact revisions.
    private const string HostedFeedNugetSchema = """
        CREATE TABLE IF NOT EXISTS feed_nuget_versions (
            feed_id TEXT NOT NULL REFERENCES feeds(id) ON DELETE RESTRICT,
            name TEXT NOT NULL, version TEXT NOT NULL,
            description TEXT NOT NULL, sha512 TEXT NOT NULL,
            size BIGINT NOT NULL CHECK(size >= 0),
            created_at TEXT NOT NULL,
            PRIMARY KEY(feed_id,name,version)
        );
        CREATE INDEX IF NOT EXISTS feed_nuget_names
            ON feed_nuget_versions(feed_id,name,version);
        """;

    // V5: global immutable raw artifacts with publication-time SHA-256.
    // These are intentionally NOT isolated feeds; see docs/raw-registry.md.
    private const string RawSchema = """
        CREATE TABLE IF NOT EXISTS raw_artifacts (
            name TEXT NOT NULL, version TEXT NOT NULL,
            sha256 TEXT NOT NULL, size BIGINT NOT NULL CHECK(size>=0),
            media_type TEXT NOT NULL, created_at TEXT NOT NULL,
            PRIMARY KEY(name,version)
        );
        """;

    private static readonly (string Id, string Kind, string Name)[] Defaults =
    [
        ("default-nuget", "nuget", "NuGet legacy"),
        ("default-npm", "npm", "npm legacy"),
        ("default-cargo", "cargo", "Cargo legacy"),
        ("default-pypi", "pypi", "PyPI legacy"),
        ("default-oci", "oci", "OCI legacy"),
        ("default-skill", "skill", "Agent Skills legacy")
    ];

    public static void Verify(SqliteConnection connection)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='packory_schema_version'";
        if (exists.ExecuteScalar() is null) return; // unmarked pre-versioned installation
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM packory_schema_version WHERE slot=1";
        RequireKnown(cmd.ExecuteScalar());
    }

    public static void Stamp(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS packory_schema_version (
                slot INTEGER PRIMARY KEY CHECK(slot=1),
                version INTEGER NOT NULL, applied_at TEXT NOT NULL
            );
            INSERT OR IGNORE INTO packory_schema_version(slot,version,applied_at)
            VALUES(1,$version,$at);
            """;
        cmd.Parameters.AddWithValue("$version", Baseline);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        Verify(connection);
    }

    public static void Upgrade(SqliteConnection connection)
    {
        using var tx = connection.BeginTransaction();
        using var read = connection.CreateCommand();
        read.Transaction = tx;
        read.CommandText = "SELECT version FROM packory_schema_version WHERE slot=1";
        var version = RequireKnown(read.ExecuteScalar());
        if (version == Current) { tx.Commit(); return; }
        if (version < 2)
        {
            using var schema = connection.CreateCommand();
            schema.Transaction = tx;
            schema.CommandText = FeedSchema;
            schema.ExecuteNonQuery();
        }
        var at = DateTimeOffset.UtcNow.ToString("O");
        if (version < 2) foreach (var feed in Defaults)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO feeds(id,kind,mode,visibility,display_name,enabled,created_at,updated_at)
                VALUES($id,$kind,'legacy','runtime',$name,1,$at,$at)
                ON CONFLICT(id) DO NOTHING
                """;
            insert.Parameters.AddWithValue("$id", feed.Id);
            insert.Parameters.AddWithValue("$kind", feed.Kind);
            insert.Parameters.AddWithValue("$name", feed.Name);
            insert.Parameters.AddWithValue("$at", at);
            insert.ExecuteNonQuery();
        }
        if (version < 3)
        {
            using var digestSchema = connection.CreateCommand();
            digestSchema.Transaction = tx;
            digestSchema.CommandText = NugetDigestSchema;
            digestSchema.ExecuteNonQuery();
        }
        if (version < 4)
        {
            using var feedVersions = connection.CreateCommand();
            feedVersions.Transaction = tx;
            feedVersions.CommandText = HostedFeedNugetSchema;
            feedVersions.ExecuteNonQuery();
        }
        if (version < 5)
        {
            using var rawSchema = connection.CreateCommand();
            rawSchema.Transaction = tx;
            rawSchema.CommandText = RawSchema;
            rawSchema.ExecuteNonQuery();
        }
        using (var stamp = connection.CreateCommand())
        {
            stamp.Transaction = tx;
            stamp.CommandText = "UPDATE packory_schema_version SET version=$version,applied_at=$at WHERE slot=1 AND version=$previous";
            stamp.Parameters.AddWithValue("$version", Current);
            stamp.Parameters.AddWithValue("$previous", version);
            stamp.Parameters.AddWithValue("$at", at);
            if (stamp.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Packory database migration lost its version gate.");
        }
        tx.Commit();
    }

    public static void Verify(NpgsqlConnection connection)
    {
        using var exists = new NpgsqlCommand(
            "SELECT to_regclass('packory_schema_version') IS NOT NULL", connection);
        if (exists.ExecuteScalar() is not bool found || !found) return;
        using var cmd = new NpgsqlCommand(
            "SELECT version FROM packory_schema_version WHERE slot=1", connection);
        RequireKnown(cmd.ExecuteScalar());
    }

    public static void Stamp(NpgsqlConnection connection)
    {
        using var cmd = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS packory_schema_version (
                slot INTEGER PRIMARY KEY CHECK(slot=1),
                version INTEGER NOT NULL, applied_at TEXT NOT NULL
            );
            INSERT INTO packory_schema_version(slot,version,applied_at)
            VALUES(1,@version,@at) ON CONFLICT(slot) DO NOTHING;
            """, connection);
        cmd.Parameters.AddWithValue("version", Baseline);
        cmd.Parameters.AddWithValue("at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        Verify(connection);
    }

    public static void Upgrade(NpgsqlConnection connection)
    {
        using var tx = connection.BeginTransaction();
        using var read = new NpgsqlCommand(
            "SELECT version FROM packory_schema_version WHERE slot=1 FOR UPDATE", connection, tx);
        var version = RequireKnown(read.ExecuteScalar());
        if (version == Current) { tx.Commit(); return; }
        if (version < 2)
        {
            using var schema = new NpgsqlCommand(FeedSchema, connection, tx);
            schema.ExecuteNonQuery();
        }
        var at = DateTimeOffset.UtcNow.ToString("O");
        if (version < 2) foreach (var feed in Defaults)
        {
            using var insert = new NpgsqlCommand("""
                INSERT INTO feeds(id,kind,mode,visibility,display_name,enabled,created_at,updated_at)
                VALUES(@id,@kind,'legacy','runtime',@name,1,@at,@at)
                ON CONFLICT(id) DO NOTHING
                """, connection, tx);
            insert.Parameters.AddWithValue("id", feed.Id);
            insert.Parameters.AddWithValue("kind", feed.Kind);
            insert.Parameters.AddWithValue("name", feed.Name);
            insert.Parameters.AddWithValue("at", at);
            insert.ExecuteNonQuery();
        }
        if (version < 3)
        {
            using var digestSchema = new NpgsqlCommand(NugetDigestSchema, connection, tx);
            digestSchema.ExecuteNonQuery();
        }
        if (version < 4)
        {
            using var feedVersions = new NpgsqlCommand(HostedFeedNugetSchema, connection, tx);
            feedVersions.ExecuteNonQuery();
        }
        if (version < 5)
        {
            using var rawSchema = new NpgsqlCommand(RawSchema, connection, tx);
            rawSchema.ExecuteNonQuery();
        }
        using (var stamp = new NpgsqlCommand(
            "UPDATE packory_schema_version SET version=@version,applied_at=@at WHERE slot=1 AND version=@previous",
            connection, tx))
        {
            stamp.Parameters.AddWithValue("version", Current);
            stamp.Parameters.AddWithValue("previous", version);
            stamp.Parameters.AddWithValue("at", at);
            if (stamp.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Packory database migration lost its version gate.");
        }
        tx.Commit();
    }
}
