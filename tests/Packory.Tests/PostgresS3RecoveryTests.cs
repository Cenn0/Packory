using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Packory;
using Xunit;
using Npgsql;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Packory.Tests;

/// <summary>Test real PostgreSQL transactions with Moto S3 conditional writes/deletes.</summary>
public sealed class PostgresS3RecoveryTests
{
    private static Settings? Configure(string directory, string prefix)
    {
        var pg = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        var s3 = Environment.GetEnvironmentVariable("PACKORY_TEST_S3");
        if (string.IsNullOrWhiteSpace(pg) || string.IsNullOrWhiteSpace(s3)) return null;
        var settings = new Settings
        {
            MetadataProvider = "postgresql", BlobProvider = "s3",
            PostgresConnectionString = pg, S3Endpoint = s3,
            S3Bucket = "packory-recovery-tests", S3Region = "us-east-1",
            S3AccessKey = "test", S3SecretKey = "test",
            S3KeyPrefix = prefix, S3AllowLoopbackHttp = true,
            DataDirectory = directory, RecoveryMaintenance = true
        };
        settings.Validate();
        return settings;
    }

    [Fact]
    public async Task PostgreSqlS3OrphanIsQuarantinedAndReversible()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packory-reconcile-" + Guid.NewGuid().ToString("N"));
        var cfg = Configure(dir, "reconcile-" + Guid.NewGuid().ToString("N"));
        if (cfg is null) return;
        try
        {
            using (var client = new AmazonS3Client(new BasicAWSCredentials("test", "test"),
                new AmazonS3Config { ServiceURL = cfg.S3Endpoint,
                    AuthenticationRegion = "us-east-1", ForcePathStyle = true }))
                await client.PutBucketAsync(new PutBucketRequest { BucketName = cfg.S3Bucket });

            var db = new PostgresDatabase(cfg);
            db.Initialize();
            using var s3 = new S3BlobStore(cfg);
            var recovery = new ArtifactRecovery(cfg, db, s3, NullLogger<ArtifactRecovery>.Instance);
            var manager = new PublicationRecoveryService(cfg, db, s3, recovery);
            var name = "orphan." + Guid.NewGuid().ToString("N")[..10];
            var key = NuGetEndpoints.NugetKey(name, "1.0.0");
            var bytes = Encoding.UTF8.GetBytes("immutable orphan, preserve bytes");
            db.BeginPublication(key, "nuget", name, "1.0.0");
            Assert.True(await s3.PutAsync(key, new MemoryStream(bytes), 2048, CancellationToken.None));
            var inspected = Assert.Single(manager.Inspect(), x => x.Key == key);
            Assert.Equal("uncommitted-object", inspected.State);
            var resolved = await manager.ResolveAsync(key, inspected.StartedAt, CancellationToken.None);
            Assert.Equal("quarantined-recoverable", resolved.Outcome);
            Assert.False(s3.Contains(key));
            var evidence = db.FindQuarantine(key);
            Assert.NotNull(evidence);
            Assert.True(evidence.Completed);
            await s3.VerifyArchivedAsync(evidence, CancellationToken.None);
            Assert.DoesNotContain(db.PendingPublications(), p => p.Key == key);
            Assert.True(await s3.PutAsync(key, new MemoryStream(bytes), 2048, CancellationToken.None));
            Assert.True(db.PublishNuget(name, "1.0.0", "new metadata", "writer"));
            Assert.Equal("hosted", db.PackageOrigin("nuget", name, "1.0.0"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task PreparedEvidenceSurvivesCrashAfterConditionalDelete()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packory-reconcile-" + Guid.NewGuid().ToString("N"));
        var cfg = Configure(dir, "reconcile-" + Guid.NewGuid().ToString("N"));
        if (cfg is null) return;
        try
        {
            using (var client = new AmazonS3Client(new BasicAWSCredentials("test", "test"),
                new AmazonS3Config { ServiceURL = cfg.S3Endpoint,
                    AuthenticationRegion = "us-east-1", ForcePathStyle = true }))
                await client.PutBucketAsync(new PutBucketRequest { BucketName = cfg.S3Bucket });
            var db = new PostgresDatabase(cfg);
            db.Initialize();
            using var s3 = new S3BlobStore(cfg);
            var name = "crash." + Guid.NewGuid().ToString("N")[..10];
            var key = NuGetEndpoints.NugetKey(name, "1.0.0");
            db.BeginPublication(key, "nuget", name, "1.0.0");
            Assert.True(await s3.PutAsync(key, new MemoryStream(new byte[] { 1, 2, 3 }), 2048, CancellationToken.None));
            var archive = await s3.ArchiveOrphanAsync(key, CancellationToken.None);
            db.PrepareQuarantine(archive);
            await s3.DeleteOriginalConditionallyAsync(archive, CancellationToken.None);
            var recovery = new ArtifactRecovery(cfg, db, s3, NullLogger<ArtifactRecovery>.Instance);
            recovery.Reconcile();
            Assert.Contains(db.PendingPublications(), p => p.Key == key);
            var result = await new PublicationRecoveryService(cfg, db, s3, recovery)
                .ResolveAsync(key, db.PendingPublications().Single(p => p.Key == key).StartedAt, CancellationToken.None);
            Assert.Equal("quarantined-recoverable", result.Outcome);
            Assert.True(db.FindQuarantine(key)?.Completed);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private sealed class ReplicaHost(Settings cfg) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                services.AddSingleton(cfg);
            });
        }
    }

