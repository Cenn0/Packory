namespace Packory;

/// <summary>
/// Bounded in-process keyed locking for immutable artifact publication and cold cache misses.
/// Stripes intentionally trade occasional unrelated contention for constant memory usage.
/// This is not a multi-node lock; sharing a data directory between processes is unsupported.
/// </summary>
public sealed class ArtifactLocks
{
    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, 256)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<IDisposable> AcquireAsync(string kind, string name, string version, CancellationToken ct)
    {
        var key = kind + "\0" + name + "\0" + version;
        var position = (int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_stripes.Length);
        var semaphore = _stripes[position];
        await semaphore.WaitAsync(ct);
        return new Lease(semaphore);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;
        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
