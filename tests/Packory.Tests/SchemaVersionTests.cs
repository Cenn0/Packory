using Npgsql;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class SchemaVersionTests
{
    [Fact]
    public void SqliteBaselineIsIdempotentAndPreservesHostedData()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-schema-" + Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Database(new Settings { DataDirectory = root });
            db.Initialize();
            Assert.True(db.PublishNuget("version.test", "1.0.0", "test", "test"));
            db.Initialize();
            Assert.Equal(6, db.Feeds(null, 20).Count);
            Assert.All(db.Feeds(null, 20), entry => Assert.Equal("legacy", entry.Mode));
            Assert.Equal("nuget", db.Feed("default-nuget")?.Kind);
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT version FROM packory_schema_version WHERE slot=1";
            Assert.Equal(SchemaVersionGuard.Current, Convert.ToInt32(cmd.ExecuteScalar()));
            Assert.Equal("hosted", db.PackageOrigin("nuget", "version.test", "1.0.0"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ExistingVersionOneReplaysMigrationWithoutDeletingHostedArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-feed-upgrade-" + Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Database(new Settings { DataDirectory = root });
            db.Initialize();
            Assert.True(db.PublishNuget("legacy.package", "1.0.0", "test", "test"));
            using (var con = db.Open())
            using (var command = con.CreateCommand())
            {
                command.CommandText = "UPDATE packory_schema_version SET version=1 WHERE slot=1";
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            db.Initialize();
            Assert.Equal(6, db.Feeds(null, 20).Count);
            Assert.Equal("hosted", db.PackageOrigin("nuget", "legacy.package", "1.0.0"));
            using var connection = db.Open();
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT version FROM packory_schema_version WHERE slot=1";
            Assert.Equal(SchemaVersionGuard.Current, Convert.ToInt32(read.ExecuteScalar()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SqliteVersionTwoUpgradePreservesExistingPackageButDoesNotInventChecksum()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-v2-upgrade-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Database(new Settings { DataDirectory = root });
            db.Initialize();
            Assert.True(db.PublishNuget("legacy.package", "1.0.0", "legacy",
                "fixture"));
            using (var con = db.Open())
            using (var command = con.CreateCommand())
            {
                command.CommandText = "DROP TABLE nuget_digests";
                command.ExecuteNonQuery();
                command.CommandText = "UPDATE packory_schema_version SET version=2 WHERE slot=1";
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            db.Initialize();
            Assert.Equal("hosted", db.PackageOrigin("nuget", "legacy.package", "1.0.0"));
            Assert.Null(db.GetNugetDigest("legacy.package", "1.0.0"));
            using var check = db.Open();
            using var read = check.CreateCommand();
            read.CommandText = "SELECT version FROM packory_schema_version WHERE slot=1";
            Assert.Equal(SchemaVersionGuard.Current, Convert.ToInt32(read.ExecuteScalar()));
            read.CommandText = "SELECT COUNT(*) FROM nuget_digests";
            Assert.Equal(0, Convert.ToInt32(read.ExecuteScalar()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SqliteV3UpgradeAddsIsolatedFeedSchemaWithoutLosingChecksums()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-v3-upgrade-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Database(new Settings { DataDirectory = root });
            db.Initialize();
            var digest = Convert.ToBase64String(
                System.Security.Cryptography.SHA512.HashData("synthetic"u8.ToArray()));
            Assert.True(db.PublishNuget("original", "1.0.0", "legacy", "fixture",
                digest, "synthetic"u8.Length));
            using (var con = db.Open())
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "DROP TABLE feed_nuget_versions";
                cmd.ExecuteNonQuery();
                cmd.CommandText = "UPDATE packory_schema_version SET version=3 WHERE slot=1";
                Assert.Equal(1, cmd.ExecuteNonQuery());
            }
            db.Initialize();
            Assert.Equal(digest, db.GetNugetDigest("original", "1.0.0")?.Sha512);
            Assert.Empty(db.FeedNugetRevisions());
            using var check = db.Open();
            using var stmt = check.CreateCommand();
            stmt.CommandText = "SELECT version FROM packory_schema_version WHERE slot=1";
            Assert.Equal(SchemaVersionGuard.Current, Convert.ToInt32(stmt.ExecuteScalar()));
            stmt.CommandText = "SELECT COUNT(*) FROM feed_nuget_versions";
            Assert.Equal(0, Convert.ToInt32(stmt.ExecuteScalar()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SqliteV5ToV6UpgradePreservesExistingRawMetadataAndAddsMavenLedger()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-v6-migration-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Database(new Settings { DataDirectory = root });
            db.Initialize();
            var hash = new string('c', 64);
            Assert.True(db.PublishRaw("synthetic.fixture", "1.0.0",
                "application/octet-stream", hash, 123, "test"));
            using (var connection = db.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DROP TABLE maven_assets";
                command.ExecuteNonQuery();
                command.CommandText = "UPDATE packory_schema_version SET version=5 WHERE slot=1";
                Assert.Equal(1, command.ExecuteNonQuery());
            }
            db.Initialize();
            Assert.Equal(hash, db.GetRawArtifact("synthetic.fixture", "1.0.0")?.Sha256);
            Assert.Empty(db.MavenAssets());
            using var check = db.Open();
            using var read = check.CreateCommand();
            read.CommandText = "SELECT version FROM packory_schema_version WHERE slot=1";
            Assert.Equal(SchemaVersionGuard.Current, Convert.ToInt32(read.ExecuteScalar()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SqliteV7FeedMigrationPreservesForeignKeysAndPriorPrivateRevisions()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-feed-v8-upgrade-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Database(new Settings { DataDirectory = root });
            db.Initialize();
            var now = DateTimeOffset.UtcNow.ToString("O");
            Assert.True(db.TryCreateDraftFeed(new FeedDefinition("old-nuget", "nuget",
                "hosted", "private", "Old synthetic private", false, now, now), "fixture"));
            Assert.True(db.TryActivateHostedNugetFeed("old-nuget", "fixture"));
            var checksum = Convert.ToBase64String(
                System.Security.Cryptography.SHA512.HashData("synthetic"u8.ToArray()));
            Assert.True(db.PublishFeedNuget("old-nuget", "same.package", "1.0.0",
                "old private package", checksum, 9, "fixture"));
            using (var connection = db.Open())
            {
                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA foreign_keys=OFF";
                    pragma.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    // Build a real V7 parent table CHECK constraint, not only a
                    // downgraded version marker on the V8 schema.
                    command.CommandText = """
                        CREATE TABLE feeds_old_v7 (
                            id TEXT NOT NULL PRIMARY KEY,
                            kind TEXT NOT NULL CHECK(kind IN ('nuget','npm','cargo','pypi','oci','skill')),
                            mode TEXT NOT NULL CHECK(mode IN ('legacy','hosted','proxy')),
                            visibility TEXT NOT NULL CHECK(visibility IN ('runtime','private','public')),
                            display_name TEXT NOT NULL,
                            enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
                            created_at TEXT NOT NULL, updated_at TEXT NOT NULL
                        );
                        INSERT INTO feeds_old_v7
                            SELECT * FROM feeds;
                        DROP TABLE feeds;
                        ALTER TABLE feeds_old_v7 RENAME TO feeds;
                        CREATE INDEX feeds_kind_id ON feeds(kind,id);
                        DROP TABLE feed_assets;
                        UPDATE packory_schema_version SET version=7 WHERE slot=1;
                        """;
                    command.ExecuteNonQuery();
                }
                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA foreign_keys=ON";
                    pragma.ExecuteNonQuery();
                }
            }
            db.Initialize();
            Assert.Equal(checksum, db.GetFeedNugetRevision(
                "old-nuget", "same.package", "1.0.0")?.Sha512);
            Assert.Equal("nuget", db.Feed("old-nuget")?.Kind);
            Assert.Equal(11, SchemaVersionGuard.Current);
            Assert.True(db.TryCreateDraftFeed(new FeedDefinition("new-raw", "raw",
                "hosted", "private", "New private Raw", false, now, now), "fixture"));
            Assert.True(db.TrySetHostedFeedEnabled("new-raw", "raw", true, "fixture"));
            using var verification = db.Open();
            using var check = verification.CreateCommand();
            check.CommandText = "PRAGMA foreign_key_check";
            using var violations = check.ExecuteReader();
            Assert.False(violations.Read());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SqliteV8ToV9WidensFeedAssetsWithoutLosingImmutableRawRows()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "packory-v9-feed-upgrade-" + Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Database(new Settings { DataDirectory = root });
            db.Initialize();
            var at = DateTimeOffset.UtcNow.ToString("O");
            const string feed = "v9-raw";
            Assert.True(db.TryCreateDraftFeed(new FeedDefinition(feed, "raw",
                "hosted", "private", "Synthetic v8 to v9", false, at, at),
                "synthetic"));
            Assert.True(db.TrySetHostedFeedEnabled(feed, "raw", true, "synthetic"));
            var asset = new FeedAssetEntry(feed, "raw", "same.name/1.0.0",
                "same.name", "1.0.0", "application/octet-stream",
                new string('a', 64), 123, at);
            Assert.True(db.PublishFeedAsset(asset, "synthetic"));

            using (var connection = db.Open())
            {
                using (var pragma = connection.CreateCommand())
                {
                    pragma.CommandText = "PRAGMA foreign_keys=OFF";
                    pragma.ExecuteNonQuery();
                }
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = """
                        CREATE TABLE feed_assets_old_v8 (
                            feed_id TEXT NOT NULL REFERENCES feeds(id) ON DELETE RESTRICT,
                            kind TEXT NOT NULL CHECK(kind IN ('raw','maven')),
                            path TEXT NOT NULL, name TEXT NOT NULL, version TEXT NOT NULL,
                            media_type TEXT NOT NULL, sha256 TEXT NOT NULL,
                            size BIGINT NOT NULL CHECK(size > 0), created_at TEXT NOT NULL,
                            PRIMARY KEY(feed_id,kind,path)
                        );
                        INSERT INTO feed_assets_old_v8 SELECT * FROM feed_assets;
                        DROP TABLE feed_assets;
                        ALTER TABLE feed_assets_old_v8 RENAME TO feed_assets;
                        CREATE INDEX feed_assets_identity
                            ON feed_assets(feed_id,kind,name,version);
                        UPDATE packory_schema_version SET version=8 WHERE slot=1;
                        """;
                    command.ExecuteNonQuery();
                }
                using var enable = connection.CreateCommand();
                enable.CommandText = "PRAGMA foreign_keys=ON";
                enable.ExecuteNonQuery();
            }

            db.Initialize();
            Assert.Equal(asset, db.GetFeedAsset(feed, "raw", asset.Path));
            using var check = db.Open();
            using var version = check.CreateCommand();
            version.CommandText = "SELECT version FROM packory_schema_version WHERE slot=1";
            Assert.Equal(SchemaVersionGuard.Current, Convert.ToInt32(version.ExecuteScalar()));
            version.CommandText = "PRAGMA foreign_key_check";
            using var violations = version.ExecuteReader();
            Assert.False(violations.Read());

            // A new V9 Go ledger row is legal without weakening the already
            // persisted private Raw row or its primary-key isolation.
            Assert.True(db.TryCreateDraftFeed(new FeedDefinition("v9-go", "go",
                "hosted", "private", "Synthetic Go", false, at, at), "synthetic"));
            Assert.True(db.TrySetHostedFeedEnabled("v9-go", "go", true, "synthetic"));
            var goEntry = new FeedAssetEntry("v9-go", "go",
                "example.com/hello/@v/v1.2.3.zip", "example.com/hello",
                "v1.2.3", "application/vnd.packory.go-module-bundle+zip",
                new string('b', 64), 512, at);
            Assert.True(FeedAssetEntry.Valid(goEntry));
            Assert.True(db.PublishFeedAsset(goEntry, "synthetic"));
            Assert.Equal(goEntry, db.GetFeedAsset("v9-go", "go", goEntry.Path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PostgresV8ToV9KeepsFeedRowsAndWidensTheGoConstraint()
    {
        var connectionString = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var schema = "packory_feed_v9_" + Guid.NewGuid().ToString("N");
        using var admin = new NpgsqlConnection(connectionString);
        admin.Open();
        using (var create = new NpgsqlCommand("CREATE SCHEMA " + schema, admin))
            create.ExecuteNonQuery();
        try
        {
            var isolated = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = schema
            };
            var db = new PostgresDatabase(new Settings
            {
                MetadataProvider = "postgresql",
                PostgresConnectionString = isolated.ConnectionString
            });
            db.Initialize();
            var at = DateTimeOffset.UtcNow.ToString("O");
            Assert.True(db.TryCreateDraftFeed(new FeedDefinition("v9-raw", "raw",
                "hosted", "private", "Synthetic raw", false, at, at), "fixture"));
            Assert.True(db.TrySetHostedFeedEnabled("v9-raw", "raw", true, "fixture"));
            var asset = new FeedAssetEntry("v9-raw", "raw", "same.name/1.0.0",
                "same.name", "1.0.0", "application/octet-stream",
                new string('a', 64), 12, at);
            Assert.True(db.PublishFeedAsset(asset, "fixture"));
            using (var connection = new NpgsqlConnection(isolated.ConnectionString))
            {
                connection.Open();
                using var downgrade = new NpgsqlCommand("""
                    ALTER TABLE feed_assets DROP CONSTRAINT feed_assets_kind_check;
                    ALTER TABLE feed_assets ADD CONSTRAINT feed_assets_kind_check
                        CHECK(kind IN ('raw','maven'));
                    UPDATE packory_schema_version SET version=8 WHERE slot=1;
                    """, connection);
                downgrade.ExecuteNonQuery();
            }
            db.Initialize();
            Assert.Equal(asset, db.GetFeedAsset("v9-raw", "raw", asset.Path));
            using var check = new NpgsqlConnection(isolated.ConnectionString);
            check.Open();
            using var cmd = new NpgsqlCommand(
                "SELECT version FROM packory_schema_version WHERE slot=1", check);
            Assert.Equal(SchemaVersionGuard.Current, Convert.ToInt32(cmd.ExecuteScalar()));
            Assert.True(db.TryCreateDraftFeed(new FeedDefinition("v9-go", "go",
                "hosted", "private", "Synthetic Go", false, at, at), "fixture"));
            Assert.True(db.TrySetHostedFeedEnabled("v9-go", "go", true, "fixture"));
            var goAsset = new FeedAssetEntry("v9-go", "go",
                "example.com/hello/@v/v1.2.3.zip", "example.com/hello",
                "v1.2.3", "application/vnd.packory.go-module-bundle+zip",
                new string('b', 64), 128, at);
            Assert.True(db.PublishFeedAsset(goAsset, "fixture"));
            Assert.Equal(goAsset, db.GetFeedAsset("v9-go", "go", goAsset.Path));
        }
        finally
        {
            using var cleanup = new NpgsqlCommand(
                "DROP SCHEMA " + schema + " CASCADE", admin);
            cleanup.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task SqliteV9ToV10PreservesBytesForeignKeysAndAddsPrivatePackageAndOciLedgers()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-v10-migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { DataDirectory = root };
            var db = new Database(settings);
            db.Initialize();
            var at = DateTimeOffset.UtcNow.ToString("O");
            var original = SeedV9Raw(db, at);
            var content = "synthetic immutable pre-upgrade bytes"u8.ToArray();
            var blobs = new BlobStore(settings);
            var key = FeedAssetEntry.BlobKey(original);
            Assert.True(await blobs.PutAsync(key, new MemoryStream(content), content.Length, CancellationToken.None));
            using (var old = db.Open())
            using (var cmd = old.CreateCommand())
            {
                cmd.CommandText = """
                    PRAGMA foreign_keys=OFF;
                    DROP TABLE feed_oci_tags;
                    CREATE TABLE feed_assets_old_v9 (
                        feed_id TEXT NOT NULL REFERENCES feeds(id) ON DELETE RESTRICT,
                        kind TEXT NOT NULL CHECK(kind IN ('raw','maven','go')),
                        path TEXT NOT NULL, name TEXT NOT NULL, version TEXT NOT NULL,
                        media_type TEXT NOT NULL, sha256 TEXT NOT NULL,
                        size BIGINT NOT NULL CHECK(size > 0), created_at TEXT NOT NULL,
                        PRIMARY KEY(feed_id,kind,path)
                    );
                    INSERT INTO feed_assets_old_v9 SELECT * FROM feed_assets;
                    DROP TABLE feed_assets;
                    ALTER TABLE feed_assets_old_v9 RENAME TO feed_assets;
                    CREATE INDEX feed_assets_identity ON feed_assets(feed_id,kind,name,version);
                    UPDATE packory_schema_version SET version=9 WHERE slot=1;
                    PRAGMA foreign_keys=ON;
                    """;
                cmd.ExecuteNonQuery();
            }
            db.Initialize();
            db.Initialize();
            Assert.Equal(original, db.GetFeedAsset(original.FeedId, "raw", original.Path));
            Assert.Equal(content, await File.ReadAllBytesAsync(blobs.Existing(key)!));
            Assert.Equal("feeds/v10-raw/raw/same.name/1.0.0/payload.blob", key);
            VerifyV10Publication(db, at);
            using var connection = db.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT version FROM packory_schema_version WHERE slot=1";
            Assert.Equal(SchemaVersionGuard.Current, Convert.ToInt32(query.ExecuteScalar()));
            query.CommandText = "PRAGMA foreign_key_check";
            using (var rows = query.ExecuteReader()) Assert.False(rows.Read());
            query.CommandText = "PRAGMA foreign_key_list(feed_assets)";
            using (var rows = query.ExecuteReader())
            {
                Assert.True(rows.Read());
                Assert.Equal("feeds", rows.GetString(2));
            }
            query.CommandText = "UPDATE feed_assets SET size=0 WHERE kind='raw'";
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => query.ExecuteNonQuery());
            query.CommandText = "DELETE FROM feeds WHERE id='v10-oci'";
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => query.ExecuteNonQuery());
            query.CommandText = "INSERT INTO feed_oci_tags SELECT * FROM feed_oci_tags";
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => query.ExecuteNonQuery());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SqliteV10MigrationRollsBackMarkerAndChildTableWhenForeignKeysAreInvalid()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-v10-rollback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Database(new Settings { DataDirectory = root });
            db.Initialize();
            var original = SeedV9Raw(db, DateTimeOffset.UtcNow.ToString("O"));
            using (var connection = db.Open())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    PRAGMA foreign_keys=OFF;
                    UPDATE feed_assets SET feed_id='missing-feed';
                    UPDATE packory_schema_version SET version=9 WHERE slot=1;
                    PRAGMA foreign_keys=ON;
                    """;
                cmd.ExecuteNonQuery();
            }
            Assert.Throws<InvalidOperationException>(() => db.Initialize());
            using var check = db.Open();
            using var query = check.CreateCommand();
            query.CommandText = "SELECT version FROM packory_schema_version WHERE slot=1";
            Assert.Equal(9, Convert.ToInt32(query.ExecuteScalar()));
            query.CommandText = "SELECT sha256 FROM feed_assets WHERE feed_id='missing-feed'";
            Assert.Equal(original.Sha256, query.ExecuteScalar());
            query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='feed_assets_v10_new'";
            Assert.Equal(0, Convert.ToInt32(query.ExecuteScalar()));
            query.CommandText = "PRAGMA foreign_keys";
            Assert.Equal(1, Convert.ToInt32(query.ExecuteScalar()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PostgresV9ToV10PreservesRowsAndEnforcesOciTagAndZeroByteConstraints()
    {
        var connectionString = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        var schema = "packory_feed_v10_" + Guid.NewGuid().ToString("N");
        using var admin = new NpgsqlConnection(connectionString);
        admin.Open();
        using (var create = new NpgsqlCommand("CREATE SCHEMA " + schema, admin)) create.ExecuteNonQuery();
        try
        {
            var isolated = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            var db = new PostgresDatabase(new Settings { MetadataProvider = "postgresql", PostgresConnectionString = isolated.ConnectionString });
            db.Initialize();
            var at = DateTimeOffset.UtcNow.ToString("O");
            var original = SeedV9Raw(db, at);
            using (var connection = new NpgsqlConnection(isolated.ConnectionString))
            {
                connection.Open();
                using var old = new NpgsqlCommand("""
                    DROP TABLE feed_oci_tags;
                    ALTER TABLE feed_assets DROP CONSTRAINT feed_assets_kind_check;
                    ALTER TABLE feed_assets ADD CONSTRAINT feed_assets_kind_check CHECK(kind IN ('raw','maven','go'));
                    ALTER TABLE feed_assets DROP CONSTRAINT feed_assets_size_check;
                    ALTER TABLE feed_assets ADD CONSTRAINT feed_assets_size_check CHECK(size > 0);
                    UPDATE packory_schema_version SET version=9 WHERE slot=1;
                    """, connection);
                old.ExecuteNonQuery();
            }
            db.Initialize();
            db.Initialize();
            Assert.Equal(original, db.GetFeedAsset(original.FeedId, "raw", original.Path));
            VerifyV10Publication(db, at);
            using var check = new NpgsqlConnection(isolated.ConnectionString);
            check.Open();
            using var cmd = new NpgsqlCommand("SELECT version FROM packory_schema_version WHERE slot=1", check);
            Assert.Equal(SchemaVersionGuard.Current, Convert.ToInt32(cmd.ExecuteScalar()));
            cmd.CommandText = "UPDATE feed_assets SET size=0 WHERE kind='raw'";
            Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.Throws<PostgresException>(() => cmd.ExecuteNonQuery()).SqlState);
            cmd.CommandText = "DELETE FROM feeds WHERE id='v10-oci'";
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.Throws<PostgresException>(() => cmd.ExecuteNonQuery()).SqlState);
            cmd.CommandText = "INSERT INTO feed_oci_tags SELECT * FROM feed_oci_tags";
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.Throws<PostgresException>(() => cmd.ExecuteNonQuery()).SqlState);
        }
        finally
        {
            using var drop = new NpgsqlCommand("DROP SCHEMA " + schema + " CASCADE", admin);
            drop.ExecuteNonQuery();
        }
    }

    private static FeedAssetEntry SeedV9Raw(IPackageDatabase db, string at)
    {
        Assert.True(db.TryCreateDraftFeed(new FeedDefinition("v10-raw", "raw", "hosted", "private", "Synthetic old raw", false, at, at), "synthetic"));
        Assert.True(db.TrySetHostedFeedEnabled("v10-raw", "raw", true, "synthetic"));
        var content = "synthetic immutable pre-upgrade bytes"u8.ToArray();
        var original = new FeedAssetEntry("v10-raw", "raw", "same.name/1.0.0", "same.name", "1.0.0", "application/octet-stream",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), content.Length, at);
        Assert.True(db.PublishFeedAsset(original, "synthetic"));
        return original;
    }

    private static void VerifyV10Publication(IPackageDatabase db, string at)
    {
        foreach (var kind in new[] { "npm", "cargo", "pypi", "oci" })
        {
            Assert.True(db.TryCreateDraftFeed(new FeedDefinition("v10-" + kind, kind, "hosted", "private", "Synthetic " + kind, false, at, at), "synthetic"));
            Assert.True(db.TrySetHostedFeedEnabled("v10-" + kind, kind, true, "synthetic"));
        }
        foreach (var kind in new[] { "npm", "cargo", "pypi" })
        {
            var path = kind == "pypi" ? "same-package/same_package-1.0.0-py3-none-any.whl" : "same-package/1.0.0";
            var row = new FeedAssetEntry("v10-" + kind, kind, path, "same-package", "1.0.0",
                "application/vnd.packory." + kind + "-package+zip", new string('a', 64), 123, at);
            Assert.True(db.PublishFeedAsset(row, "synthetic"));
            Assert.Equal(row, db.GetFeedAsset(row.FeedId, kind, path));
            Assert.False(db.PublishFeedAsset(row with { Sha256 = new string('b', 64) }, "synthetic"));
        }
        var empty = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData([]));
        var blob = new FeedAssetEntry("v10-oci", "oci", "blob/team/image/" + empty,
            "team/image", "sha256:" + empty, "application/octet-stream", empty, 0, at);
        Assert.True(db.PublishFeedAsset(blob, "synthetic"));
        Assert.Throws<InvalidOperationException>(() => db.SetFeedOciTag("v10-oci", "team/image", "latest", blob.Version, "synthetic"));
        var digest = new string('b', 64);
        var manifest = blob with { Path = "manifest/team/image/" + digest, Version = "sha256:" + digest,
            Sha256 = digest, MediaType = FeedOciEndpoints.ManifestMedia, Size = 123 };
        Assert.True(db.PublishFeedAsset(manifest, "synthetic"));
        db.SetFeedOciTag("v10-oci", "team/image", "latest", manifest.Version, "synthetic");
        Assert.Equal(manifest.Version, db.GetFeedOciTag("v10-oci", "team/image", "latest"));
        Assert.Throws<InvalidOperationException>(() => db.SetFeedOciTag("v10-oci", "different/image", "latest", manifest.Version, "synthetic"));
    }

    [Fact]
    public void SqliteRejectsFutureVersionWithoutOverwritingMarker()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-schema-" + Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Database(new Settings { DataDirectory = root });
            db.Initialize();
            using (var connection = db.Open())
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE packory_schema_version SET version=99 WHERE slot=1";
                Assert.Equal(1, cmd.ExecuteNonQuery());
            }
            Assert.Throws<InvalidOperationException>(() => db.Initialize());
            using var check = db.Open();
            using var query = check.CreateCommand();
            query.CommandText = "SELECT version FROM packory_schema_version WHERE slot=1";
            Assert.Equal(99, Convert.ToInt32(query.ExecuteScalar()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PostgreSqlUsesSameBaseline()
    {
        var connectionString = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) return;
        // Baseline counts must not include hosted feeds from other provider tests.
        var schema = "packory_schema_" + Guid.NewGuid().ToString("N");
        using var administrator = new NpgsqlConnection(connectionString);
        administrator.Open();
        using (var create = new NpgsqlCommand("CREATE SCHEMA " + schema, administrator))
            create.ExecuteNonQuery();
        try
        {
            var isolated = new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = schema };
            var database = new PostgresDatabase(new Settings { MetadataProvider = "postgresql",
                PostgresConnectionString = isolated.ConnectionString });
            database.Initialize();
            using var connection = new NpgsqlConnection(isolated.ConnectionString);
            connection.Open();
            SchemaVersionGuard.Verify(connection);
            var feeds = database.Feeds(null, 20);
            Assert.Equal(6, feeds.Count);
            var firstPage = database.Feeds(null, 2);
            Assert.Equal(feeds.Take(2).Select(x => x.Id), firstPage.Select(x => x.Id));
            var remaining = database.Feeds(firstPage[^1].Id, 20);
            Assert.Equal(feeds.Skip(2).Select(x => x.Id), remaining.Select(x => x.Id));
            Assert.Empty(database.Feeds(feeds[^1].Id, 20));
            Assert.Equal("oci", database.Feed("default-oci")?.Kind);
            using var cmd = new NpgsqlCommand("SELECT version FROM packory_schema_version WHERE slot=1", connection);
            Assert.Equal(SchemaVersionGuard.Current, Convert.ToInt32(cmd.ExecuteScalar()));
        }
        finally
        {
            using var drop = new NpgsqlCommand("DROP SCHEMA " + schema + " CASCADE", administrator);
            drop.ExecuteNonQuery();
        }
    }
}
