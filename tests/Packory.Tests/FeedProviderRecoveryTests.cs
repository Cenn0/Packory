using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Packory;
using Xunit;

namespace Packory.Tests;

/// <summary>
/// Storage contract and interruption tests. These use synthetic package bytes
/// deliberately; native archive/protocol acceptance is tested by the adapters.
/// PostgreSQL runs only against the explicitly configured disposable service.
/// </summary>
public sealed class FeedProviderRecoveryTests
{
    private const long Maximum = 1024 * 1024;
    private const string Version = "1.0.0";
    private static readonly JsonElement Metadata = JsonSerializer.SerializeToElement(new
    {
        purpose = "synthetic shared-storage recovery fixture"
    });

    private sealed class Fixture : IDisposable
    {
        private readonly NpgsqlConnection? _administrator;
        private readonly string? _schema;
        public Settings Settings { get; }
        public IPackageDatabase Database { get; }
        public BlobStore Blobs { get; }
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "packory-feed-recovery-" + Guid.NewGuid().ToString("N"));

        public Fixture(bool postgres)
        {
            Directory.CreateDirectory(Root);
            Settings = new Settings { DataDirectory = Root, MaxArtifactBytes = Maximum };
            if (postgres)
            {
                var connection = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
                Assert.False(string.IsNullOrWhiteSpace(connection));
                _schema = "packory_feed_recovery_" + Guid.NewGuid().ToString("N")[..14];
                _administrator = new NpgsqlConnection(connection);
                _administrator.Open();
                using var create = new NpgsqlCommand("CREATE SCHEMA " + _schema, _administrator);
                create.ExecuteNonQuery();
                Settings.MetadataProvider = "postgresql";
                Settings.PostgresConnectionString = new NpgsqlConnectionStringBuilder(connection)
                {
                    SearchPath = _schema
                }.ConnectionString;
            }
            Settings.Validate();
            Database = OpenDatabase();
            Database.Initialize();
            Blobs = new BlobStore(Settings);
        }

        public IPackageDatabase OpenDatabase() => Settings.MetadataProvider == "postgresql"
            ? new PostgresDatabase(Settings) : new Database(Settings);

        public ArtifactRecovery Recovery(IPackageDatabase database, BlobStore blobs) =>
            new(Settings, database, blobs, NullLogger<ArtifactRecovery>.Instance);

        public void Feed(string id, string kind)
        {
            var now = DateTimeOffset.UtcNow.ToString("O");
            Assert.True(Database.TryCreateDraftFeed(new FeedDefinition(id, kind, "hosted", "private",
                "Synthetic recovery fixture", false, now, now), "test-admin"));
            Assert.True(Database.TrySetHostedFeedEnabled(id, kind, true, "test-admin"));
        }

        public void SimulateLegacyFeedDeletion(string id)
        {
            // Reproduce a database left by older lifecycle code. Preserve audit,
            // token and journal evidence while removing only the empty feed row.
            if (Settings.MetadataProvider == "postgresql")
            {
                using var connection = new NpgsqlConnection(Settings.PostgresConnectionString);
                connection.Open();
                using var delete = new NpgsqlCommand("DELETE FROM feeds WHERE id=@id", connection);
                delete.Parameters.AddWithValue("id", id);
                Assert.Equal(1, delete.ExecuteNonQuery());
            }
            else
            {
                using var connection = ((Database)Database).Open();
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM feeds WHERE id=$id";
                delete.Parameters.AddWithValue("$id", id);
                Assert.Equal(1, delete.ExecuteNonQuery());
            }
        }

