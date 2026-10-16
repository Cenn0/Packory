namespace Packory;

public sealed record RecoveryReport(int QuarantinedOrphans, int MissingHostedArtifacts);

/// <summary>
/// One-time, pre-serve reconciliation. A crash between writing an immutable file
/// and committing its database reference may leave an orphan. Move it aside rather
/// than accidentally serving it or silently deleting user data.
/// </summary>
public sealed class ArtifactRecovery(Settings settings, IPackageDatabase database, BlobStore blobs, ILogger<ArtifactRecovery> logger)
{
    public int MissingHostedCount()
    {
        var missing = 0;
        foreach (var artifact in database.StoredArtifacts().Where(x => x.Origin == "hosted"))
        {
            var key = artifact.Kind switch
            {
                "nuget" => NuGetEndpoints.NugetKey(artifact.Name, artifact.Version),
                "npm" => NpmEndpoints.NpmKey(artifact.Name, artifact.Version),
                "skill" => SkillEndpoints.SkillKey(artifact.Name, artifact.Version),
                "cargo" => CargoEndpoints.Key(artifact.Name, artifact.Version),
                _ => null
            };
            if (key is null || !blobs.Contains(key)) missing++;
        }
        foreach (var file in database.PyFiles())
        {
            if (!blobs.Contains(PyPiEndpoints.Key(file.Name, file.Filename))) missing++;
        }
        foreach (var digest in database.OciAllBlobDigests())
            if (!blobs.Contains(OciEndpoints.BlobKey(digest))) missing++;
        foreach (var digest in database.OciAllManifestDigests())
            if (!blobs.Contains(OciEndpoints.ManifestKey(digest))) missing++;
        return missing;
    }

    public RecoveryReport Reconcile()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        var missingHosted = 0;
        foreach (var artifact in database.StoredArtifacts())
        {
            string? key = artifact.Kind switch
            {
                "nuget" when Names.Nuget(artifact.Name) && Names.Version(artifact.Version)
                    => NuGetEndpoints.NugetKey(artifact.Name, artifact.Version),
                "npm" when Names.Npm(artifact.Name) && Names.Version(artifact.Version)
                    => NpmEndpoints.NpmKey(artifact.Name, artifact.Version),
                "skill" when Names.Skill(artifact.Name) && Names.Version(artifact.Version)
                    => SkillEndpoints.SkillKey(artifact.Name, artifact.Version),
                "cargo" when CargoEndpoints.IsName(artifact.Name) && CargoEndpoints.IsVersion(artifact.Version)
                    => CargoEndpoints.Key(artifact.Name, artifact.Version),
                _ => null
            };
            if (key is null)
            {
                logger.LogWarning("Invalid stored artifact identity: {Kind}", artifact.Kind);
                if (artifact.Origin == "hosted") missingHosted++;
                continue;
            }
            known.Add(key);
            if (artifact.Origin == "hosted" && !blobs.Contains(key))
            {
                missingHosted++;
                logger.LogError("Hosted artifact is missing: {Kind} {Name} {Version}",
                    artifact.Kind, artifact.Name, artifact.Version);
            }
        }

        foreach (var file in database.PyFiles())
        {
            var key = PyPiEndpoints.Key(file.Name, file.Filename);
            known.Add(key);
            if (!blobs.Contains(key)) missingHosted++;
        }
        foreach (var digest in database.OciAllBlobDigests())
        {
            var key = OciEndpoints.BlobKey(digest);
            known.Add(key);
            if (!blobs.Contains(key)) missingHosted++;
        }
        foreach (var digest in database.OciAllManifestDigests())
        {
            var key = OciEndpoints.ManifestKey(digest);
            known.Add(key);
            if (!blobs.Contains(key)) missingHosted++;
        }
        var root = Path.Combine(Path.GetFullPath(settings.DataDirectory), "blobs");
        var quarantine = Path.Combine(Path.GetFullPath(settings.DataDirectory), "recovery", "orphaned",
            DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N"));
        var moved = 0;
        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var key = Path.GetRelativePath(root, path).Replace('\\', '/');
                var immutable = key.StartsWith("nuget/", StringComparison.Ordinal) && key.EndsWith(".nupkg", StringComparison.Ordinal) ||
                    key.StartsWith("npm/", StringComparison.Ordinal) && key.EndsWith(".tgz", StringComparison.Ordinal) ||
                    key.StartsWith("skills/", StringComparison.Ordinal) && key.EndsWith(".zip", StringComparison.Ordinal) ||
                    key.StartsWith("cargo/", StringComparison.Ordinal) && key.EndsWith(".crate", StringComparison.Ordinal) ||
                    key.StartsWith("pypi/", StringComparison.Ordinal) &&
                    (key.EndsWith(".whl", StringComparison.Ordinal) ||
                     key.EndsWith(".tar.gz", StringComparison.Ordinal) ||
                     key.EndsWith(".zip", StringComparison.Ordinal)) ||
                    key.StartsWith("oci/blobs/", StringComparison.Ordinal) && key.EndsWith(".blob", StringComparison.Ordinal) ||
                    key.StartsWith("oci/manifests/", StringComparison.Ordinal) && key.EndsWith(".json", StringComparison.Ordinal);
                if (!immutable || known.Contains(key)) continue;
                var destination = Path.Combine(quarantine, key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(path, destination);
                moved++;
            }
        }
        if (moved > 0)
            logger.LogWarning("Quarantined {Count} unindexed immutable blob(s) under recovery/orphaned. Review before deletion.", moved);
        foreach (var pending in database.PendingPublications())
        {
            var exists = blobs.Contains(pending.Key);
            var committed = pending.Kind switch
            {
                "oci-blob" => database.OciHasBlob(pending.Name, pending.Version),
                "oci-manifest" => database.OciManifest(pending.Name, pending.Version) is not null,
                "pypi" => database.PyFiles(pending.Name).Any(
                    file => PyPiEndpoints.Key(file.Name, file.Filename) == pending.Key),
                _ => database.PackageOrigin(pending.Kind, pending.Name, pending.Version) == "hosted"
            };
            if (!exists || committed)
            {
                // Crash before blob creation or after committed metadata: no remote orphan.
                database.CompletePublication(pending.Key);
            }
            else
            {
                // Fail closed: no automatic remote S3 deletes or unverified DB reconstruction.
                logger.LogError("Uncommitted artifact bytes require operator reconciliation: {Kind} {Key}",
                    pending.Kind, pending.Key);
            }
        }
        return new RecoveryReport(moved, missingHosted);
    }
}