    [Fact]
    public async Task OneWriterAndReadOnlyReplicaSharePostgreSqlAndS3WithoutSecondWriter()
    {
        var baseConnection = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        var endpoint = Environment.GetEnvironmentVariable("PACKORY_TEST_S3");
        if (string.IsNullOrWhiteSpace(baseConnection) || string.IsNullOrWhiteSpace(endpoint)) return;

        // Isolated logical database ensures the test never interferes with other package fixtures.
        var databaseName = "packory_replica_" + Guid.NewGuid().ToString("N")[..14];
        using (var connection = new NpgsqlConnection(baseConnection))
        {
            connection.Open();
            using var command = new NpgsqlCommand("CREATE DATABASE " + databaseName, connection);
            command.ExecuteNonQuery();
        }
        var connectionBuilder = new NpgsqlConnectionStringBuilder(baseConnection) { Database = databaseName };
        var root = Path.Combine(Path.GetTempPath(), "packory-reader-" + Guid.NewGuid().ToString("N"));
        try
        {
            var writerCfg = new Settings
            {
                InstanceRole = "writer", MetadataProvider = "postgresql", BlobProvider = "s3",
                PostgresConnectionString = connectionBuilder.ConnectionString,
                S3Endpoint = endpoint, S3Bucket = "packory-recovery-tests",
                S3Region = "us-east-1", S3AccessKey = "test", S3SecretKey = "test",
                S3KeyPrefix = "replica-" + Guid.NewGuid().ToString("N")[..12],
                S3AllowLoopbackHttp = true, DataDirectory = Path.Combine(root, "writer")
            };
            writerCfg.Validate();
            var readerCfg = new Settings
            {
                InstanceRole = "reader", MetadataProvider = "postgresql", BlobProvider = "s3",
                PostgresConnectionString = writerCfg.PostgresConnectionString,
                S3Endpoint = writerCfg.S3Endpoint, S3Bucket = writerCfg.S3Bucket,
                S3Region = writerCfg.S3Region, S3AccessKey = "test", S3SecretKey = "test",
                S3KeyPrefix = writerCfg.S3KeyPrefix,
                S3AllowLoopbackHttp = true, DataDirectory = Path.Combine(root, "reader")
            };
            readerCfg.Validate();
            using var writer = new ReplicaHost(writerCfg);
            using var writerClient = writer.CreateClient();
            Assert.Equal(System.Net.HttpStatusCode.OK,
                (await writerClient.GetAsync("/health/ready")).StatusCode);
            using var reader = new ReplicaHost(readerCfg);
            using var readerClient = reader.CreateClient();
            Assert.Equal(System.Net.HttpStatusCode.OK,
                (await readerClient.GetAsync("/health/ready")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.OK,
                (await readerClient.GetAsync("/api/packages")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.OK,
                (await readerClient.GetAsync("/cargo/index/config.json")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable,
                (await readerClient.GetAsync("/nuget/v3/index.json")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable,
                (await readerClient.GetAsync("/npm/example")).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable,
                (await readerClient.PutAsync("/cargo/api/v1/crates/new",
                    new ByteArrayContent(new byte[] { 0, 1, 2 }))).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.OK,
                (await readerClient.GetAsync("/v2/")).StatusCode);

            using var secondWriter = new ReplicaHost(new Settings
            {
                InstanceRole = "writer", MetadataProvider = "postgresql", BlobProvider = "s3",
                PostgresConnectionString = writerCfg.PostgresConnectionString,
                S3Endpoint = writerCfg.S3Endpoint, S3Bucket = writerCfg.S3Bucket,
                S3Region = writerCfg.S3Region, S3AccessKey = "test", S3SecretKey = "test",
                S3KeyPrefix = writerCfg.S3KeyPrefix,
                S3AllowLoopbackHttp = true, DataDirectory = Path.Combine(root, "writer2")
            });
            Assert.ThrowsAny<Exception>(() => secondWriter.CreateClient());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ConcurrentLeaseDisposalIsIdempotentAndCannotDoubleDisposeConnection()
    {
        var pg = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(pg)) return;
        var cfg = new Settings { MetadataProvider = "postgresql", PostgresConnectionString = pg };
        var lease = new PostgresSingleWriterLease(cfg);
        lease.Acquire();
        var failures = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 64, _ =>
        {
            try { lease.Dispose(); }
            catch (Exception error) { failures.Add(error); }
        });
        Assert.Empty(failures);
        Assert.False(lease.IsHeld());
        lease.Dispose();
    }

    [Fact]
    public void PostgreSqlExclusiveWriterLockPreventsSecondInstance()
    {
        var dir = Path.Combine(Path.GetTempPath(), "packory-lease-" + Guid.NewGuid().ToString("N"));
        var cfg = Configure(dir, "lease-" + Guid.NewGuid().ToString("N"));
        if (cfg is null) return;
        using var first = new PostgresSingleWriterLease(cfg);
        using var second = new PostgresSingleWriterLease(cfg);
        first.Acquire();
        Assert.True(first.IsHeld());
        Assert.Throws<InvalidOperationException>(() => second.Acquire());
        first.Dispose();
        second.Acquire();
        Assert.True(second.IsHeld());
    }
    [Fact]
    public void ConnectedBackendWithoutAdvisoryLockMustNotPassWriterLeaseCheck()
    {
        var pg = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(pg)) return;
        var cfg = new Settings { MetadataProvider = "postgresql", PostgresConnectionString = pg };
        using var first = new PostgresSingleWriterLease(cfg);
        first.Acquire();
        Assert.True(first.IsHeld());

        // Emulate an operator-side/session lock release WITHOUT disconnecting
        // PostgreSQL. The old SELECT 1 check incorrectly reported success here.
        var field = typeof(PostgresSingleWriterLease).GetField("_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var connection = Assert.IsType<Npgsql.NpgsqlConnection>(field!.GetValue(first));
        using (var unlock = new Npgsql.NpgsqlCommand("SELECT pg_advisory_unlock_all()", connection))
            unlock.ExecuteScalar();
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.False(first.IsHeld());

        using var second = new PostgresSingleWriterLease(cfg);
        second.Acquire();
        Assert.True(second.IsHeld());
        Assert.False(first.IsHeld());
    }

    [Fact]
    public async Task RecoveryMaintenanceLeavesUnwrittenIntentAndQueuedSkillUntouched()
    {
        // CI uses disposable PostgreSQL and S3 emulation. The app must not run
        // reconciliation or background scanning while an operator inspects the
        // existing database under the exclusive maintenance lease.
        var baseConnection = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        var endpoint = Environment.GetEnvironmentVariable("PACKORY_TEST_S3");
        if (string.IsNullOrWhiteSpace(baseConnection) || string.IsNullOrWhiteSpace(endpoint)) return;
        var schema = "packory_maintenance_" + Guid.NewGuid().ToString("N")[..12];
        var directory = Path.Combine(Path.GetTempPath(), "packory-maintenance-" + Guid.NewGuid().ToString("N"));
        using var administrator = new NpgsqlConnection(baseConnection);
        administrator.Open();
        using (var cmd = new NpgsqlCommand("CREATE SCHEMA " + schema, administrator))
            cmd.ExecuteNonQuery();
        try
        {
            var isolated = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema };
            var cfg = new Settings
            {
                MetadataProvider = "postgresql", BlobProvider = "s3",
                PostgresConnectionString = isolated.ConnectionString,
                S3Endpoint = endpoint, S3Bucket = "packory-recovery-tests",
                S3Region = "us-east-1", S3KeyPrefix = "maintenance-" + Guid.NewGuid().ToString("N")[..12],
                S3AccessKey = "test", S3SecretKey = "test",
                S3AllowLoopbackHttp = true, RecoveryMaintenance = true,
                DataDirectory = directory, AdminToken = new string('x', 64)
            };
            cfg.Validate();
            var db = new PostgresDatabase(cfg);
            db.Initialize(); // Explicit setup; app startup must never initialize the schema.
            var security = new SkillSecurityStore(cfg);
            security.Initialize();
            var package = "unwritten." + Guid.NewGuid().ToString("N")[..10];
            var key = NuGetEndpoints.NugetKey(package, "1.0.0");
            db.BeginPublication(key, "nuget", package, "1.0.0");
            Assert.True(security.TryStage("queued-test", "1.0.0", "synthetic fixture",
                new string('a', 64), "skill-staging/test/1.0.0.zip"));

            using (var host = new ReplicaHost(cfg))
            using (var client = host.CreateClient())
            {
                Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync("/health/ready")).StatusCode);
                Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable,
                    (await client.GetAsync("/api/packages")).StatusCode);
                await Task.Delay(250);
                Assert.Contains(db.PendingPublications(), entry => entry.Key == key);
                Assert.Equal("QUEUED", security.Get("queued-test", "1.0.0")?.State);
                Assert.False(Directory.Exists(Path.Combine(directory, "skill-scans")));
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/publications/pending");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", cfg.AdminToken);
                using var response = await client.SendAsync(request);
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

                using var unauthenticatedScan = await client.GetAsync("/api/admin/integrity/scan");
                Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthenticatedScan.StatusCode);
                using var scanRequest = new HttpRequestMessage(HttpMethod.Get,
                    "/api/admin/integrity/scan?after=0&limit=3");
                scanRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", cfg.AdminToken);
                using var scanResponse = await client.SendAsync(scanRequest);
                Assert.Equal(System.Net.HttpStatusCode.OK, scanResponse.StatusCode);
                Assert.Equal("no-store", scanResponse.Headers.CacheControl?.ToString());
                using var scan = System.Text.Json.JsonDocument.Parse(
                    await scanResponse.Content.ReadAsStringAsync());
                Assert.Equal(0, scan.RootElement.GetProperty("checked").GetInt32());
                Assert.Equal(1, scan.RootElement.GetProperty("pendingPublicationIntents").GetInt32());
                Assert.Equal(0, scan.RootElement.GetProperty("totalReferences").GetInt32());
            }
            Assert.Contains(db.PendingPublications(), entry => entry.Key == key);
        }
        finally
        {
            using (var cmd = new NpgsqlCommand("DROP SCHEMA " + schema + " CASCADE", administrator))
                cmd.ExecuteNonQuery();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PrivateFeedNugetPublicationIntentPersistsAndRecoversUsingPostgresAndS3()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-feed-journal-" +
            Guid.NewGuid().ToString("N"));
        var cfg = Configure(root, "feed-journal-" + Guid.NewGuid().ToString("N")[..12]);
        if (cfg is null) return;
        try
        {
            using (var s3Client = new AmazonS3Client(new BasicAWSCredentials("test", "test"),
                new AmazonS3Config
                {
                    ServiceURL = cfg.S3Endpoint,
                    AuthenticationRegion = "us-east-1",
                    ForcePathStyle = true
                }))
                await s3Client.PutBucketAsync(new PutBucketRequest { BucketName = cfg.S3Bucket });

            var db = new PostgresDatabase(cfg);
            db.Initialize();
            var created = DateTimeOffset.UtcNow.ToString("O");
            var feedId = "feed-journal-" + Guid.NewGuid().ToString("N")[..12];
            Assert.True(db.TryCreateDraftFeed(new FeedDefinition(
                feedId, "nuget", "hosted", "private", "Synthetic",
                false, created, created), "integration-admin"));
            Assert.True(db.TryActivateHostedNugetFeed(feedId, "integration-admin"));
            var name = "feed." + Guid.NewGuid().ToString("N")[..10];
            const string version = "1.0.0";
            var key = FeedNugetEndpoints.BlobKey(feedId, name, version);
            var bytes = Encoding.UTF8.GetBytes("synthetic private NuGet revision");
            var sha = Convert.ToBase64String(
                System.Security.Cryptography.SHA512.HashData(bytes));
            using var storage = new S3BlobStore(cfg);
            var recovery = new ArtifactRecovery(cfg, db, storage,
                NullLogger<ArtifactRecovery>.Instance);
            db.BeginPublication(key, "feed-nuget", feedId + "|" + name, version);
            Assert.True(await storage.PutAsync(key, new MemoryStream(bytes),
                4096, CancellationToken.None));
            var pending = Assert.Single(db.PendingPublications(), item => item.Key == key);
            Assert.Equal("uncommitted-object", recovery.State(pending));
            Assert.True(db.PublishFeedNuget(feedId, name, version, "synthetic",
                sha, bytes.LongLength, "integration-writer"));
            Assert.Equal("committed", recovery.State(pending));
            recovery.Reconcile();
            Assert.DoesNotContain(db.PendingPublications(), item => item.Key == key);
            Assert.Equal(0, recovery.MissingHostedCount());
            Assert.Equal(sha, db.GetFeedNugetRevision(feedId, name, version)?.Sha512);
            Assert.True(db.TryDeactivateHostedNugetFeed(feedId, "integration-admin"));
            recovery.Reconcile();
            Assert.True(storage.Contains(key)); // Disable must not GC immutable bytes.
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

}
