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
        foreach (var revision in database.FeedNugetRevisions())
        {
            if (!FeedDefinition.ValidId(revision.FeedId) || !Names.Nuget(revision.Name) ||
                !Names.Version(revision.Version) ||
                !blobs.Contains(FeedNugetEndpoints.BlobKey(
                    revision.FeedId, revision.Name, revision.Version)))
                missing++;
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

    public bool MetadataCommitted(PendingPublication pending) => pending.Kind switch
    {
        "feed-nuget" => FeedPublicationCommitted(pending),
        "oci-blob" => database.OciHasBlob(pending.Name, pending.Version),
        "oci-manifest" => database.OciManifest(pending.Name, pending.Version) is not null,
        "pypi" => database.PyFiles(pending.Name).Any(
            file => PyPiEndpoints.Key(file.Name, file.Filename) == pending.Key),
        _ => database.PackageOrigin(pending.Kind, pending.Name, pending.Version) == "hosted"
    };

    private bool FeedPublicationCommitted(PendingPublication pending)
    {
        var sep = pending.Name.IndexOf('|');
        if (sep <= 0 || sep == pending.Name.Length - 1) return false;
        var feedId = pending.Name[..sep];
        var name = pending.Name[(sep + 1)..];
        return FeedDefinition.ValidId(feedId) && Names.Nuget(name) &&
            Names.Version(pending.Version) &&
            string.Equals(pending.Key,
                FeedNugetEndpoints.BlobKey(feedId, name, pending.Version),
                StringComparison.Ordinal) &&
            database.GetFeedNugetRevision(feedId, name, pending.Version) is not null;
    }

    public string State(PendingPublication pending)
    {
        var exists = blobs.Contains(pending.Key);
        var committed = MetadataCommitted(pending);
        return (exists, committed) switch
        {
            (true, true) => "committed",
            (false, false) => "not-written",
            (false, true) => "missing-hosted-blob",
            _ => "uncommitted-object"
        };
    }

    /// <summary>
    /// Only overdue unresolved publish intents affect readiness: in-flight writes
    /// do not make the process flap. Prepared evidence remains a blocker.
    /// </summary>
    public int StaleUnresolvedCount(TimeSpan? grace = null)
    {
        var deadline = DateTimeOffset.UtcNow - (grace ?? TimeSpan.FromMinutes(5));
        return database.PendingPublications().Count(pending =>
        {
            var evidence = database.FindQuarantine(pending.Key);
            if (evidence is not null && !evidence.Completed) return true;
            if (!DateTimeOffset.TryParse(pending.StartedAt, out var started) || started > deadline)
                return false;
            return State(pending) is "uncommitted-object" or "missing-hosted-blob";
        });
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

        foreach (var revision in database.FeedNugetRevisions())
        {
            if (!FeedDefinition.ValidId(revision.FeedId) ||
                !Names.Nuget(revision.Name) || !Names.Version(revision.Version))
            {
                missingHosted++;
                logger.LogError("Invalid indexed private feed NuGet revision.");
                continue;
            }
            var key = FeedNugetEndpoints.BlobKey(revision.FeedId,
                revision.Name, revision.Version);
            known.Add(key);
            if (!blobs.Contains(key))
            {
                missingHosted++;
                logger.LogError("Private feed NuGet artifact missing from blob store.");
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
                var immutable = key.StartsWith("feeds/", StringComparison.Ordinal) &&
                    key.EndsWith(".nupkg", StringComparison.Ordinal) ||
                    key.StartsWith("nuget/", StringComparison.Ordinal) && key.EndsWith(".nupkg", StringComparison.Ordinal) ||
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
            var committed = MetadataCommitted(pending);
            var prepared = database.FindQuarantine(pending.Key);
            // Only two states are safe to clear automatically:
            // - neither bytes nor metadata exist (the write never became visible)
            // - both exist (the metadata commit is complete).
            // Metadata referencing missing bytes MUST retain its intent. Clearing it
            // would discard essential recovery evidence after a storage failure.
            if (prepared is null && (exists == committed))
            {
                database.CompletePublication(pending.Key);
            }
            else
            {
                // Prepared quarantine evidence and all mismatched DB/blob states
                // remain operator-visible. Never auto-delete or reconstruct bytes.
                logger.LogError("Publication requires operator investigation: {Kind} {Key}; BlobExists={BlobExists}; MetadataCommitted={MetadataCommitted}; PreparedEvidence={PreparedEvidence}",
                    pending.Kind, pending.Key, exists, committed, prepared is not null);
            }
        }
        return new RecoveryReport(moved, missingHosted);
    }
}
