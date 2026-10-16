using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class S3ProviderTests
{
    [Fact]
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
            IPackageDatabase metadata = new Database(settings);
            metadata.Initialize();
            metadata.BeginPublication(package, "nuget", "test", "1.0.0");
            var recovery = new ArtifactRecovery(settings, metadata, store,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtifactRecovery>.Instance);
            recovery.Reconcile();
            Assert.Contains(metadata.PendingPublications(), pending => pending.Key == package);
            Assert.True(metadata.PublishNuget("test", "1.0.0", "test", "integration-writer"));
            recovery.Reconcile();
            Assert.Empty(metadata.PendingPublications());
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
}
