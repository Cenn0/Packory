using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Packory;
using Xunit;

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
            var inspected = Assert.Single(manager.Inspect().Where(x => x.Key == key));
            Assert.Equal("uncommitted-object", inspected.State);
            var resolved = await manager.ResolveAsync(key, inspected.StartedAt, CancellationToken.None);
            Assert.Equal("quarantined-recoverable", resolved.Outcome);
            Assert.False(s3.Contains(key));
            var evidence = db.FindQuarantine(key);
            Assert.NotNull(evidence);
            Assert.True(evidence.Completed);
            await s3.VerifyArchivedAsync(evidence, CancellationToken.None);
            Assert.DoesNotContain(db.PendingPublications(), p => p.Key == key);
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
}
