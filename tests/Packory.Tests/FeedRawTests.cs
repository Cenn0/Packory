using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class FeedRawTests
{
    private const string Admin = "synthetic-private-feed-admin-for-tests";
    private const string Reader = "synthetic-global-reader-for-tests";
    private const string Writer = "synthetic-global-writer-for-tests";

    private sealed class Host(long maxBytes = 0, long maxArtifacts = 0) : WebApplicationFactory<Program>
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(),
            "packory-feed-raw-tests-" + Guid.NewGuid().ToString("N"));
        public string Root => _data;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Packory:DataDirectory"] = _data,
                    ["Packory:PublicBaseUrl"] = "http://localhost:8080",
                    ["Packory:AdminToken"] = Admin,
                    ["Packory:ReadToken"] = Reader,
                    ["Packory:WriteToken"] = Writer,
                    ["Packory:AnonymousRead"] = "true"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                var cfg = new Settings
                {
                    DataDirectory = _data, PublicBaseUrl = "http://localhost:8080",
                    AdminToken = Admin, ReadToken = Reader, WriteToken = Writer,
                    AnonymousRead = true, McpEnabled = false,
                    MaxArtifactBytes = 1024 * 1024,
                    MaxRawFeedBytes = maxBytes, MaxRawFeedArtifacts = maxArtifacts
                };
                cfg.Validate();
                services.AddSingleton(cfg);
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_data))
                Directory.Delete(_data, true);
        }
    }

    private static HttpRequestMessage Req(HttpMethod method, string url,
        string? token = null, byte[]? bytes = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (bytes is not null)
        {
            req.Content = new ByteArrayContent(bytes);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        return req;
    }

    private static async Task<HttpStatusCode> Status(HttpClient client, HttpMethod method,
        string url, string? token = null, byte[]? bytes = null)
    {
        using var response = await client.SendAsync(Req(method, url, token, bytes));
        return response.StatusCode;
    }

    private static async Task CreateFeed(HttpClient client, string id)
    {
        using var request = Req(HttpMethod.Post, "/api/v1/admin/feeds/drafts", Admin);
        request.Content = JsonContent.Create(new
        {
            id, kind = "raw", mode = "hosted", displayName = "Synthetic private Raw"
        });
        using var draft = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
            "/api/v1/admin/feeds/" + id + "/activate", Admin));
    }

    private static async Task<(string Id, string Token)> Scoped(
        HttpClient client, string role, string scope)
    {
        using var request = Req(HttpMethod.Post, "/api/admin/tokens", Admin);
        request.Content = JsonContent.Create(new
        {
            role, @namespace = scope, expiresInHours = 1
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (document.RootElement.GetProperty("id").GetString()!,
            document.RootElement.GetProperty("token").GetString()!);
    }

    [Theory]
    [InlineData(10, 0, 3)]
    [InlineData(100, 2, 2)]
    public async Task ConcurrentWritesRespectIndependentCommittedFeedQuotas(long maxBytes, long maxArtifacts, int admitted)
    {
        using var host = new Host(maxBytes, maxArtifacts);
        using var client = host.CreateClient();
        await CreateFeed(client, "quota-alpha");
        await CreateFeed(client, "quota-beta");
        var alpha = await Scoped(client, "write", "feed:quota-alpha:publish");
        var reader = await Scoped(client, "read", "feed:quota-alpha:read");
        var beta = await Scoped(client, "write", "feed:quota-beta:publish");
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 10).Select(index => Status(client,
            HttpMethod.Put, "/feeds/quota-alpha/raw/item-" + index + "/1.0", alpha.Token, "abc"u8.ToArray())));
        Assert.Equal(admitted, outcomes.Count(x => x == HttpStatusCode.Created));
        Assert.Equal(10 - admitted, outcomes.Count(x => (int)x == 507));
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put,
            "/feeds/quota-beta/raw/independent/1.0", beta.Token, "abc"u8.ToArray()));
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        Assert.Equal(new RawFeedUsage(admitted, admitted * 3), db.RawFeedUsage("quota-alpha"));
        Assert.Equal(new RawFeedUsage(1, 3), db.RawFeedUsage("quota-beta"));
        Assert.Empty(db.PendingPublications());
        var reopened = new Database(host.Services.GetRequiredService<Settings>());
        reopened.Initialize();
        Assert.Equal(new RawFeedUsage(admitted, admitted * 3), reopened.RawFeedUsage("quota-alpha"));
        const string usageUrl = "/feeds/quota-alpha/raw/usage";
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, HttpMethod.Get, usageUrl));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Get, usageUrl, beta.Token));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Get, usageUrl, Reader));
        using (var response = await client.SendAsync(Req(HttpMethod.Get, usageUrl, reader.Token)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(admitted * 3, json.RootElement.GetProperty("bytes").GetInt64());
            Assert.Equal(admitted, json.RootElement.GetProperty("artifacts").GetInt64());
            Assert.Equal("retain-immutable-no-online-delete", json.RootElement.GetProperty("retention").GetString());
        }
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
            "/api/v1/admin/feeds/quota-alpha/deactivate", Admin));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, HttpMethod.Get, usageUrl, reader.Token));
    }

    [Fact]
    public void QuotaMathAndConfigurationRejectNegativeAndOverflowInputs()
    {
        Assert.False(RawFeedQuota.Admit(new RawFeedUsage(1, long.MaxValue), 10,
            new Settings { MaxRawFeedBytes = 10 }));
        Assert.False(RawFeedQuota.Admit(new RawFeedUsage(-1, 0), 1, new Settings()));
        Assert.False(RawFeedQuota.Admit(new RawFeedUsage(0, 0), 0, new Settings()));
        Assert.True(RawFeedQuota.Admit(new RawFeedUsage(0, 0), 10, new Settings { MaxRawFeedBytes = 10 }));
        Assert.False(RawFeedQuota.Admit(new RawFeedUsage(1, 10), 1, new Settings { MaxRawFeedBytes = 10 }));
        Assert.Throws<InvalidOperationException>(() => new Settings { MaxRawFeedBytes = -1 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new Settings { MaxRawFeedArtifacts = -1 }.Validate());
    }

    [Fact]
    public void PostgreSqlRawUsageIsBoundToExactFeedAndProtocol()
    {
        var connection = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var db = new PostgresDatabase(new Settings { MetadataProvider = "postgresql", PostgresConnectionString = connection });
        db.Initialize();
        var id = "raw-quota-" + Guid.NewGuid().ToString("N")[..12];
        var at = DateTimeOffset.UtcNow.ToString("O");
        Assert.True(db.TryCreateDraftFeed(new FeedDefinition(id, "raw", "hosted", "private", "Synthetic", false, at, at), "fixture"));
        Assert.True(db.TrySetHostedFeedEnabled(id, "raw", true, "fixture"));
        Assert.Equal(new RawFeedUsage(0, 0), db.RawFeedUsage(id));
        foreach (var size in new long[] { 7, 13 })
            Assert.True(db.PublishFeedAsset(new FeedAssetEntry(id, "raw", "item-" + size + "/1.0", "item-" + size, "1.0",
                "application/octet-stream", new string('a', 64), size, at), "fixture"));
        Assert.Equal(new RawFeedUsage(2, 20), db.RawFeedUsage(id));
        Assert.Equal(new RawFeedUsage(0, 0), db.RawFeedUsage("missing-feed"));
    }

    [Fact]
    public async Task SameCoordinatesHaveIndependentPrivateBytesAndExactGrants()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        await CreateFeed(client, "alpha-feed");
        await CreateFeed(client, "beta-feed");
        var alphaReader = await Scoped(client, "read", "feed:alpha-feed:read");
        var alphaWriter = await Scoped(client, "write", "feed:alpha-feed:publish");
        var betaReader = await Scoped(client, "read", "feed:beta-feed:read");
        var betaWriter = await Scoped(client, "write", "feed:beta-feed:publish");
        var a = "/feeds/alpha-feed/raw/same.name/1.0.0";
        var b = "/feeds/beta-feed/raw/same.name/1.0.0";
        var alpha = "immutable alpha bytes"u8.ToArray();
        var beta = "immutable beta bytes"u8.ToArray();

        Assert.Equal(HttpStatusCode.Unauthorized,
            await Status(client, HttpMethod.Get, a));
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Get, a, Reader));
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Get, a, betaReader.Token));
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Put, b, alphaWriter.Token, alpha));
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Put, a, alphaReader.Token, alpha));
        Assert.Equal(HttpStatusCode.Created,
            await Status(client, HttpMethod.Put, a, alphaWriter.Token, alpha));
        Assert.Equal(HttpStatusCode.Created,
            await Status(client, HttpMethod.Put, b, betaWriter.Token, beta));
        Assert.Equal(HttpStatusCode.Conflict,
            await Status(client, HttpMethod.Put, a, alphaWriter.Token, beta));
        using (var response = await client.SendAsync(Req(HttpMethod.Get, a, alphaReader.Token)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(alpha, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("\"" + Convert.ToHexStringLower(SHA256.HashData(alpha)) + "\"",
                response.Headers.ETag?.ToString());
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        }
        using (var response = await client.SendAsync(Req(HttpMethod.Get, b, betaReader.Token)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(beta, await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Get, a + "/metadata", betaReader.Token));
        Assert.Equal(HttpStatusCode.OK,
            await Status(client, HttpMethod.Head, a, alphaReader.Token));
        Assert.Equal(HttpStatusCode.NotFound,
            await Status(client, HttpMethod.Get, "/raw/same.name/1.0.0"));
        using (var catalog = await client.GetAsync("/api/packages?kind=raw"))
            Assert.DoesNotContain("same.name", await catalog.Content.ReadAsStringAsync());

        var db = host.Services.GetRequiredService<IPackageDatabase>();
        Assert.Equal(2, db.FeedAssets().Count);
        Assert.Empty(db.PendingPublications());
        Assert.Equal(2, db.FeedAssets().Select(FeedAssetEntry.BlobKey).Distinct().Count());
        Assert.False(db.TryDeleteDraftFeed("alpha-feed", "synthetic"));

        var recovery = host.Services.GetRequiredService<ArtifactRecovery>();
        Assert.Equal(0, recovery.MissingHostedCount());
        Assert.Equal(0, recovery.Reconcile().MissingHostedArtifacts);
        var scanner = host.Services.GetRequiredService<ArtifactIntegrityScanner>();
        var results = await scanner.ScanAsync(0, 10, CancellationToken.None);
        Assert.Equal(2, results.Items.Count(x => x.Kind == "feed-raw" &&
            x.State == "digest_match"));

        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
            "/api/v1/admin/feeds/alpha-feed/deactivate", Admin));
        Assert.Equal(HttpStatusCode.NotFound,
            await Status(client, HttpMethod.Get, a, alphaReader.Token));
        Assert.Equal(HttpStatusCode.OK,
            await Status(client, HttpMethod.Get, b, betaReader.Token));
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
            "/api/v1/admin/feeds/alpha-feed/activate", Admin));
        Assert.Equal(HttpStatusCode.OK,
            await Status(client, HttpMethod.Get, a, alphaReader.Token));

        Assert.Equal(HttpStatusCode.NoContent, await Status(client, HttpMethod.Delete,
            "/api/admin/tokens/" + alphaReader.Id, Admin));
        Assert.Equal(HttpStatusCode.Unauthorized,
            await Status(client, HttpMethod.Get, a, alphaReader.Token));
        Assert.Equal(HttpStatusCode.OK,
            await Status(client, HttpMethod.Get, b, betaReader.Token));
    }

    [Fact]
    public async Task MissingOrTamperedPrivateRawNeverFallsBackToGlobalStorage()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        await CreateFeed(client, "delta-feed");
        var writer = await Scoped(client, "write", "feed:delta-feed:publish");
        var reader = await Scoped(client, "read", "feed:delta-feed:read");
        var path = "/feeds/delta-feed/raw/same.name/1.0.0";
        var content = "trusted payload"u8.ToArray();
        Assert.Equal(HttpStatusCode.NotFound,
            await Status(client, HttpMethod.Get, path, reader.Token));
        Assert.Equal(HttpStatusCode.Created,
            await Status(client, HttpMethod.Put, path, writer.Token, content));
        // Even an administrator or global-reader token does not expose private
        // bytes through legacy Raw URLs.
        Assert.Equal(HttpStatusCode.NotFound,
            await Status(client, HttpMethod.Get, "/raw/same.name/1.0.0", Reader));
        await File.WriteAllTextAsync(Path.Combine(host.Root, "blobs", "feeds",
            "delta-feed", "raw", "same.name", "1.0.0", "payload.blob"), "corrupted");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            await Status(client, HttpMethod.Get, path, reader.Token));
        var scanner = host.Services.GetRequiredService<ArtifactIntegrityScanner>();
        var result = await scanner.ScanAsync(0, 10, CancellationToken.None);
        Assert.Contains(result.Items, x => x.Kind == "feed-raw" &&
            x.FeedId == "delta-feed" && x.State == "digest_mismatch");
    }

    [Fact]
    public async Task FutureUnimplementedFeedKindsRemainNonOperational()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        foreach (var kind in new[] { "skill" })
        {
            var id = "draft-" + kind;
            using var create = Req(HttpMethod.Post, "/api/v1/admin/feeds/drafts", Admin);
            create.Content = JsonContent.Create(new
            {
                id, kind, mode = "hosted", displayName = "Synthetic draft"
            });
            using var response = await client.SendAsync(create);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, await Status(client,
                HttpMethod.Post, "/api/v1/admin/feeds/" + id + "/activate", Admin));
        }
    }
}
