using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class PackoryTelemetryTests
{
    [Fact]
    public async Task RequestsEmitOnlyRouteTemplatesAndAdminTotalsRequirePrivileges()
    {
        var measurements = new ConcurrentBag<(string Name, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, target) =>
        {
            if (instrument.Meter.Name == "Packory") target.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => measurements.Add((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => measurements.Add((instrument.Name, tags.ToArray())));
        listener.Start();
        using var host = new FeedEcosystemHost();
        using var client = host.CreateClient();
        const string confidential = "synthetic-sensitive-name";
        Assert.Equal(HttpStatusCode.Unauthorized, await FeedEcosystemHost.Status(client, HttpMethod.Get,
            "/feeds/" + confidential + "/raw/" + confidential + "/1.0"));
        Assert.Equal(HttpStatusCode.NotFound, await FeedEcosystemHost.Status(client, HttpMethod.Get, "/" + confidential));
        Assert.Equal(HttpStatusCode.Unauthorized, await FeedEcosystemHost.Status(client, HttpMethod.Get, "/api/admin/metrics"));
        Assert.Equal(HttpStatusCode.Forbidden, await FeedEcosystemHost.Status(client, HttpMethod.Get, "/api/admin/metrics", FeedEcosystemHost.Reader));
        using var response = await client.SendAsync(FeedEcosystemHost.Request(HttpMethod.Get, "/api/admin/metrics", FeedEcosystemHost.Admin));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("in-process-since-start", result.RootElement.GetProperty("scope").GetString());
        Assert.True(result.RootElement.GetProperty("authorizationDenied").GetInt64() >= 3);
        Assert.True(result.RootElement.GetProperty("requests").GetInt64() >= 4);
        Assert.Contains(measurements, row => row.Name == "packory.auth.denials");
        Assert.Contains(measurements, row => row.Name == "packory.http.duration");
        Assert.Contains(measurements, row => row.Tags.Any(t => t.Key == "http.route" && Equals(t.Value, "unmatched")));
        foreach (var row in measurements)
        foreach (var tag in row.Tags)
        {
            Assert.Contains(tag.Key, new[] { "http.route", "http.request.method", "http.response.status_class" });
            Assert.DoesNotContain(confidential, tag.Value?.ToString() ?? "");
        }
    }
}
