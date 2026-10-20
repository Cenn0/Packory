using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Packory;
using Xunit;

namespace Packory.Tests;

/// <summary>
/// A feed or publish-namespace grant must never become a global read grant.
/// These routes cover independent native protocol handlers, not just catalog
/// or MCP endpoints which already have their own scope boundary.
/// </summary>
public sealed class ScopedGlobalRegistryReadTests
{
    private const string Admin = "synthetic-admin-credential-for-tests-only";
    private const string Reader = "synthetic-reader-credential-for-tests-only";
    private const string Writer = "synthetic-writer-credential-for-tests-only";

    private sealed class Host(bool anonymousRead = false) : WebApplicationFactory<Program>
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(),
            "packory-read-boundary-" + Guid.NewGuid().ToString("N"));

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
                    ["Packory:AnonymousRead"] = anonymousRead.ToString()
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                var settings = new Settings
                {
                    DataDirectory = _data, PublicBaseUrl = "http://localhost:8080",
                    AdminToken = Admin, ReadToken = Reader, WriteToken = Writer,
                    AnonymousRead = anonymousRead, McpEnabled = false,
                    MaxArtifactBytes = 1024 * 1024
                };
                settings.Validate();
                services.AddSingleton(settings);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_data))
                Directory.Delete(_data, recursive: true);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path,
        string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<HttpStatusCode> Status(HttpClient client,
        HttpMethod method, string path, string token, HttpContent? content = null)
    {
        using var request = Request(method, path, token, content);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static string AddToken(IPackageDatabase db, string role, string? grant,
        char character)
    {
        var token = new string(character, 64);
        var digest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        db.CreateManagedToken(new ManagedTokenRecord(
            Guid.NewGuid().ToString("N"), digest, role, grant,
            DateTimeOffset.UtcNow.AddHours(1).ToString("O")));
        return token;
    }

    [Fact]
    public async Task FeedAndNamespaceCredentialsCannotReadAnyGlobalNativeRegistry()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var feedReader = AddToken(db, "read", "feed:alpha-feed:read", 'a');
        var feedPublisher = AddToken(db, "write", "feed:alpha-feed:publish", 'b');
        var namespacePublisher = AddToken(db, "write", "raw:example", 'c');
        var globalReader = AddToken(db, "read", null, 'd');

        // Routes with and without existing artifacts must reject a scoped
        // credential before potentially exposing listing or blob content.
        var globalRoutes = new[]
        {
            "/nuget/v3/index.json",
            "/nuget/v3/search?q=anything",
            "/npm/-/v1/search?text=anything",
            "/cargo/index/config.json",
            "/pypi/simple/",
            "/v2/",
            "/api/packages",
            "/raw/example/1.0/metadata",
            "/go/example.com/example/@v/list",
            "/maven/org/example/demo/maven-metadata.xml"
        };
        foreach (var route in globalRoutes)
        {
            foreach (var scope in new[] { feedReader, feedPublisher, namespacePublisher })
            {
                var denied = await Status(client, HttpMethod.Get, route, scope);
                Assert.True(denied == HttpStatusCode.Forbidden,
                    $"Scoped token unexpectedly accessed global route {route}: {(int)denied}");
            }

            var global = await Status(client, HttpMethod.Get, route, globalReader);
            Assert.NotEqual(HttpStatusCode.Forbidden, global);
            Assert.NotEqual(HttpStatusCode.Unauthorized, global);
            var staticRead = await Status(client, HttpMethod.Get, route, Reader);
            Assert.NotEqual(HttpStatusCode.Forbidden, staticRead);
            Assert.NotEqual(HttpStatusCode.Unauthorized, staticRead);
        }
    }

    [Fact]
    public async Task PrivateNugetFeedStillAcceptsOnlyExactActiveFeedGrants()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var at = DateTimeOffset.UtcNow.ToString("O");
        foreach (var feedId in new[] { "alpha-feed", "beta-feed" })
        {
            Assert.True(db.TryCreateDraftFeed(
                new FeedDefinition(feedId, "nuget", "hosted", "private",
                    "Synthetic " + feedId, false, at, at), "synthetic"));
            Assert.True(db.TryActivateHostedNugetFeed(feedId, "synthetic"));
        }

        var alphaReader = AddToken(db, "read", "feed:alpha-feed:read", 'e');
        var alphaWriter = AddToken(db, "write", "feed:alpha-feed:publish", 'f');
        var betaReader = AddToken(db, "read", "feed:beta-feed:read", '1');
        var alphaIndex = "/feeds/alpha-feed/nuget/v3/index.json";
        var betaIndex = "/feeds/beta-feed/nuget/v3/index.json";
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Get, alphaIndex, alphaReader));
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Get, alphaIndex, alphaWriter));
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Get, betaIndex, betaReader));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Get, betaIndex, alphaReader));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Get, alphaIndex, betaReader));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Get, alphaIndex, Reader));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Get,
            "/nuget/v3/index.json", alphaReader));

        Assert.True(db.TryDeactivateHostedNugetFeed("alpha-feed", "synthetic"));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, HttpMethod.Get, alphaIndex, alphaReader));
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Get, betaIndex, betaReader));
    }

    [Fact]
    public async Task ScopedPublisherMayWriteGrantedArtifactButCannotUseGlobalReads()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var rawPublisher = AddToken(db, "write", "raw:allowed", '2');

        static ByteArrayContent Bytes()
        {
            var content = new ByteArrayContent("synthetic payload"u8.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return content;
        }

        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Put, "/raw/denied/1.0", rawPublisher, Bytes()));
        Assert.Equal(HttpStatusCode.Created,
            await Status(client, HttpMethod.Put, "/raw/allowed/1.0", rawPublisher, Bytes()));
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Get, "/raw/allowed/1.0", rawPublisher));
        Assert.Equal(HttpStatusCode.OK,
            await Status(client, HttpMethod.Get, "/raw/allowed/1.0", Reader));
    }

    [Fact]
    public async Task ExplicitAnonymousLegacyReadStillAllowsPublicNativeDiscovery()
    {
        using var host = new Host(anonymousRead: true);
        using var client = host.CreateClient();
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var scoped = AddToken(db, "read", "feed:alpha-feed:read", '3');
        Assert.Equal(HttpStatusCode.OK,
            await Status(client, HttpMethod.Get, "/nuget/v3/index.json", scoped));
        // Private feeds still do NOT honor the anonymous legacy read setting.
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Get,
                "/feeds/alpha-feed/nuget/v3/index.json", scoped));
    }
}
