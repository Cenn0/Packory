namespace Packory;

/// <summary>HttpClient.Timeout ends at response headers for streamed requests.
/// Keep the same deadline active while reading and validating the body.</summary>
internal static class UpstreamDeadline
{
    public static CancellationTokenSource Start(HttpClient client, CancellationToken caller)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        if (client.Timeout != Timeout.InfiniteTimeSpan) deadline.CancelAfter(client.Timeout);
        return deadline;
    }
}
