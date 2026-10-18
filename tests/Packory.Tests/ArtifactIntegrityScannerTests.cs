using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Packory;
using Xunit;

namespace Packory.Tests;

/// <summary>
/// Tests actual SQLite and filesystem storage, not synthetic in-memory hashes.
/// PostgreSQL and S3 backend tests remain mandatory in the dedicated workflows.
/// </summary>
public sealed class ArtifactIntegrityScannerTests
{
    private static async Task StoreAsync(BlobStore store, string key, byte[] bytes)
    {
        Assert.True(await store.PutAsync(key, new MemoryStream(bytes, writable: false),
            bytes.Length + 16, CancellationToken.None));
    }

    [Fact]
    public async Task PagesRealReferencesAcrossSixEcosystemsWithoutInventingNugetProof()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-integrity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { DataDirectory = root };
            settings.Validate();
            var db = new Database(settings);
            db.Initialize();
            var security = new SkillSecurityStore(settings);
            security.Initialize();
            var store = new BlobStore(settings);
            const string version = "1.0.0";

            var nuget = Encoding.UTF8.GetBytes("synthetic nuget bytes");
            Assert.True(db.PublishNuget("integrity.sample", version, "sample", "test-actor"));
            await StoreAsync(store, NuGetEndpoints.NugetKey("integrity.sample", version), nuget);

            var npmName = "integrity-npm";
            var npm = Encoding.UTF8.GetBytes("synthetic npm bytes");
            var npmInfo = JsonSerializer.Serialize(new
            {
                name = npmName, version,
                dist = new { integrity = "sha512-" + Convert.ToBase64String(SHA512.HashData(npm)) }
            });
            Assert.True(db.PutNpmVersion(npmName, version, npmInfo, "synthetic",
                new Dictionary<string, string> { ["latest"] = version }, "test-actor"));
            await StoreAsync(store, NpmEndpoints.NpmKey(npmName, version), npm);

            var cargoName = "integritycargo";
            var cargo = Encoding.UTF8.GetBytes("synthetic crate bytes");
            var cargoInfo = JsonSerializer.Serialize(new
            {
                name = cargoName, vers = version,
                cksum = Convert.ToHexStringLower(SHA256.HashData(cargo))
            });
            Assert.True(db.PublishCargo(cargoName, version, cargoInfo, "sample", "test-actor"));
            await StoreAsync(store, CargoEndpoints.Key(cargoName, version), cargo);

            var wheel = Encoding.UTF8.GetBytes("synthetic wheel bytes");
            const string wheelName = "integrity_pypi-1.0.0-py3-none-any.whl";
            var pyFile = new PyFileEntry("integrity-pypi", version, wheelName,
                Convert.ToHexStringLower(SHA256.HashData(wheel)), wheel.Length);
            Assert.True(db.PublishPyFile(pyFile, "sample", "test-actor"));
            await StoreAsync(store, PyPiEndpoints.Key(pyFile.Name, wheelName), wheel);

            var skill = Encoding.UTF8.GetBytes("synthetic immutable skill archive");
            var skillDigest = Convert.ToHexStringLower(SHA256.HashData(skill));
            Assert.True(db.AddSkill("integrity-skill", version, "sample", "test-actor"));
            await StoreAsync(store, SkillEndpoints.SkillKey("integrity-skill", version), skill);
            Assert.True(security.TryStage("integrity-skill", version, "sample",
                skillDigest, "skill-staging/integrity-skill/1.0.0.zip"));
            Assert.True(security.SaveResult("integrity-skill", version, skillDigest,
                "NEEDS_REVIEW", 0, "SAFE", "{}"));
            Assert.True(security.TryTransition("integrity-skill", version, skillDigest,
                "NEEDS_REVIEW", "APPROVED", "test-reviewer", "Synthetic review fixture"));

