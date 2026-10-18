using System.Security.Cryptography;
using System.Text;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class NugetDigestTests
{
    [Fact]
    public void V3SqliteRecordsImmutablePublisherHashAndRejectsConflictingMirrors()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-nuget-digest-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Database(new Settings { DataDirectory = root });
            db.Initialize();
            var bytes = Encoding.UTF8.GetBytes("immutable fixture bytes");
            var sha = Convert.ToBase64String(SHA512.HashData(bytes));
            Assert.True(NugetDigest.Valid(sha));
            Assert.False(NugetDigest.Valid(sha.TrimEnd('=')));
            Assert.False(NugetDigest.Valid("malformed"));
            Assert.True(db.PublishNuget("integrity.package", "1.0.0", "synthetic",
                "fixture", sha, bytes.Length));
            var stored = db.GetNugetDigest("integrity.package", "1.0.0");
            Assert.NotNull(stored);
            Assert.Equal(sha, stored.Sha512);
            Assert.Equal(bytes.Length, stored.Size);
            Assert.Equal("hosted", stored.Origin);
            Assert.False(db.PublishNuget("integrity.package", "1.0.0", "new metadata", "fixture",
                sha, bytes.Length));
            Assert.Equal(sha, db.GetNugetDigest("integrity.package", "1.0.0")?.Sha512);
            Assert.Throws<InvalidOperationException>(() => db.UpsertNugetMirror(
                "integrity.package", "1.0.0", "not allowed", sha, bytes.Length));
            Assert.Throws<InvalidDataException>(() => db.PublishNuget(
                "invalid.package", "1.0.0", "bad", "fixture", "not-base64", 5));
            Assert.False(db.HasPackage("nuget", "invalid.package", "1.0.0"));

            db.UpsertNugetMirror("mirrored.package", "1.0.0", "synthetic", sha, bytes.Length);
            Assert.Equal(sha, db.GetNugetDigest("mirrored.package", "1.0.0")?.Sha512);
            Assert.Equal("mirror", db.GetNugetDigest("mirrored.package", "1.0.0")?.Origin);
            Assert.Throws<InvalidDataException>(() => db.UpsertNugetMirror(
                "mirrored.package", "1.0.0", "changed content",
                Convert.ToBase64String(SHA512.HashData("changed"u8.ToArray())),
                "changed"u8.Length));
            Assert.Equal(sha, db.GetNugetDigest("mirrored.package", "1.0.0")?.Sha512);
            db.Initialize();
            Assert.Equal(sha, db.GetNugetDigest("mirrored.package", "1.0.0")?.Sha512);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ScannerUsesPersistedPublisherHashNeverSelfHash()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-nuget-audit-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = new Settings { DataDirectory = root };
            var db = new Database(cfg);
            db.Initialize();
            var security = new SkillSecurityStore(cfg);
            security.Initialize();
            var blobs = new BlobStore(cfg);
            var bytes = Encoding.UTF8.GetBytes("new hosted synthetic nupkg");
            var sha = Convert.ToBase64String(SHA512.HashData(bytes));
            Assert.True(db.PublishNuget("integritynew", "1.0.0", "test", "fixture",
                sha, bytes.Length));
            var key = NuGetEndpoints.NugetKey("integritynew", "1.0.0");
            Assert.True(await blobs.PutAsync(key, new MemoryStream(bytes, false),
                4096, CancellationToken.None));
            var scanner = new ArtifactIntegrityScanner(db, blobs, security, cfg);
            var good = await scanner.ScanAsync(0, 10, CancellationToken.None);
            Assert.Equal("digest_match", Assert.Single(good.Items).State);
            Assert.Equal("sha512", Assert.Single(good.Items).DigestBasis);

            await File.WriteAllBytesAsync(blobs.Existing(key)!, "corrupt"u8.ToArray());
            var invalid = await scanner.ScanAsync(0, 10, CancellationToken.None);
            Assert.Equal("digest_mismatch", Assert.Single(invalid.Items).State);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