        public void Dispose()
        {
            if (_administrator is not null)
            {
                try
                {
                    using var drop = new NpgsqlCommand("DROP SCHEMA " + _schema + " CASCADE", _administrator);
                    drop.ExecuteNonQuery();
                }
                finally { _administrator.Dispose(); }
            }
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class InterruptedBlobStore(Settings settings) : BlobStore(settings)
    {
        public override async Task<bool> PutAsync(string key, Stream input, long maxBytes, CancellationToken ct)
        {
            var stored = await base.PutAsync(key, input, maxBytes, ct);
            if (stored) throw new IOException("Synthetic interruption after immutable blob persistence.");
            return stored;
        }
    }

    public class InterruptedJournalProxy : DispatchProxy
    {
        public IPackageDatabase Target { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IPackageDatabase.CompletePublication))
                throw new IOException("Synthetic interruption before journal completion.");
            try { return targetMethod.Invoke(Target, args); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
    }

    private static string PathFor(string kind, string name) => kind switch
    {
        "pypi" => FeedPyPiEndpoints.AssetPath(name, name.Replace('-', '_') + "-1.0.0-py3-none-any.whl"),
        "cargo" => FeedCargoEndpoints.AssetPath(name, Version),
        "npm" => name + "/" + Version,
        _ => throw new ArgumentException("Unsupported fixture kind")
    };

    private static Task<bool> Publish(string feed, string kind, string name, byte[] payload,
        IPackageDatabase database, BlobStore blobs) =>
        FeedPackageStore.PublishAsync(feed, kind, PathFor(kind, name), name, Version,
            Metadata, payload, database, blobs, new ArtifactLocks(), Maximum, "test-publisher", CancellationToken.None);

    private static FeedDefinition Draft(IPackageDatabase database, string id, string kind = "cargo")
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var draft = new FeedDefinition(id, kind, "hosted", "private", "Synthetic lifecycle fixture", false, now, now);
        Assert.True(database.TryCreateDraftFeed(draft, "test-admin"));
        return draft;
    }

    [Fact]
    public void SqliteActivatedFeedIdentityCannotBeReassignedAfterDisable() => VerifyActivatedIdentity(false);

    [PostgresFeedFact]
    public void PostgresActivatedFeedIdentityCannotBeReassignedAfterDisable() => VerifyActivatedIdentity(true);

    private static void VerifyActivatedIdentity(bool postgres)
    {
        using var fixture = new Fixture(postgres);
        var kinds = new[] { "nuget", "npm", "cargo", "pypi", "oci", "raw", "maven", "go" };
        foreach (var kind in kinds)
        {
            var id = "retained-" + kind;
            fixture.Feed(id, kind);
            Assert.True(fixture.Database.TrySetHostedFeedEnabled(id, kind, false, "test-admin"));
        }
        // No packages or managed tokens are required: external OIDC grants can
        // still name any formerly active feed, even when it stayed empty.
        Assert.Empty(fixture.Database.FeedAssets());
        Assert.Empty(fixture.Database.FeedNugetRevisions());
        var reopened = fixture.OpenDatabase();
        reopened.Initialize();
        foreach (var kind in kinds)
        {
            var id = "retained-" + kind;
            var original = reopened.Feed(id);
            Assert.NotNull(original);
            Assert.False(reopened.TryDeleteDraftFeed(id, "test-admin"));
            Assert.Equal(original, reopened.Feed(id));
            var replacement = original with { Kind = kind == "raw" ? "cargo" : "raw", DisplayName = "New identity" };
            Assert.False(reopened.TryCreateDraftFeed(replacement, "test-admin"));
            Assert.True(reopened.TrySetHostedFeedEnabled(id, kind, true, "test-admin"));
            Assert.Equal(kind, reopened.Feed(id)!.Kind);

            Assert.True(reopened.TrySetHostedFeedEnabled(id, kind, false, "test-admin"));
            fixture.SimulateLegacyFeedDeletion(id);
            var historical = fixture.OpenDatabase();
            historical.Initialize();
            Assert.Null(historical.Feed(id));
            Assert.Contains(historical.AuditEvents(null, 100), entry =>
                entry.Action == "feed-hosted-activate" && entry.Kind == "feed" && entry.Name == id);
            Assert.False(historical.TryCreateDraftFeed(replacement, "test-admin"));
            Assert.Null(historical.Feed(id));
        }
        // Activation history for retained-* does not reserve the shorter ID.
        var prefix = Draft(reopened, "retained");
        Assert.True(reopened.TryDeleteDraftFeed(prefix.Id, "test-admin"));
        var unused = Draft(reopened, "never-activated");
        Assert.True(reopened.TryDeleteDraftFeed(unused.Id, "test-admin"));
        Assert.Null(reopened.Feed(unused.Id));
        Assert.True(reopened.TryCreateDraftFeed(unused with { Kind = "raw" }, "test-admin"));
        Assert.Equal("raw", reopened.Feed(unused.Id)!.Kind);
    }

