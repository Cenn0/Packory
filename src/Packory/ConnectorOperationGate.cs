using System.Security.Cryptography;
using System.Text;

namespace Packory;

/// <summary>Bounded single-writer coordination. A successful configuration change follows prior calls.</summary>
public sealed class ConnectorOperationGate : IDisposable
{
    private readonly SemaphoreSlim[] stripes = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1)).ToArray();
    public async Task<IDisposable> EnterAsync(string id, CancellationToken cancellationToken)
    {
        var stripe = stripes[SHA256.HashData(Encoding.UTF8.GetBytes(id))[0] % stripes.Length];
        if (!await stripe.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken))
            throw new ConnectorBusyException();
        return new Lease(stripe);
    }
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? held = semaphore;
        public void Dispose() => Interlocked.Exchange(ref held, null)?.Release();
    }
    public void Dispose() { foreach (var stripe in stripes) stripe.Dispose(); }
}

public sealed class ConnectorBusyException : Exception
{
    public ConnectorBusyException() : base("Connector is busy; retry after its current operation completes.") { }
}
