using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class S3ProviderFactAttribute : FactAttribute
{
    public S3ProviderFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PACKORY_TEST_S3")))
            Skip = "Requires an isolated S3 endpoint.";
    }
}

public sealed class S3ProviderTests
{
    [S3ProviderFact]
    public async Task AbandonedLocalStageCannotBlockOrSubstituteNewRemoteWrite()
    {
        var endpoint = Environment.GetEnvironmentVariable("PACKORY_TEST_S3");
        Assert.False(string.IsNullOrWhiteSpace(endpoint));
        var root = Path.Combine(Path.GetTempPath(), "packory-s3-abandoned-" + Guid.NewGuid().ToString("N"));
        const string bucket = "packory-ci-objectstore";
        var settings = new Settings { DataDirectory = root, BlobProvider = "s3",
            S3Endpoint = endpoint, S3Bucket = bucket, S3Region = "us-east-1",
            S3KeyPrefix = "abandoned-" + Guid.NewGuid().ToString("N"),
            S3AccessKey = "test", S3SecretKey = "test", S3AllowLoopbackHttp = true };
        settings.Validate();
        using var sdk = new AmazonS3Client(new BasicAWSCredentials("test", "test"), new AmazonS3Config {
            ServiceURL = endpoint, AuthenticationRegion = "us-east-1", ForcePathStyle = true });
        await sdk.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        try
        {
            const string key = "raw/example/1.0.0/payload.blob";
            var abandoned = Encoding.UTF8.GetBytes("abandoned local-only content");
            Assert.True(await new BlobStore(settings).PutAsync(key, new MemoryStream(abandoned), 1024, CancellationToken.None));
            using var remote = new S3BlobStore(settings);
            Assert.False(remote.Contains(key));
            var fresh = Encoding.UTF8.GetBytes("current caller content");
            Assert.True(await remote.PutAsync(key, new MemoryStream(fresh), 1024, CancellationToken.None));
            using var fetched = await sdk.GetObjectAsync(bucket, settings.S3KeyPrefix + "/" + key);
            using var bytes = new MemoryStream(); await fetched.ResponseStream.CopyToAsync(bytes);
            Assert.Equal(fresh, bytes.ToArray());
            Assert.Equal(fresh, await File.ReadAllBytesAsync(remote.Existing(key)!));
            Assert.False(await remote.PutAsync(key, new MemoryStream(abandoned), 1024, CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [S3ProviderFact]
    public async Task S3ImmutableWritesAndMutableMetadataSurviveRestart()
    {
        var endpoint = Environment.GetEnvironmentVariable("PACKORY_TEST_S3");
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        const string bucket = "packory-ci-objectstore";
        var config = new AmazonS3Config
        {
            ServiceURL = endpoint, AuthenticationRegion = "us-east-1", ForcePathStyle = true
        };
        using var client = new AmazonS3Client(new BasicAWSCredentials("test", "test"), config);
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
        var id = Guid.NewGuid().ToString("N")[..12];
        var first = Path.Combine(Path.GetTempPath(), "packory-s3-" + Guid.NewGuid().ToString("N"));
        var second = Path.Combine(Path.GetTempPath(), "packory-s3-" + Guid.NewGuid().ToString("N"));
        static Settings For(string folder, string endpoint, string bucket, string id) => new()
        {
            BlobProvider = "s3", DataDirectory = folder, S3Bucket = bucket,
            S3Endpoint = endpoint, S3KeyPrefix = "test-" + id,
            S3Region = "us-east-1", S3AccessKey = "test", S3SecretKey = "test",
            S3AllowLoopbackHttp = true
        };
        try
        {
            var settings = For(first, endpoint, bucket, id);
            settings.Validate();
            const string package = "nuget/test/1.0.0/test.1.0.0.nupkg";
            var bytes = Encoding.UTF8.GetBytes("immutable test package");
            using var store = new S3BlobStore(settings);
            Assert.False(store.Contains(package));
            Assert.True(await store.PutAsync(package, new MemoryStream(bytes), 1024, CancellationToken.None));
            Assert.True(store.Contains(package));
            Assert.False(await store.PutAsync(package, new MemoryStream(bytes), 1024, CancellationToken.None));
            // Crash after the S3 write but before DB commit must retain an
            // operator-visible remote orphan record rather than silently losing it.
            IPackageDatabase publicationDb = new Database(settings);
            publicationDb.Initialize();
            publicationDb.BeginPublication(package, "nuget", "test", "1.0.0");
            var recovery = new ArtifactRecovery(settings, publicationDb, store,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtifactRecovery>.Instance);
            recovery.Reconcile();
            Assert.Contains(publicationDb.PendingPublications(), pending => pending.Key == package);
            Assert.True(publicationDb.PublishNuget("test", "1.0.0", "test", "integration-writer"));
            recovery.Reconcile();
            Assert.Empty(publicationDb.PendingPublications());
            const string metadata = "npm-metadata/demo.json";
            await store.ReplaceAsync(metadata, new MemoryStream(Encoding.UTF8.GetBytes("v1")), 1024, CancellationToken.None);
            await store.ReplaceAsync(metadata, new MemoryStream(Encoding.UTF8.GetBytes("v2")), 1024, CancellationToken.None);
            using var restarted = new S3BlobStore(For(second, endpoint, bucket, id));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(restarted.Existing(package)!));
            Assert.Equal("v2", await File.ReadAllTextAsync(restarted.Existing(metadata)!));
            Assert.Null(restarted.Existing("skills/absent/1.0.0.zip"));
            var ociBytes = Encoding.UTF8.GetBytes("OCI integrity data");
            var sha = Convert.ToHexStringLower(SHA256.HashData(ociBytes));
            var ociKey = "oci/blobs/sha256/" + sha + ".blob";
            Assert.True(await store.PutAsync(ociKey, new MemoryStream(ociBytes), 1024, CancellationToken.None));
            Assert.Equal(ociBytes, await File.ReadAllBytesAsync(restarted.Existing(ociKey)!));
            // An already materialized local cache file is not an integrity authority.
            var localOci = restarted.Existing(ociKey)!;
            await File.WriteAllTextAsync(localOci, "tampered cache");
            Assert.Throws<InvalidDataException>(() => restarted.Existing(ociKey));
            // Removing the corrupted cache allows a fresh verified remote materialization.
            Assert.Equal(ociBytes, await File.ReadAllBytesAsync(restarted.Existing(ociKey)!));
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket, Key = "test-" + id + "/" + package
            });
            Assert.False(store.Contains(package));
            Assert.Null(store.Existing(package));
        }
        finally
        {
            if (Directory.Exists(first)) Directory.Delete(first, true);
            if (Directory.Exists(second)) Directory.Delete(second, true);
        }
    }
    [S3ProviderFact]
    public async Task RemoteIntegrityAuditDetectsTamperingHiddenByHealthyLocalCache()
    {
        var endpoint = Environment.GetEnvironmentVariable("PACKORY_TEST_S3");
        if (string.IsNullOrWhiteSpace(endpoint)) return;
        const string bucket = "packory-ci-objectstore";
        var prefix = "audit-" + Guid.NewGuid().ToString("N")[..12];
        var root = Path.Combine(Path.GetTempPath(), "packory-s3-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = new Settings
            {
                DataDirectory = root, BlobProvider = "s3", S3Endpoint = endpoint,
                S3Bucket = bucket, S3KeyPrefix = prefix, S3Region = "us-east-1",
                S3AccessKey = "test", S3SecretKey = "test", S3AllowLoopbackHttp = true
            };
            cfg.Validate();
            var db = new Database(cfg);
            db.Initialize();
            var security = new SkillSecurityStore(cfg);
            security.Initialize();
            using var store = new S3BlobStore(cfg);
            using var sdk = new AmazonS3Client(new BasicAWSCredentials("test", "test"),
                new AmazonS3Config
                {
                    ServiceURL = endpoint, AuthenticationRegion = "us-east-1", ForcePathStyle = true
                });
            await sdk.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
            var original = Encoding.UTF8.GetBytes("original canonical bytes");
            var changed = original.ToArray();
            changed[^1] ^= 0x20; // Same length: only the authoritative digest detects this change.
            const string filename = "remote_integrity-1.0.0-py3-none-any.whl";
            var file = new PyFileEntry("remote-integrity", "1.0.0", filename,
                Convert.ToHexStringLower(SHA256.HashData(original)), original.Length);
            Assert.True(db.PublishPyFile(file, "sample", "integration-writer"));
            var key = PyPiEndpoints.Key(file.Name, filename);
            Assert.True(await store.PutAsync(key,
                new MemoryStream(original, writable: false), 4096, CancellationToken.None));
            var oldCache = store.Existing(key)!;
            Assert.Equal(original, await File.ReadAllBytesAsync(oldCache));
            foreach (var algorithm in new[] { "sha256", "sha512" })
                Assert.True(await ArtifactDigests.MatchesRemoteAsync(store, key, algorithm,
                    algorithm == "sha256" ? SHA256.HashData(original) : SHA512.HashData(original),
                    original.Length, 4096, CancellationToken.None));

            var scanner = new ArtifactIntegrityScanner(db, store, security, cfg);
            var initial = await scanner.ScanAsync(0, 1, CancellationToken.None);
            Assert.Equal("digest_match", Assert.Single(initial.Items).State);

            // Emulate storage-side corruption while retaining the earlier local
            // cache copy. An audit of Existing(key) would falsely pass.
            using (var payload = new MemoryStream(changed, writable: false))
                await sdk.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket, Key = prefix + "/" + key,
                    InputStream = payload, AutoCloseStream = false
                });
            Assert.Equal(original, await File.ReadAllBytesAsync(oldCache));
            foreach (var algorithm in new[] { "sha256", "sha512" })
                Assert.False(await ArtifactDigests.MatchesRemoteAsync(store, key, algorithm,
                    algorithm == "sha256" ? SHA256.HashData(original) : SHA512.HashData(original),
                    original.Length, 4096, CancellationToken.None));
            var corrupted = await scanner.ScanAsync(0, 1, CancellationToken.None);
            Assert.Equal("digest_mismatch", Assert.Single(corrupted.Items).State);

            var directlyRead = await store.InspectRemoteAsync(key, "sha256", 4096,
                CancellationToken.None);
            Assert.True(directlyRead.Exists);
            Assert.False(directlyRead.ExceedsLimit);
            Assert.Equal(changed.Length, directlyRead.Size);
            Assert.Equal(SHA256.HashData(changed), directlyRead.Digest);

            var exceeded = await store.InspectRemoteAsync(key, "sha256", 1,
                CancellationToken.None);
            Assert.True(exceeded.ExceedsLimit);
            var missing = await store.InspectRemoteAsync("pypi/not-present/0.0.1.whl",
                "sha256", 4096, CancellationToken.None);
            Assert.False(missing.Exists);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

}
