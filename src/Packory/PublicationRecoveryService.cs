namespace Packory;

public sealed record RecoveryInspection(
    string Key, string Kind, string Name, string Version, string StartedAt,
    string State, string? RecoveryKey, bool QuarantineCompleted);

public sealed record RecoveryResolution(string Key, string Outcome, string? RecoveryKey, string? Sha256);

/// <summary>
/// Explicit operator-controlled offline repair. No metadata reconstruction or
/// unconditional deletions. PostgreSQL exclusive lease is held by the host.
/// </summary>
public sealed class PublicationRecoveryService(
    Settings settings, IPackageDatabase database, BlobStore blobs, ArtifactRecovery recovery)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public IReadOnlyList<RecoveryInspection> Inspect() =>
        database.PendingPublications().Select(p =>
        {
            var evidence = database.FindQuarantine(p.Key);
            return new RecoveryInspection(p.Key, p.Kind, p.Name, p.Version, p.StartedAt,
                recovery.State(p), evidence?.RecoveryKey, evidence?.Completed ?? false);
        }).ToArray();

    public async Task<RecoveryResolution> ResolveAsync(
        string key, string expectedStartedAt, CancellationToken ct)
    {
        if (!settings.RecoveryMaintenance || blobs is not S3BlobStore store)
            throw new InvalidOperationException("Quarantine requires exclusive PostgreSQL/S3 recovery maintenance");
        await _gate.WaitAsync(ct);
        try
        {
            var pending = database.PendingPublications().SingleOrDefault(x => x.Key == key);
            if (pending is null || pending.StartedAt != expectedStartedAt)
                throw new InvalidOperationException("Publication intent has changed since operator inspection");

            var evidence = database.FindQuarantine(key);
            var state = recovery.State(pending);
            if (state == "missing-hosted-blob")
                throw new InvalidOperationException("Committed metadata references missing bytes; restore a verified backup first");
            if (state == "committed")
            {
                if (evidence is not null)
                    throw new InvalidOperationException("Prepared quarantine collides with committed metadata; manual review required");
                database.CompletePublication(key);
                return new RecoveryResolution(key, "committed-journal-cleared", null, null);
            }
            if (state == "not-written" && evidence is null)
            {
                database.CompletePublication(key);
                return new RecoveryResolution(key, "unwritten-journal-cleared", null, null);
            }
            if (evidence is null)
            {
                // A unique immutable copy is hash-verified BEFORE an evidence row
                // is committed. If anything fails, the original remains intact.
                evidence = await store.ArchiveOrphanAsync(key, ct);
                database.PrepareQuarantine(evidence);
            }
            // Even after a crash between the conditional delete and journal commit,
            // the prepared evidence remains and is independently hash-verified.
            await store.VerifyArchivedAsync(evidence, ct);
            if (recovery.MetadataCommitted(pending))
                throw new InvalidOperationException("Metadata became committed; refusing quarantine deletion");
            await store.DeleteOriginalConditionallyAsync(evidence, ct);
            if (store.Contains(key))
                throw new InvalidOperationException("Original object remains visible after conditional delete");
            database.FinishQuarantine(key);
            return new RecoveryResolution(key, "quarantined-recoverable", evidence.RecoveryKey, evidence.Sha256);
        }
        finally { _gate.Release(); }
    }
}
