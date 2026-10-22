using Microsoft.Data.Sqlite;
using Npgsql;

namespace Packory;

/// <summary>
/// Explicit forward-only database versions. V11 introduced mutable Maven
/// metadata; V12 permits G-level plugin mappings; V13 adds private npm mutable tag pointers;
/// V14 adds feed-local Cargo yank pointers.
/// Immutable global/private artifact rows and keys remain unchanged.
/// </summary>
public static class SchemaVersionGuard
{
    private const int Baseline = 1;
    public const int Current = 14;

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
            kind TEXT NOT NULL CHECK(kind IN ('nuget','npm','cargo','pypi','oci','skill','raw','maven','go')),
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

    // V6: immutable Maven release assets. The transport remains globally scoped;
    // only separately accepted hosted/proxy feeds may claim feed-local isolation.
    private const string MavenAssetSchema = """
        CREATE TABLE IF NOT EXISTS maven_assets (
            path TEXT NOT NULL PRIMARY KEY,
            group_id TEXT NOT NULL, artifact_id TEXT NOT NULL,
            version TEXT NOT NULL, sha256 TEXT NOT NULL,
            size BIGINT NOT NULL CHECK(size > 0),
            media_type TEXT NOT NULL, created_at TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS maven_assets_coordinates
            ON maven_assets(group_id,artifact_id,version);
        """;

    // V7: immutable Go .info/.mod/.zip triplet committed as one blob/metadata revision.
    // Global hosted only, no cross-protocol feed isolation claim.
    private const string GoModuleSchema = """
        CREATE TABLE IF NOT EXISTS go_modules (
            name TEXT NOT NULL, version TEXT NOT NULL,
            bundle_sha256 TEXT NOT NULL, bundle_size BIGINT NOT NULL CHECK(bundle_size > 0),
            zip_sha256 TEXT NOT NULL, zip_size BIGINT NOT NULL CHECK(zip_size > 0),
            mod_sha256 TEXT NOT NULL, mod_size BIGINT NOT NULL CHECK(mod_size > 0),
            published_at TEXT NOT NULL, PRIMARY KEY(name,version)
        );
        CREATE INDEX IF NOT EXISTS go_modules_by_name ON go_modules(name,version);
        """;

    // V8: private hosted raw and future Maven/Go feed scope. Immutable
    // references are keyed by feed+kind+canonical path, never global catalog.
    // All native private adapters use the same durable publication journal.
    private const string FeedAssetSchema = """
        CREATE TABLE IF NOT EXISTS feed_assets (
            feed_id TEXT NOT NULL REFERENCES feeds(id) ON DELETE RESTRICT,
            kind TEXT NOT NULL CHECK(kind IN ('raw','maven','go')),
            path TEXT NOT NULL, name TEXT NOT NULL, version TEXT NOT NULL,
            media_type TEXT NOT NULL, sha256 TEXT NOT NULL,
            size BIGINT NOT NULL CHECK(size > 0), created_at TEXT NOT NULL,
            PRIMARY KEY(feed_id,kind,path)
        );
        CREATE INDEX IF NOT EXISTS feed_assets_identity
            ON feed_assets(feed_id,kind,name,version);
        """;

    // Unlike a renamed-old-table migration this preserves child foreign-key
    // references to the feeds table name, as required by SQLite's documented
    // generalized schema-change procedure.
    private const string SqliteFeedKindMigration = """
        CREATE TABLE feeds_e0_new (
            id TEXT NOT NULL PRIMARY KEY,
            kind TEXT NOT NULL CHECK(kind IN ('nuget','npm','cargo','pypi','oci','skill','raw','maven','go')),
            mode TEXT NOT NULL CHECK(mode IN ('legacy','hosted','proxy')),
            visibility TEXT NOT NULL CHECK(visibility IN ('runtime','private','public')),
            display_name TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
            created_at TEXT NOT NULL, updated_at TEXT NOT NULL
        );
        INSERT INTO feeds_e0_new(id,kind,mode,visibility,display_name,enabled,created_at,updated_at)
            SELECT id,kind,mode,visibility,display_name,enabled,created_at,updated_at FROM feeds;
        DROP TABLE feeds;
        ALTER TABLE feeds_e0_new RENAME TO feeds;
        CREATE INDEX feeds_kind_id ON feeds(kind,id);
        """;

    // V9: widen only the immutable child ledger kind CHECK. Existing private
    // Raw/Maven data must survive bit-for-bit; never drop the parent feeds table.
    private const string SqliteFeedAssetGoMigration = """
        CREATE TABLE feed_assets_v9_new (
            feed_id TEXT NOT NULL REFERENCES feeds(id) ON DELETE RESTRICT,
            kind TEXT NOT NULL CHECK(kind IN ('raw','maven','go')),
            path TEXT NOT NULL, name TEXT NOT NULL, version TEXT NOT NULL,
            media_type TEXT NOT NULL, sha256 TEXT NOT NULL,
            size BIGINT NOT NULL CHECK(size > 0), created_at TEXT NOT NULL,
            PRIMARY KEY(feed_id,kind,path)
        );
        INSERT INTO feed_assets_v9_new
            (feed_id,kind,path,name,version,media_type,sha256,size,created_at)
        SELECT feed_id,kind,path,name,version,media_type,sha256,size,created_at
        FROM feed_assets;
        DROP TABLE feed_assets;
        ALTER TABLE feed_assets_v9_new RENAME TO feed_assets;
        CREATE INDEX feed_assets_identity
            ON feed_assets(feed_id,kind,name,version);
        """;

