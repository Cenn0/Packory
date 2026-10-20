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

    private sealed class Host : WebApplicationFactory<Program>
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
                    MaxArtifactBytes = 1024 * 1024
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
        foreach (var kind in new[] { "go", "npm", "oci" })
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