    [Fact]
    public void SqliteDraftDeletionRetainsExactManagedFeedGrantReferences() => VerifyGrantReferences(false);

    [PostgresFeedFact]
    public void PostgresDraftDeletionRetainsExactManagedFeedGrantReferences() => VerifyGrantReferences(true);

    private static void VerifyGrantReferences(bool postgres)
    {
        using var fixture = new Fixture(postgres);
        foreach (var (permission, revoke) in new[]
        {
            ("read", false), ("publish", false), ("read", true), ("publish", true)
        })
        {
            var id = "grant-" + permission + (revoke ? "-revoked" : "");
            var draft = Draft(fixture.Database, id);
            // Model imported/legacy references without an activation audit.
            var tokenId = Guid.NewGuid().ToString("N");
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(tokenId)));
            fixture.Database.CreateManagedToken(new ManagedTokenRecord(tokenId, hash,
                permission == "read" ? "read" : "write", "feed:" + id + ":" + permission,
                DateTimeOffset.UtcNow.AddHours(1).ToString("O")));
            var reopened = fixture.OpenDatabase();
            Assert.False(reopened.TryDeleteDraftFeed(id, "test-admin"));
            Assert.NotNull(reopened.Feed(id));
            Assert.NotNull(reopened.FindManagedToken(hash));
            if (revoke)
            {
                Assert.True(reopened.RevokeManagedToken(tokenId));
                Assert.False(reopened.TryDeleteDraftFeed(id, "test-admin"));
            }
            fixture.SimulateLegacyFeedDeletion(id);
            var historical = fixture.OpenDatabase();
            Assert.Null(historical.Feed(id));
            Assert.False(historical.TryCreateDraftFeed(draft with { Kind = "raw" }, "test-admin"));
            Assert.Null(historical.Feed(id));
        }
        // A grant for grant-read or grant-publish does not refer to grant.
        Draft(fixture.Database, "grant");
        Assert.True(fixture.Database.TryDeleteDraftFeed("grant", "test-admin"));
        Draft(fixture.Database, "grant");
    }

    [Fact]
    public void SqliteDraftDeletionRetainsExactPublicationIntentReferences() => VerifyIntentReferences(false);

    [PostgresFeedFact]
    public void PostgresDraftDeletionRetainsExactPublicationIntentReferences() => VerifyIntentReferences(true);

    private static void VerifyIntentReferences(bool postgres)
    {
        using var fixture = new Fixture(postgres);
        foreach (var kind in new[] { "nuget", "npm", "cargo", "pypi", "oci", "raw", "maven", "go" })
        {
            var id = "pending-" + kind;
            var draft = Draft(fixture.Database, id, kind);
            var key = "feeds/" + id + "/" + kind + "/uncommitted.bundle";
            // Only an imported journal reference exists; never invent a serving
            // row or stored package to make this defensive lifecycle case pass.
            fixture.Database.BeginPublication(key, "feed-" + kind, id + "|uncommitted", Version);
            var reopened = fixture.OpenDatabase();
            var pending = Assert.Single(reopened.PendingPublications());
            Assert.False(reopened.TryDeleteDraftFeed(id, "test-admin"));
            Assert.Equal(pending, Assert.Single(reopened.PendingPublications()));
            Assert.NotNull(reopened.Feed(id));
            fixture.SimulateLegacyFeedDeletion(id);
            var historical = fixture.OpenDatabase();
            Assert.Null(historical.Feed(id));
            Assert.False(historical.TryCreateDraftFeed(draft, "test-admin"));
            Assert.Null(historical.Feed(id));
            Assert.Equal(pending, Assert.Single(historical.PendingPublications()));
            historical.CompletePublication(key);
            Assert.True(historical.TryCreateDraftFeed(draft, "test-admin"));
            Assert.True(historical.TryDeleteDraftFeed(id, "test-admin"));
        }
        Draft(fixture.Database, "pending");
        Draft(fixture.Database, "pending-other");
        fixture.Database.BeginPublication("feeds/pending-other/cargo/uncommitted.bundle",
            "feed-cargo", "pending-other|uncommitted", Version);
        Assert.True(fixture.Database.TryDeleteDraftFeed("pending", "test-admin"));
        Assert.False(fixture.Database.TryDeleteDraftFeed("pending-other", "test-admin"));
        Draft(fixture.Database, "pending");
    }

    [Fact]
    public Task SqliteFeedBundlesSurviveReopenWithDistinctSameCoordinateBytes() => VerifyReopen(false);

    [PostgresFeedFact]
    public Task PostgresFeedBundlesSurviveReopenWithDistinctSameCoordinateBytes() => VerifyReopen(true);

    private static async Task VerifyReopen(bool postgres)
    {
        using var fixture = new Fixture(postgres);
        foreach (var kind in new[] { "npm", "cargo", "pypi" })
        {
            fixture.Feed(kind + "-alpha", kind);
            fixture.Feed(kind + "-beta", kind);
            Assert.True(await Publish(kind + "-alpha", kind, "shared-fixture", "alpha"u8.ToArray(), fixture.Database, fixture.Blobs));
            Assert.True(await Publish(kind + "-beta", kind, "shared-fixture", "beta"u8.ToArray(), fixture.Database, fixture.Blobs));
            Assert.False(await Publish(kind + "-alpha", kind, "shared-fixture", "different"u8.ToArray(), fixture.Database, fixture.Blobs));
        }
        Assert.Equal(6, fixture.Database.FeedAssets().Count);
        Assert.Empty(fixture.Database.PendingPublications());
        Assert.Empty(fixture.Database.StoredArtifacts());

        // Recreate both provider objects rather than reusing process-local state.
        var reopened = fixture.OpenDatabase();
        reopened.Initialize();
        var blobs = new BlobStore(fixture.Settings);
        Assert.Equal(0, fixture.Recovery(reopened, blobs).Reconcile().MissingHostedArtifacts);
        foreach (var kind in new[] { "npm", "cargo", "pypi" })
        {
            foreach (var label in new[] { "alpha", "beta" })
            {
                var feed = kind + "-" + label;
                var result = await FeedPackageStore.ReadAsync(feed, kind, PathFor(kind, "shared-fixture"),
                    reopened, blobs, Maximum, CancellationToken.None);
                Assert.NotNull(result);
                Assert.Equal(System.Text.Encoding.UTF8.GetBytes(label), result.Payload);
                Assert.Equal(Metadata.GetRawText(), result.Metadata.GetRawText());
                var entry = Assert.Single(reopened.FeedPackageAssets(feed, kind, "shared-fixture"));
                Assert.True(FeedAssetEntry.Valid(entry));
                Assert.StartsWith("feeds/" + feed + "/", FeedAssetEntry.BlobKey(entry));
            }
        }
    }

    [Fact]
    public Task SqliteInterruptedFeedWritesRetainEvidenceAndNeverInventVisibility() => VerifyInterruptions(false);

    [PostgresFeedFact]
    public Task PostgresInterruptedFeedWritesRetainEvidenceAndNeverInventVisibility() => VerifyInterruptions(true);

    private static async Task VerifyInterruptions(bool postgres)
    {
        using var fixture = new Fixture(postgres);
        const string feed = "cargo-interrupted";
        fixture.Feed(feed, "cargo");
        var interrupted = new InterruptedBlobStore(fixture.Settings);
        await Assert.ThrowsAsync<IOException>(() => Publish(feed, "cargo", "before-ledger",
            "not-visible"u8.ToArray(), fixture.Database, interrupted));

        var reopened = fixture.OpenDatabase();
        var blobs = new BlobStore(fixture.Settings);
        var recovery = fixture.Recovery(reopened, blobs);
        var pending = Assert.Single(reopened.PendingPublications());
        var original = await File.ReadAllBytesAsync(blobs.Existing(pending.Key)!);
        Assert.Equal("uncommitted-object", recovery.State(pending));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            // An existing intent protects its bytes; auto-quarantine would erase
            // the only recovery link when a later run sees an absent source.
            Assert.Equal(0, recovery.Reconcile().QuarantinedOrphans);
            Assert.Equal(pending, Assert.Single(reopened.PendingPublications()));
            Assert.Equal(original, await File.ReadAllBytesAsync(blobs.Existing(pending.Key)!));
            Assert.Null(await FeedPackageStore.ReadAsync(feed, "cargo", PathFor("cargo", "before-ledger"),
                reopened, blobs, Maximum, CancellationToken.None));
        }

        var proxy = DispatchProxy.Create<IPackageDatabase, InterruptedJournalProxy>();
        ((InterruptedJournalProxy)(object)proxy).Target = reopened;
        await Assert.ThrowsAsync<IOException>(() => Publish(feed, "cargo", "after-ledger",
            "visible-and-verified"u8.ToArray(), proxy, blobs));
        var committed = Assert.Single(reopened.PendingPublications(), p => p.Key != pending.Key);
        Assert.Equal("committed", recovery.State(committed));
        var served = await FeedPackageStore.ReadAsync(feed, "cargo", PathFor("cargo", "after-ledger"),
            reopened, blobs, Maximum, CancellationToken.None);
        Assert.NotNull(served);
        Assert.Equal("visible-and-verified"u8.ToArray(), served.Payload);
        recovery.Reconcile();
        Assert.DoesNotContain(reopened.PendingPublications(), p => p.Key == committed.Key);
        Assert.Equal(pending, Assert.Single(reopened.PendingPublications()));

        // Missing committed bytes keep their intent and fail closed on read.
        reopened.BeginPublication(committed.Key, committed.Kind, committed.Name, committed.Version);
        File.Delete(blobs.Existing(committed.Key)!);
        Assert.Equal("missing-hosted-blob", recovery.State(committed));
        Assert.Equal(1, recovery.Reconcile().MissingHostedArtifacts);
        Assert.Contains(reopened.PendingPublications(), p => p.Key == committed.Key);
        await Assert.ThrowsAsync<InvalidDataException>(() => FeedPackageStore.ReadAsync(feed, "cargo",
            PathFor("cargo", "after-ledger"), reopened, blobs, Maximum, CancellationToken.None));
    }

    [Fact]
    public Task SqliteUntrackedFeedBundleIsQuarantinedWithoutCatalogVisibility() => VerifyUntrackedOrphan(false);

    [PostgresFeedFact]
    public Task PostgresUntrackedFeedBundleIsQuarantinedWithoutCatalogVisibility() => VerifyUntrackedOrphan(true);

    private static async Task VerifyUntrackedOrphan(bool postgres)
    {
        using var fixture = new Fixture(postgres);
        const string feed = "pypi-orphan";
        fixture.Feed(feed, "pypi");
        Assert.True(await Publish(feed, "pypi", "committed-fixture", "synthetic"u8.ToArray(), fixture.Database, fixture.Blobs));
        var committed = Assert.Single(fixture.Database.FeedAssets());
        var source = await File.ReadAllBytesAsync(fixture.Blobs.Existing(FeedAssetEntry.BlobKey(committed))!);
        var orphan = committed with { Name = "orphan-fixture", Path = PathFor("pypi", "orphan-fixture") };
        var orphanKey = FeedAssetEntry.BlobKey(orphan);
        Assert.True(await fixture.Blobs.PutAsync(orphanKey, new MemoryStream(source), Maximum, CancellationToken.None));
        Assert.Empty(fixture.Database.PendingPublications());
        var report = fixture.Recovery(fixture.OpenDatabase(), new BlobStore(fixture.Settings)).Reconcile();
        Assert.Equal(1, report.QuarantinedOrphans);
        Assert.Equal(0, report.MissingHostedArtifacts);
        Assert.False(fixture.Blobs.Contains(orphanKey));
        Assert.Single(fixture.Database.FeedAssets());
        Assert.Null(await FeedPackageStore.ReadAsync(feed, "pypi", orphan.Path,
            fixture.Database, fixture.Blobs, Maximum, CancellationToken.None));
        var preserved = Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Root, "recovery", "orphaned"), "*", SearchOption.AllDirectories));
        Assert.Equal(source, await File.ReadAllBytesAsync(preserved));
    }

    [PostgresS3FeedFact]
    public async Task PostgresS3FeedBundlesMaterializeColdAndRejectRemoteTamperingWithWarmCache()
    {
        using var fixture = new Fixture(postgres: true);
        var cfg = fixture.Settings;
        cfg.BlobProvider = "s3";
        cfg.S3Endpoint = Environment.GetEnvironmentVariable("PACKORY_TEST_S3");
        cfg.S3Bucket = "packory-feed-recovery-" + Guid.NewGuid().ToString("N");
        cfg.S3Region = "us-east-1";
        cfg.S3AccessKey = "test";
        cfg.S3SecretKey = "test";
        cfg.S3KeyPrefix = "synthetic-private-bundles";
        cfg.S3AllowLoopbackHttp = true;
        // A larger general metadata limit would hide a cold-cache bug that
        // allowed only MaxArtifactBytes and rejected its valid ZIP envelope.
        cfg.MaxMetadataBytes = 1024;
        cfg.Validate();

        using var client = new AmazonS3Client(new BasicAWSCredentials("test", "test"),
            new AmazonS3Config
            {
                ServiceURL = cfg.S3Endpoint, AuthenticationRegion = cfg.S3Region,
                ForcePathStyle = true
            });
        const string name = "shared-fixture";
        var kinds = new[] { "npm", "cargo", "pypi" };
        var payloads = new Dictionary<string, byte[]>
        {
            ["alpha"] = Enumerable.Range(0, (int)Maximum).Select(i => (byte)(i % 251)).ToArray(),
            ["beta"] = Enumerable.Range(0, (int)Maximum).Select(i => (byte)((i + 1) % 251)).ToArray()
        };
        var keys = kinds.SelectMany(kind => payloads.Keys.Select(label =>
            cfg.S3KeyPrefix + "/" + FeedPackageStore.BlobKey(kind + "-" + label, kind, PathFor(kind, name))))
            .ToArray();

        await client.PutBucketAsync(new PutBucketRequest { BucketName = cfg.S3Bucket });
        try
        {
            using (var writer = new S3BlobStore(cfg))
            {
                foreach (var kind in kinds)
                {
                    foreach (var (label, payload) in payloads)
                    {
                        var feed = kind + "-" + label;
                        fixture.Feed(feed, kind);
                        Assert.True(await Publish(feed, kind, name, payload, fixture.Database, writer));
                        Assert.False(await Publish(feed, kind, name, "changed"u8.ToArray(), fixture.Database, writer));
                        var entry = Assert.Single(fixture.Database.FeedPackageAssets(feed, kind, name));
                        Assert.True(entry.Size > Maximum); // Full-size payload plus immutable envelope.
                        Assert.True(FeedAssetEntry.Valid(entry));
                    }
                }
            }

            Assert.Equal(6, fixture.Database.FeedAssets().Count);
            Assert.Empty(fixture.Database.StoredArtifacts());
            Assert.Empty(fixture.Database.PendingPublications());
            var objects = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = cfg.S3Bucket });
            Assert.Equal(keys.Order(StringComparer.Ordinal), objects.S3Objects.Select(o => o.Key).Order(StringComparer.Ordinal));

            var cacheDirectory = Path.Combine(fixture.Root, "blobs");
            Directory.Delete(cacheDirectory, recursive: true);
            var reopened = fixture.OpenDatabase();
            reopened.Initialize();
            using var reader = new S3BlobStore(cfg);
            Assert.Empty(Directory.EnumerateFiles(cacheDirectory, "*", SearchOption.AllDirectories));
            foreach (var kind in kinds)
            {
                foreach (var (label, payload) in payloads)
                {
                    var result = await FeedPackageStore.ReadAsync(kind + "-" + label, kind, PathFor(kind, name),
                        reopened, reader, Maximum, CancellationToken.None);
                    Assert.NotNull(result);
                    Assert.Equal(payload, result.Payload);
                    Assert.Equal(Metadata.GetRawText(), result.Metadata.GetRawText());
                }
                // Existing bytes in two other feeds never synthesize an alias
                // in a missing feed or the legacy global package catalog.
                Assert.Null(await FeedPackageStore.ReadAsync("absent-feed", kind, PathFor(kind, name),
                    reopened, reader, Maximum, CancellationToken.None));
            }
            Assert.Empty(reopened.StoredArtifacts());
            Assert.Equal(6, Directory.EnumerateFiles(cacheDirectory, "*", SearchOption.AllDirectories).Count());
            Assert.Equal(0, fixture.Recovery(reopened, reader).Reconcile().MissingHostedArtifacts);

            foreach (var kind in kinds)
            {
                var entry = Assert.Single(reopened.FeedPackageAssets(kind + "-alpha", kind, name));
                var key = FeedAssetEntry.BlobKey(entry);
                var cachePath = reader.Existing(key)!;
                var original = await File.ReadAllBytesAsync(cachePath);
                var tampered = original.ToArray();
                tampered[tampered.Length / 2] ^= 0x80; // Same length; a HEAD/size check cannot detect this.
                using var payload = new MemoryStream(tampered, writable: false);
                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = cfg.S3Bucket, Key = cfg.S3KeyPrefix + "/" + key,
                    InputStream = payload, AutoCloseStream = false
                });

                Assert.Equal(original, await File.ReadAllBytesAsync(cachePath));
                await Assert.ThrowsAsync<InvalidDataException>(() => FeedPackageStore.ReadAsync(
                    kind + "-alpha", kind, PathFor(kind, name), reopened, reader, Maximum, CancellationToken.None));
                Assert.Equal(entry, Assert.Single(reopened.FeedPackageAssets(kind + "-alpha", kind, name)));
                var otherFeed = await FeedPackageStore.ReadAsync(kind + "-beta", kind, PathFor(kind, name),
                    reopened, reader, Maximum, CancellationToken.None);
                Assert.NotNull(otherFeed);
                Assert.Equal(payloads["beta"], otherFeed.Payload);
            }
        }
        finally
        {
            // This schema and bucket belong only to this test invocation.
            foreach (var key in keys)
                await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = cfg.S3Bucket, Key = key });
            await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = cfg.S3Bucket });
        }
    }
}

internal sealed class PostgresFeedFactAttribute : FactAttribute
{
    public PostgresFeedFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES")))
            Skip = "Requires the explicit disposable PACKORY_TEST_POSTGRES service; PostgreSQL was not tested in this run.";
    }
}

internal sealed class PostgresS3FeedFactAttribute : FactAttribute
{
    public PostgresS3FeedFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PACKORY_TEST_S3")))
            Skip = "Requires the explicit disposable PACKORY_TEST_POSTGRES and PACKORY_TEST_S3 services; PostgreSQL/S3 was not tested in this run.";
    }
}
