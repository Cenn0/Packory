using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class OciPersistentUploadTests
{
    [Fact]
    public void UploadSessionSurvivesSingleWriterProcessRestartWithoutCrossRepoLeakage()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-oci-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { DataDirectory = root, MaxArtifactBytes = 4096 };
            var first = new OciUploadStore(settings);
            var id = first.Begin("team/first");
            Assert.True(first.TryGet("team/first", id, out var original));
            Assert.NotNull(original);
            File.WriteAllBytes(original.Path, new byte[] { 7, 8, 9 });
            first.Touch(original);

            // Instantiate a new store as after an application process restart.
            var restarted = new OciUploadStore(settings);
            Assert.False(restarted.TryGet("team/other", id, out _));
            Assert.True(restarted.TryGet("team/first", id, out var resumed));
            Assert.NotNull(resumed);
            Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(resumed.Path));

            restarted.End(id);
            Assert.False(File.Exists(resumed.Path));
            Assert.False(File.Exists(Path.Combine(root, "oci-staging", id + ".json")));
            Assert.False(new OciUploadStore(settings).TryGet("team/first", id, out _));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void OversizedOrCorruptedPersistedSessionsFailClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-oci-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { DataDirectory = root, MaxArtifactBytes = 4096 };
            var first = new OciUploadStore(settings);
            var id = first.Begin("team/first");
            Assert.True(first.TryGet("team/first", id, out var ticket));
            Assert.NotNull(ticket);
            File.WriteAllBytes(ticket.Path, new byte[4097]);

            Assert.False(new OciUploadStore(settings).TryGet("team/first", id, out _));
            Assert.False(first.TryGet("team/first", id, out _));

            var another = first.Begin("team/second");
            File.WriteAllText(Path.Combine(root, "oci-staging", another + ".json"), "{broken");
            Assert.False(new OciUploadStore(settings).TryGet("team/second", another, out _));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void LegacyUnboundStagingBytesRemainUntrustedAfterRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-oci-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "oci-staging");
            Directory.CreateDirectory(path);
            var id = Guid.NewGuid().ToString("N");
            File.WriteAllBytes(Path.Combine(path, id + ".tmp"), new byte[] { 1, 2, 3 });

            var store = new OciUploadStore(new Settings { DataDirectory = root });
            Assert.False(store.TryGet("team/repo", id, out _));
            Assert.True(File.Exists(Path.Combine(path, id + ".tmp")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
