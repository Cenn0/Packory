using Microsoft.Extensions.Logging.Abstractions;
using Packory;
using Xunit;

namespace Packory.Tests;

/// <summary>
/// Startup reconciliation must never lose the last durable record of an
/// interrupted publish when metadata and artifact bytes disagree.
/// </summary>
public sealed class RecoveryJournalSafetyTests
{
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "packory-journal-" + Guid.NewGuid().ToString("N"));
        public Settings Settings { get; }
        public Database Database { get; }
        public BlobStore Blobs { get; }
        public ArtifactRecovery Recovery { get; }

        public Fixture()
        {
            Settings = new Settings { DataDirectory = Root };
            Database = new Database(Settings);
            Database.Initialize();
            Blobs = new BlobStore(Settings);
            Recovery = new ArtifactRecovery(Settings, Database, Blobs,
                NullLogger<ArtifactRecovery>.Instance);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    [Fact]
    public void MissingCommittedBytesKeepTheirJournalAndDegradeReadiness()
    {
        using var fixture = new Fixture();
        const string name = "lost.bytes.test";
        const string version = "1.0.0";
        var key = NuGetEndpoints.NugetKey(name, version);

        fixture.Database.BeginPublication(key, "nuget", name, version);
        Assert.True(fixture.Database.PublishNuget(name, version, "hosted", "test"));
        // Model the crash/storage-loss interval: DB committed, S3/FS bytes absent.
        Assert.False(fixture.Blobs.Contains(key));
        Assert.Equal("missing-hosted-blob",
            fixture.Recovery.State(Assert.Single(fixture.Database.PendingPublications())));

        var report = fixture.Recovery.Reconcile();

        Assert.Equal(1, report.MissingHostedArtifacts);
        Assert.Equal(key, Assert.Single(fixture.Database.PendingPublications()).Key);
        Assert.Equal(1, fixture.Recovery.MissingHostedCount());
        Assert.Equal(1, fixture.Recovery.StaleUnresolvedCount(TimeSpan.Zero));

        // Subsequent startup cycles must not silently erase the journal either.
        fixture.Recovery.Reconcile();
        Assert.Equal(key, Assert.Single(fixture.Database.PendingPublications()).Key);
    }

    [Fact]
    public async Task FullyCommittedBlobAndMetadataCanClearJournal()
    {
        using var fixture = new Fixture();
        const string name = "fully.committed.test";
        const string version = "1.0.0";
        var key = NuGetEndpoints.NugetKey(name, version);
        fixture.Database.BeginPublication(key, "nuget", name, version);
        Assert.True(await fixture.Blobs.PutAsync(key,
            new MemoryStream(new byte[] { 1, 2, 3 }), 1024, CancellationToken.None));
        Assert.True(fixture.Database.PublishNuget(name, version, "hosted", "test"));

        fixture.Recovery.Reconcile();

        Assert.Empty(fixture.Database.PendingPublications());
        Assert.Equal(0, fixture.Recovery.MissingHostedCount());
        Assert.True(fixture.Blobs.Contains(key));
    }

    [Fact]
    public void UnwrittenPublicationCanClearJournalWithoutInventingMetadata()
    {
        using var fixture = new Fixture();
        const string name = "never.written.test";
        const string version = "1.0.0";
        var key = NuGetEndpoints.NugetKey(name, version);
        fixture.Database.BeginPublication(key, "nuget", name, version);

        fixture.Recovery.Reconcile();

        Assert.Empty(fixture.Database.PendingPublications());
        Assert.Null(fixture.Database.PackageOrigin("nuget", name, version));
    }
}
