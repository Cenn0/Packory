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
                "raw" => RawEndpoints.Key(artifact.Name, artifact.Version),
                _ => null
            };
            if (key is null || !blobs.Contains(key)) missing++;
        }
        foreach (var maven in database.MavenAssets())
            if (!blobs.Contains(MavenEndpoints.Key(maven.Path))) missing++;
        foreach (var module in database.GoModules())
            if (!GoModuleBundle.ValidRecord(module) ||
                !blobs.Contains(GoModuleBundle.Key(module.Name, module.Version))) missing++;
        foreach (var asset in database.FeedAssets())
            if (!FeedAssetEntry.Valid(asset) ||
                !blobs.Contains(FeedAssetEntry.BlobKey(asset))) missing++;
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

    private static bool IsImmutableFeedKey(string key)
    {
        var parts = key.Split('/');
        if (parts.Length < 4 || parts[0] != "feeds" || !FeedDefinition.ValidId(parts[1])) return false;
        return parts[2] switch
        {
            "nuget" => key.EndsWith(".nupkg", StringComparison.Ordinal),
            "raw" => key.EndsWith("/payload.blob", StringComparison.Ordinal),
            "maven" => true,
            "go" or "npm" or "cargo" or "pypi" => key.EndsWith(".bundle", StringComparison.Ordinal),
            "oci" => key.EndsWith(".blob", StringComparison.Ordinal) || key.EndsWith(".json", StringComparison.Ordinal),
            _ => false
        };
    }

    public bool MetadataCommitted(PendingPublication pending) => pending.Kind switch
    {
        "feed-nuget" => FeedPublicationCommitted(pending),
        "feed-raw" or "feed-maven" or "feed-go" or "feed-npm" or "feed-cargo" or "feed-pypi" or "feed-oci" => FeedAssetCommitted(pending),
        "go" when GoModuleBundle.ValidCoordinates(pending.Name, pending.Version) &&
            pending.Key == GoModuleBundle.Key(pending.Name, pending.Version) =>
            database.GetGoModule(pending.Name, pending.Version) is not null,
        "maven" when pending.Key == MavenEndpoints.Key(pending.Name) =>
            database.GetMavenAsset(pending.Name) is { } asset && asset.Version == pending.Version,
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

    private bool FeedAssetCommitted(PendingPublication pending)
    {
        var sep = pending.Name.IndexOf('|');
        if (sep <= 0 || sep == pending.Name.Length - 1) return false;
        var feedId = pending.Name[..sep];
        var name = pending.Name[(sep + 1)..];
        if (!FeedDefinition.ValidId(feedId)) return false;
        var kind = pending.Kind["feed-".Length..];
        var path = kind == "raw" ? FeedAssetEntry.RawPath(name, pending.Version) : name;
        var asset = database.GetFeedAsset(feedId, kind, path);
        return asset is not null && asset.Version == pending.Version &&
            FeedAssetEntry.Valid(asset) &&
            string.Equals(FeedAssetEntry.BlobKey(asset), pending.Key, StringComparison.Ordinal);
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
                "raw" when RawEndpoints.ValidName(artifact.Name) && RawEndpoints.ValidVersion(artifact.Version)
                    => RawEndpoints.Key(artifact.Name, artifact.Version),
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

        foreach (var module in database.GoModules())
        {
            if (!GoModuleBundle.ValidRecord(module))
            {
                missingHosted++;
                logger.LogError("Invalid hosted Go module ledger entry.");
                continue;
            }
            var key = GoModuleBundle.Key(module.Name, module.Version);
            known.Add(key);
            if (!blobs.Contains(key))
            {
                missingHosted++;
                logger.LogError("Hosted Go module bundle missing from blob store.");
            }
        }
        foreach (var maven in database.MavenAssets())
        {
            if (!MavenEndpoints.ValidRecord(maven))
            {
                missingHosted++;
                logger.LogError("Invalid indexed Maven asset.");
                continue;
            }
            var key = MavenEndpoints.Key(maven.Path);
            known.Add(key);
            if (!blobs.Contains(key)) missingHosted++;
        }
        foreach (var asset in database.FeedAssets())
        {
            if (!FeedAssetEntry.Valid(asset))
            {
                missingHosted++;
                logger.LogError("Invalid indexed private feed asset.");
                continue;
            }
            var key = FeedAssetEntry.BlobKey(asset);
            known.Add(key);
            if (!blobs.Contains(key))
            {
                missingHosted++;
                logger.LogError("Private feed artifact missing from blob store.");
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
        // Publication intent is authoritative recovery evidence. An incomplete
        // filesystem write has an immutable blob but no catalog reference yet.
        // Moving that blob before evaluating intents would make State() report
        // "not-written" and erase the journal, losing the link to preserved bytes.
        // Only genuinely unreferenced, untracked objects may be auto-quarantined.
        var pendingPublications = database.PendingPublications();
        var pendingKeys = pendingPublications.Select(p => p.Key)
            .ToHashSet(StringComparer.Ordinal);
        var root = Path.Combine(Path.GetFullPath(settings.DataDirectory), "blobs");
        var quarantine = Path.Combine(Path.GetFullPath(settings.DataDirectory), "recovery", "orphaned",
            DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N"));
        var moved = 0;
        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var key = Path.GetRelativePath(root, path).Replace('\\', '/');
                var immutable = IsImmutableFeedKey(key) ||
                    key.StartsWith("nuget/", StringComparison.Ordinal) && key.EndsWith(".nupkg", StringComparison.Ordinal) ||
                    key.StartsWith("npm/", StringComparison.Ordinal) && key.EndsWith(".tgz", StringComparison.Ordinal) ||
                    key.StartsWith("skills/", StringComparison.Ordinal) && key.EndsWith(".zip", StringComparison.Ordinal) ||
                    key.StartsWith("cargo/", StringComparison.Ordinal) && key.EndsWith(".crate", StringComparison.Ordinal) ||
                    key.StartsWith("pypi/", StringComparison.Ordinal) &&
                    (key.EndsWith(".whl", StringComparison.Ordinal) ||
                     key.EndsWith(".tar.gz", StringComparison.Ordinal) ||
                     key.EndsWith(".zip", StringComparison.Ordinal)) ||
                    key.StartsWith("oci/blobs/", StringComparison.Ordinal) && key.EndsWith(".blob", StringComparison.Ordinal) ||
                    key.StartsWith("oci/manifests/", StringComparison.Ordinal) && key.EndsWith(".json", StringComparison.Ordinal) ||
                     key.StartsWith("raw/", StringComparison.Ordinal) && key.EndsWith("/payload.blob", StringComparison.Ordinal) ||
                    key.StartsWith("maven/", StringComparison.Ordinal) ||
                    key.StartsWith("go/modules/", StringComparison.Ordinal) &&
                        key.EndsWith(".bundle", StringComparison.Ordinal);
                if (!immutable || known.Contains(key) || pendingKeys.Contains(key)) continue;
                var destination = Path.Combine(quarantine, key.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Move(path, destination);
                moved++;
            }
        }
        if (moved > 0)
            logger.LogWarning("Quarantined {Count} unindexed immutable blob(s) under recovery/orphaned. Review before deletion.", moved);
        foreach (var pending in pendingPublications)
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
