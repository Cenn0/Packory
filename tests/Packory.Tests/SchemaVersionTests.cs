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
        var database = new PostgresDatabase(new Settings { MetadataProvider = "postgresql",
            PostgresConnectionString = connectionString });
        database.Initialize();
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();
        SchemaVersionGuard.Verify(connection);
        Assert.Equal(6, database.Feeds(null, 20).Count);
        Assert.Equal("oci", database.Feed("default-oci")?.Kind);
        using var cmd = new NpgsqlCommand("SELECT version FROM packory_schema_version WHERE slot=1", connection);
        Assert.Equal(2, Convert.ToInt32(cmd.ExecuteScalar()));
    }
}