    // V10: all delivered private hosted adapters share one immutable ledger.
    // Zero bytes are valid OCI blob content; other artifact kinds remain nonempty.
    private const string SqliteFeedAssetV10Migration = """
        CREATE TABLE feed_assets_v10_new (
            feed_id TEXT NOT NULL REFERENCES feeds(id) ON DELETE RESTRICT,
            kind TEXT NOT NULL CHECK(kind IN ('raw','maven','go','npm','cargo','pypi','oci')),
            path TEXT NOT NULL, name TEXT NOT NULL, version TEXT NOT NULL,
            media_type TEXT NOT NULL, sha256 TEXT NOT NULL,
            size BIGINT NOT NULL CHECK(size > 0 OR (kind='oci' AND size=0)),
            created_at TEXT NOT NULL,
            PRIMARY KEY(feed_id,kind,path)
        );
        INSERT INTO feed_assets_v10_new
            (feed_id,kind,path,name,version,media_type,sha256,size,created_at)
        SELECT feed_id,kind,path,name,version,media_type,sha256,size,created_at
        FROM feed_assets;
        DROP TABLE feed_assets;
        ALTER TABLE feed_assets_v10_new RENAME TO feed_assets;
        CREATE INDEX feed_assets_identity ON feed_assets(feed_id,kind,name,version);
        """;

    private const string FeedOciTagSchema = """
        CREATE TABLE IF NOT EXISTS feed_oci_tags (
            feed_id TEXT NOT NULL REFERENCES feeds(id) ON DELETE RESTRICT,
            name TEXT NOT NULL, tag TEXT NOT NULL, digest TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY(feed_id,name,tag)
        );
        """;

    // Metadata is mutable protocol state, not an immutable blob. Keeping the
    // original bounded XML bytes and digest in one transaction avoids a new
    // cross-store publication boundary and preserves native sidecar checksums.
    // Empty scope denotes the legacy global repository; other scopes are exact
    // private feed IDs. Provider writes additionally verify the active feed.
    private const string MavenMetadataSchema = """
        CREATE TABLE IF NOT EXISTS maven_metadata (
            scope TEXT NOT NULL,
            path TEXT NOT NULL,
            content_base64 TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            size BIGINT NOT NULL CHECK(size > 0),
            updated_at TEXT NOT NULL,
            PRIMARY KEY(scope,path)
        );
        """;

    // Mutable npm dist-tags are separate pointers. Immutable published tarballs
    // and publication-time manifests remain unchanged even when tags move.
    private const string FeedNpmTagSchema = """
        CREATE TABLE IF NOT EXISTS feed_npm_tags (
            feed_id TEXT NOT NULL REFERENCES feeds(id) ON DELETE RESTRICT,
            name TEXT NOT NULL, tag TEXT NOT NULL,
            version TEXT NOT NULL,
            deleted INTEGER NOT NULL CHECK(deleted IN (0,1)),
            updated_at TEXT NOT NULL,
            PRIMARY KEY(feed_id,name,tag)
        );
        """;

