using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Packory;

/// <summary>Bounded in-process observations; no user/feed/package identifiers.</summary>
public sealed class PackoryTelemetry : IDisposable
{
    private readonly Meter _meter = new("Packory", "1.0");
    private readonly Counter<long> _requests;
    private readonly Counter<long> _denials;
    private readonly Histogram<double> _duration;
    private long _completed, _active, _denied, _serverErrors, _limited;

    public PackoryTelemetry()
    {
        _requests = _meter.CreateCounter<long>("packory.http.requests", "{request}");
        _denials = _meter.CreateCounter<long>("packory.auth.denials", "{request}");
        _duration = _meter.CreateHistogram<double>("packory.http.duration", "s");
        _meter.CreateObservableGauge("packory.http.active", () => Interlocked.Read(ref _active), "{request}");
    }

    public object Snapshot() => new
    {
        scope = "in-process-since-start", requests = Interlocked.Read(ref _completed),
        active = Interlocked.Read(ref _active), authorizationDenied = Interlocked.Read(ref _denied),
        serverErrors = Interlocked.Read(ref _serverErrors), rateLimited = Interlocked.Read(ref _limited)
    };

    public async Task Observe(HttpContext context, RequestDelegate next)
    {
        var started = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _active);
        var exceptionStatus = 0;
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        { exceptionStatus = 499; throw; }
        catch { exceptionStatus = 500; throw; }
        finally
        {
            var status = exceptionStatus == 0 ? context.Response.StatusCode : exceptionStatus;
            var template = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            var route = template is { Length: <= 256 } ? template : "unmatched";
            var method = context.Request.Method is "GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS"
                ? context.Request.Method : "OTHER";
            var statusClass = status is >= 100 and <= 599 ? (status / 100) + "xx" : "other";
            var tags = new TagList { { "http.route", route }, { "http.request.method", method }, { "http.response.status_class", statusClass } };
            Interlocked.Decrement(ref _active);
            Interlocked.Increment(ref _completed);
            _requests.Add(1, tags);
            _duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
            if (status is 401 or 403)
            {
                Interlocked.Increment(ref _denied);
                _denials.Add(1, tags);
            }
            if (status >= 500) Interlocked.Increment(ref _serverErrors);
            if (status == 429) Interlocked.Increment(ref _limited);
        }
    }

    public void Dispose() => _meter.Dispose();
}
