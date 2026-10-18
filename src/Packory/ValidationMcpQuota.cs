using System.Threading.RateLimiting;

namespace Packory;

/// <summary>
/// A deliberately global, zero-queue defensive cap for the optional, costly
/// signature-verification MCP tools. Auth still happens before quota use.
/// The fixed cap also bounds memory: no attacker-controlled partition keys.
/// </summary>
public sealed class ValidationMcpQuota : IDisposable
{
    private readonly FixedWindowRateLimiter _limiter = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = 60,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true
    });

    public bool TryAcquire()
    {
        using var lease = _limiter.AttemptAcquire();
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