    // V14: Yank is mutable sparse-index state, never an overwrite of the immutable
    // Cargo crate bundle. The full coordinate remains feed-scoped and audited.
    private const string FeedCargoYankSchema = """
        CREATE TABLE IF NOT EXISTS feed_cargo_yanks (
            feed_id TEXT NOT NULL REFERENCES feeds(id) ON DELETE RESTRICT,
            name TEXT NOT NULL, version TEXT NOT NULL,
            yanked INTEGER NOT NULL CHECK(yanked IN (0,1)),
            updated_at TEXT NOT NULL,
            PRIMARY KEY(feed_id,name,version)
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
        // The v8 CHECK expansion rebuilds the parent feeds table. SQLite
        // requires foreign-key enforcement to be suspended BEFORE BEGIN,
        // then verified inside the transaction before commit.
        using var current = connection.CreateCommand();
        current.CommandText = "SELECT version FROM packory_schema_version WHERE slot=1";
        var previousVersion = RequireKnown(current.ExecuteScalar());
        if (previousVersion == Current) return;
        using (var disable = connection.CreateCommand())
        {
            disable.CommandText = "PRAGMA foreign_keys=OFF";
            disable.ExecuteNonQuery();
        }
        try
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
        if (version < 6)
        {
            using var mavenSchema = connection.CreateCommand();
            mavenSchema.Transaction = tx;
            mavenSchema.CommandText = MavenAssetSchema;
            mavenSchema.ExecuteNonQuery();
        }
        if (version < 7)
        {
            using var goSchema = connection.CreateCommand();
            goSchema.Transaction = tx;
            goSchema.CommandText = GoModuleSchema;
            goSchema.ExecuteNonQuery();
        }
        if (version < 8)
        {
            using var migration = connection.CreateCommand();
            migration.Transaction = tx;
            migration.CommandText = SqliteFeedKindMigration + FeedAssetSchema;
            migration.ExecuteNonQuery();
            using var check = connection.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "PRAGMA foreign_key_check";
            using var violations = check.ExecuteReader();
            if (violations.Read())
                throw new InvalidOperationException("Foreign-key violation after feed migration.");
        }
        if (version >= 8 && version < 9)
        {
            using var migration = connection.CreateCommand();
            migration.Transaction = tx;
            migration.CommandText = SqliteFeedAssetGoMigration;
            migration.ExecuteNonQuery();
            using var check = connection.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "PRAGMA foreign_key_check";
            using var violations = check.ExecuteReader();
            if (violations.Read())
                throw new InvalidOperationException("Foreign-key violation after V9 feed migration.");
        }
        if (version < 10)
        {
            using var migration = connection.CreateCommand();
            migration.Transaction = tx;
            migration.CommandText = SqliteFeedAssetV10Migration + FeedOciTagSchema;
            migration.ExecuteNonQuery();
            using var check = connection.CreateCommand();
            check.Transaction = tx;
            check.CommandText = "PRAGMA foreign_key_check";
            using var violations = check.ExecuteReader();
            if (violations.Read())
                throw new InvalidOperationException("Foreign-key violation after V10 feed migration.");
        }
        if (version < 11)
        {
            using var metadata = connection.CreateCommand();
            metadata.Transaction = tx;
            metadata.CommandText = MavenMetadataSchema;
            metadata.ExecuteNonQuery();
        }
        if (version < 13)
        {
            using var npmTags = connection.CreateCommand();
            npmTags.Transaction = tx;
            npmTags.CommandText = FeedNpmTagSchema;
            npmTags.ExecuteNonQuery();
        }
        if (version < 14)
        {
            using var cargoYanks = connection.CreateCommand();
            cargoYanks.Transaction = tx;
            cargoYanks.CommandText = FeedCargoYankSchema;
            cargoYanks.ExecuteNonQuery();
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
        finally
        {
            using var enable = connection.CreateCommand();
            enable.CommandText = "PRAGMA foreign_keys=ON";
            enable.ExecuteNonQuery();
        }
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
        if (version < 6)
        {
            using var mavenSchema = new NpgsqlCommand(MavenAssetSchema, connection, tx);
            mavenSchema.ExecuteNonQuery();
        }
        if (version < 7)
        {
            using var goSchema = new NpgsqlCommand(GoModuleSchema, connection, tx);
            goSchema.ExecuteNonQuery();
        }
        if (version < 8)
        {
            using var schema = new NpgsqlCommand("""
                ALTER TABLE feeds DROP CONSTRAINT IF EXISTS feeds_kind_check;
                ALTER TABLE feeds ADD CONSTRAINT feeds_kind_check
                    CHECK(kind IN ('nuget','npm','cargo','pypi','oci','skill','raw','maven','go'));
                """ + FeedAssetSchema, connection, tx);
            schema.ExecuteNonQuery();
        }
        if (version >= 8 && version < 9)
        {
            using var schema = new NpgsqlCommand("""
                ALTER TABLE feed_assets DROP CONSTRAINT IF EXISTS feed_assets_kind_check;
                ALTER TABLE feed_assets ADD CONSTRAINT feed_assets_kind_check
                    CHECK(kind IN ('raw','maven','go'));
                """, connection, tx);
            schema.ExecuteNonQuery();
        }
        if (version < 10)
        {
            using var schema = new NpgsqlCommand("""
                ALTER TABLE feed_assets DROP CONSTRAINT IF EXISTS feed_assets_kind_check;
                ALTER TABLE feed_assets ADD CONSTRAINT feed_assets_kind_check
                    CHECK(kind IN ('raw','maven','go','npm','cargo','pypi','oci'));
                ALTER TABLE feed_assets DROP CONSTRAINT IF EXISTS feed_assets_size_check;
                ALTER TABLE feed_assets ADD CONSTRAINT feed_assets_size_check
                    CHECK(size > 0 OR (kind='oci' AND size=0));
                """ + FeedOciTagSchema, connection, tx);
            schema.ExecuteNonQuery();
        }
        if (version < 11)
        {
            using var metadata = new NpgsqlCommand(MavenMetadataSchema, connection, tx);
            metadata.ExecuteNonQuery();
        }
        if (version < 13)
        {
            using var npmTags = new NpgsqlCommand(FeedNpmTagSchema, connection, tx);
            npmTags.ExecuteNonQuery();
        }
        if (version < 14)
        {
            using var cargoYanks = new NpgsqlCommand(FeedCargoYankSchema, connection, tx);
            cargoYanks.ExecuteNonQuery();
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
