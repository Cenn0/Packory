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
            Assert.Equal(7, Convert.ToInt32(read.ExecuteScalar()));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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