            var layer = Encoding.UTF8.GetBytes("synthetic OCI layer");
            var blobDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(layer));
            db.OciAddBlob("integrity/repo", blobDigest);
            await StoreAsync(store, OciEndpoints.BlobKey(blobDigest), layer);

            var manifest = Encoding.UTF8.GetBytes("{\"schemaVersion\":2}");
            var manifestDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(manifest));
            Assert.True(db.OciPutManifest("integrity/repo", "latest",
                new OciManifestEntry(manifestDigest,
                    "application/vnd.oci.image.manifest.v1+json", manifest.Length),
                null, null, "test-actor"));
            await StoreAsync(store, OciEndpoints.ManifestKey(manifestDigest), manifest);

            var scanner = new ArtifactIntegrityScanner(db, store, security, settings);
            var first = await scanner.ScanAsync(0, 3, CancellationToken.None);
            var second = await scanner.ScanAsync(3, 3, CancellationToken.None);
            var last = await scanner.ScanAsync(6, 3, CancellationToken.None);
            var all = first.Items.Concat(second.Items).Concat(last.Items).ToArray();

            Assert.Equal(7, first.TotalReferences);
            Assert.Equal(3, first.Checked);
            Assert.Equal(3, first.NextOffset);
            Assert.Equal(6, second.NextOffset);
            Assert.Null(last.NextOffset);
            Assert.Equal(7, all.Length);
            Assert.Equal(6, all.Count(x => x.State == "digest_match"));
            Assert.Single(all, x => x.Kind == "nuget" && x.State == "unverifiable" &&
                x.DigestBasis == "no-stored-digest");
            Assert.All(all.Where(x => x.State == "digest_match"),
                x => Assert.Contains(x.DigestBasis, new[] { "sha256", "sha512" }));
            Assert.Equal(0, first.PendingPublicationIntents);
            Assert.Contains("no full snapshot guarantee", first.Coverage);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TamperedMissingInvalidAndWeakMetadataAreNeverReportedAsVerified()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-integrity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { DataDirectory = root };
            var db = new Database(settings);
            db.Initialize();
            var security = new SkillSecurityStore(settings);
            security.Initialize();
            var blobs = new BlobStore(settings);

            var advertised = Convert.ToHexStringLower(SHA256.HashData("expected"u8.ToArray()));
            Assert.True(db.PublishCargo("integritybad", "1.0.0",
                JsonSerializer.Serialize(new { name = "integritybad", vers = "1.0.0", cksum = advertised }),
                "sample", "test-actor"));
            await StoreAsync(blobs, CargoEndpoints.Key("integritybad", "1.0.0"),
                "tampered"u8.ToArray());

            Assert.True(db.PublishPyFile(new PyFileEntry("integrity-missing", "1.0.0",
                "integrity_missing-1.0.0-py3-none-any.whl", advertised, 8),
                "sample", "test-actor"));

            db.OciAddBlob("integrity/repo", "sha256:bad");
            db.UpsertPackage("nuget", "../invalid", "1.0.0", "bad identifier",
                "hosted");

            var scanner = new ArtifactIntegrityScanner(db, blobs, security, settings);
            var page = await scanner.ScanAsync(0, 10, CancellationToken.None);
            Assert.Equal(4, page.TotalReferences);
            Assert.Single(page.Items, x => x.Kind == "cargo" && x.State == "digest_mismatch");
            Assert.Single(page.Items, x => x.Kind == "pypi" && x.State == "missing");
            Assert.Equal(2, page.Items.Count(x => x.State == "invalid_reference"));
            Assert.DoesNotContain(page.Items, x => x.State == "digest_match");

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => scanner.ScanAsync(0, 11, CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => scanner.ScanAsync(-1, 2, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnresolvedPublicationIsVisibleAsNonzeroPreflightBlocker()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-integrity-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new Settings { DataDirectory = root };
            var db = new Database(settings);
            db.Initialize();
            var security = new SkillSecurityStore(settings);
            security.Initialize();
            var scanner = new ArtifactIntegrityScanner(db, new BlobStore(settings),
                security, settings);
            db.BeginPublication("nuget/unresolved/1.0.0/unresolved.1.0.0.nupkg",
                "nuget", "unresolved", "1.0.0");
            var report = await scanner.ScanAsync(0, 2, CancellationToken.None);
            Assert.Equal(1, report.PendingPublicationIntents);
            Assert.Empty(report.Items);
            Assert.Null(report.NextOffset);
            Assert.Single(db.PendingPublications());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
