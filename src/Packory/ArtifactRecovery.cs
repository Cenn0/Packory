namespace Packory;

public sealed record RecoveryReport(int QuarantinedOrphans, int MissingHostedArtifacts);

/// <summary>
/// One-time, pre-serve reconciliation. A crash between writing an immutable file
/// and committing its database reference may leave an orphan. Move it aside rather
/// than accidentally serving it or silently deleting user data.
/// </summary>
public sealed class ArtifactRecovery(Settings settings, Database database, BlobStore blobs, ILogger<ArtifactRecovery> logger)
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
                _ => null
            };
            if (key is null || blobs.Existing(key) is null) missing++;
        }
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
                _ => null
            };
            if (key is null)
            {
                logger.LogWarning("Invalid stored artifact identity: {Kind}", artifact.Kind);
                if (artifact.Origin == "hosted") missingHosted++;
                continue;
            }
            known.Add(key);
            if (artifact.Origin == "hosted" && blobs.Existing(key) is null)
            {
                missingHosted++;
                logger.LogError("Hosted artifact is missing: {Kind} {Name} {Version}",
                    artifact.Kind, artifact.Name, artifact.Version);
            }
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
                    key.StartsWith("skills/", StringComparison.Ordinal) && key.EndsWith(".zip", StringComparison.Ordinal);
                if (!immutable || known.Contains(key)) continue;
                var destination = Path.Combine(quarantine, key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(path, destination);
                moved++;
            }
        }
        if (moved > 0)
            logger.LogWarning("Quarantined {Count} unindexed immutable blob(s) under recovery/orphaned. Review before deletion.", moved);
        return new RecoveryReport(moved, missingHosted);
    }
}
