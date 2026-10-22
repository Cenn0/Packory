using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class MavenMetadataProviderTests
{
    private const string MetadataPath = "org/example/library/maven-metadata.xml";
    private const string PomPath = "org/example/library/1.0.0/library-1.0.0.pom";
    private const string At = "2026-10-22T00:00:00.0000000+00:00";

    private sealed class Fixture : IDisposable
    {
        private readonly NpgsqlConnection? administrator;
        private readonly string? schema;
        public Settings Settings { get; }
        public IPackageDatabase Db { get; }
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "packory-maven-v11-" + Guid.NewGuid().ToString("N"));

        public Fixture(bool postgres)
        {
            Settings = new Settings { DataDirectory = Root };
            if (postgres)
            {
                var connection = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
                Assert.False(string.IsNullOrWhiteSpace(connection));
                administrator = new NpgsqlConnection(connection);
                administrator.Open();
                schema = "packory_maven_" + Guid.NewGuid().ToString("N");
                using var create = new NpgsqlCommand("CREATE SCHEMA " + schema, administrator);
                create.ExecuteNonQuery();
                Settings.MetadataProvider = "postgresql";
                Settings.PostgresConnectionString = new NpgsqlConnectionStringBuilder(connection)
                    { SearchPath = schema, Pooling = false }.ConnectionString;
            }
            Db = Reopen();
            Db.Initialize();
        }

        public IPackageDatabase Reopen() => Settings.MetadataProvider == "postgresql"
            ? new PostgresDatabase(Settings) : new Database(Settings);

        public void Sql(string sql)
        {
            if (Settings.MetadataProvider == "postgresql")
            {
                using var connection = new NpgsqlConnection(Settings.PostgresConnectionString);
                connection.Open();
                using var command = new NpgsqlCommand(sql, connection);
                command.ExecuteNonQuery();
            }
            else
            {
                using var connection = ((Database)Db).Open();
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        public void Feed(string id, string kind = "maven", bool active = true)
        {
            Assert.True(Db.TryCreateDraftFeed(new FeedDefinition(id, kind, "hosted", "private",
                "Synthetic Maven persistence fixture", false, At, At), "test"));
            if (active) Assert.True(Db.TrySetHostedFeedEnabled(id, kind, true, "test"));
        }

        public MavenAssetEntry SeedPom()
        {
            var bytes = "<project><groupId>org.example</groupId><artifactId>library</artifactId><version>1.0.0</version></project>"u8.ToArray();
            var asset = new MavenAssetEntry(PomPath, "org.example", "library", "1.0.0",
                Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, "application/xml", At);
            var blobs = new BlobStore(Settings);
            Assert.True(blobs.PutAsync(MavenEndpoints.Key(PomPath), new MemoryStream(bytes), bytes.Length,
                CancellationToken.None).GetAwaiter().GetResult());
            Assert.True(Db.PublishMavenAsset(asset, "test"));
            return asset;
        }

        public void Dispose()
        {
            if (administrator is not null)
            {
                using var drop = new NpgsqlCommand("DROP SCHEMA " + schema + " CASCADE", administrator);
                drop.ExecuteNonQuery();
                administrator.Dispose();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static MavenMetadataEntry Entry(string scope = "", string whitespace = "")
    {
        var xml = "<metadata><groupId>org.example</groupId><artifactId>library</artifactId>" + whitespace +
            "<versioning><latest>1.0.0</latest><release>1.0.0</release><versions><version>1.0.0</version>" +
            "</versions><lastUpdated>20261022000000</lastUpdated></versioning></metadata>";
        var bytes = Encoding.UTF8.GetBytes(xml);
        return new(scope, MetadataPath, Convert.ToBase64String(bytes),
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, At);
    }

    [Fact]
    public async Task SqliteMetadataHasExactBytesCompareAndSwapAndScopedAudit() => await VerifyPersistence(false);

    [PostgresFeedFact]
    public async Task PostgresMetadataHasExactBytesCompareAndSwapAndScopedAudit() => await VerifyPersistence(true);

    private static async Task VerifyPersistence(bool postgres)
    {
        using var fixture = new Fixture(postgres);
        var db = fixture.Db;
        fixture.Feed("maven-alpha");
        fixture.Feed("maven-beta");
        fixture.Feed("maven-draft", active: false);
        fixture.Feed("wrong-kind", "raw");
        var original = Entry();
        Assert.True(db.PutMavenMetadata(original, "test", null));
        Assert.False(db.PutMavenMetadata(original, "test", null));
        Assert.Equal(original, fixture.Reopen().GetMavenMetadata("", MetadataPath));
        Assert.Null(db.GetMavenMetadata("maven-alpha", MetadataPath));
        Assert.True(db.PutMavenMetadata(Entry("maven-alpha", "\n"), "test", null));
        Assert.True(db.PutMavenMetadata(Entry("maven-beta", "\r\n"), "test", null));
        Assert.False(db.PutMavenMetadata(Entry("missing-feed"), "test", null));
        Assert.False(db.PutMavenMetadata(Entry("maven-draft"), "test", null));
        Assert.False(db.PutMavenMetadata(Entry("wrong-kind"), "test", null));
        Assert.False(db.PutMavenMetadata(Entry("maven-draft"), "test", original.Sha256));
        var before = db.AuditEvents(null, 100).Count;
        Assert.False(db.PutMavenMetadata(Entry(whitespace: " "), "test", new string('0', 64)));
        Assert.Equal(before, db.AuditEvents(null, 100).Count);
        var a = Entry(whitespace: "\n");
        var b = Entry(whitespace: "\r\n");
        var writes = await Task.WhenAll(
            Task.Run(() => fixture.Reopen().PutMavenMetadata(a, "test", original.Sha256)),
            Task.Run(() => fixture.Reopen().PutMavenMetadata(b, "test", original.Sha256)));
        Assert.Single(writes, x => x);
        Assert.Equal(writes[0] ? a : b, db.GetMavenMetadata("", MetadataPath));
        Assert.Equal(before + 1, db.AuditEvents(null, 100).Count);
        Assert.Equal(3, db.MavenMetadata().Count);
        Assert.Equal(Entry("maven-alpha", "\n"), db.GetMavenMetadata("maven-alpha", MetadataPath));
        Assert.True(db.TrySetHostedFeedEnabled("maven-alpha", "maven", false, "test"));
        var privateEntry = db.GetMavenMetadata("maven-alpha", MetadataPath)!;
        Assert.False(db.PutMavenMetadata(Entry("maven-alpha", " "), "test", privateEntry.Sha256));
        Assert.Equal(privateEntry, db.GetMavenMetadata("maven-alpha", MetadataPath));
        Assert.False(db.TryDeleteDraftFeed("maven-alpha", "test"));
        Assert.Throws<ArgumentException>(() => db.PutMavenMetadata(original with { Sha256 = new string('0', 64) }, "test", null));
        Assert.Throws<ArgumentException>(() => db.PutMavenMetadata(original with { Size = original.Size + 1 }, "test", null));
    }

    [Fact]
    public void SqliteV10UpgradePreservesImmutableRowsAndAddsMetadata() => VerifyMigration(false);

    [PostgresFeedFact]
    public void PostgresV10UpgradePreservesImmutableRowsAndAddsMetadata() => VerifyMigration(true);

    private static void VerifyMigration(bool postgres)
    {
        using var fixture = new Fixture(postgres);
        var original = fixture.SeedPom();
        fixture.Feed("migration-feed");
        fixture.Sql("DROP TABLE maven_metadata; UPDATE packory_schema_version SET version=10 WHERE slot=1;");
        var migrated = fixture.Reopen();
        migrated.Initialize();
        migrated.Initialize();
        Assert.Equal(original, migrated.GetMavenAsset(PomPath));
        Assert.NotNull(migrated.Feed("migration-feed"));
        Assert.Empty(migrated.MavenMetadata());
        var metadata = Entry();
        Assert.True(migrated.PutMavenMetadata(metadata, "test", null));
        Assert.True(MavenMetadata.ReferencesValid(metadata, migrated));
        Assert.Equal(metadata, fixture.Reopen().GetMavenMetadata("", MetadataPath));
        Assert.Equal(0, new ArtifactRecovery(fixture.Settings, migrated, new BlobStore(fixture.Settings),
            NullLogger<ArtifactRecovery>.Instance).MissingHostedCount());
        Assert.True(migrated.IsHealthy());
    }

    [Fact]
    public void SqliteAuditFailureRollsBackMetadataRevision()
    {
        using var fixture = new Fixture(false);
        fixture.Sql("""
            CREATE TRIGGER reject_maven_audit BEFORE INSERT ON audit_events
            WHEN NEW.action='metadata-update'
            BEGIN SELECT RAISE(ABORT,'synthetic audit failure'); END;
            """);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => fixture.Db.PutMavenMetadata(Entry(), "test", null));
        Assert.Null(fixture.Db.GetMavenMetadata("", MetadataPath));
        fixture.Sql("DROP TRIGGER reject_maven_audit");
        Assert.True(fixture.Db.PutMavenMetadata(Entry(), "test", null));
    }

    [Fact]
    public void SqliteRecoveryFailsClosedOnDamagedMetadataAndMissingReferences() => VerifyRecovery(false);

    [PostgresFeedFact]
    public void PostgresRecoveryFailsClosedOnDamagedMetadataAndMissingReferences() => VerifyRecovery(true);

    private static void VerifyRecovery(bool postgres)
    {
        using var fixture = new Fixture(postgres);
        fixture.SeedPom();
        var metadata = Entry();
        Assert.True(fixture.Db.PutMavenMetadata(metadata, "test", null));
        var recovery = new ArtifactRecovery(fixture.Settings, fixture.Db, new BlobStore(fixture.Settings),
            NullLogger<ArtifactRecovery>.Instance);
        Assert.Equal(0, recovery.MissingHostedCount());
        fixture.Sql("UPDATE maven_metadata SET size=size+1");
        Assert.Equal(1, recovery.MissingHostedCount());
        Assert.Equal(1, recovery.Reconcile().MissingHostedArtifacts);
        fixture.Sql("UPDATE maven_metadata SET size=size-1; DELETE FROM maven_assets;");
        Assert.Equal(1, recovery.MissingHostedCount());
    }
}
