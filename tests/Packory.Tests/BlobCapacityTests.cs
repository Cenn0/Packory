using System.Text;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class BlobCapacityTests
{
    [Fact]
    public async Task FilesystemQuotaRejectsOverflowWithoutLosingEarlierArtifacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-quota-" + Guid.NewGuid().ToString("N"));
        try
        {
            var config = new Settings { DataDirectory = root, MaxBlobStorageBytes = 12 };
            config.Validate();
            var store = new BlobStore(config);
            Assert.True(await store.PutAsync("nuget/first.nupkg", new MemoryStream(new byte[8]), 100,
                CancellationToken.None));
            await Assert.ThrowsAsync<StorageQuotaExceededException>(async () =>
                await store.PutAsync("nuget/second.nupkg", new MemoryStream(new byte[6]), 100,
                    CancellationToken.None));
            Assert.Null(store.Existing("nuget/second.nupkg"));
            Assert.Equal(8, new FileInfo(store.Existing("nuget/first.nupkg")!).Length);
            await store.ReplaceAsync("nuget/first.nupkg", new MemoryStream(new byte[3]),
                100, CancellationToken.None);
            Assert.True(await store.PutAsync("nuget/second.nupkg", new MemoryStream(new byte[6]), 100,
                CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EvictionNeverDeletesHostedImmutableContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-eviction-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BlobStore(new Settings { DataDirectory = root });
            await store.PutAsync("nuget/keep/1.0.0/keep.1.0.0.nupkg",
                new MemoryStream(Encoding.UTF8.GetBytes("immutable")), 100, CancellationToken.None);
            await store.ReplaceAsync("npm-metadata/a.json", new MemoryStream(new byte[5]), 100, CancellationToken.None);
            await store.ReplaceAsync("nuget-registration/a/b.json",
                new MemoryStream(new byte[5]), 100, CancellationToken.None);
            Assert.Equal(2, store.EvictMutableMetadata());
            Assert.NotNull(store.Existing("nuget/keep/1.0.0/keep.1.0.0.nupkg"));
            Assert.Null(store.Existing("npm-metadata/a.json"));
            Assert.Null(store.Existing("nuget-registration/a/b.json"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void StorageQuotaDoesNotPretendToControlS3()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new Settings { BlobProvider = "s3", MaxBlobStorageBytes = 1000 }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new Settings { MaxBlobStorageBytes = -1 }.Validate());
    }
}
