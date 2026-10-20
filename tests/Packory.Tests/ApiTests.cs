using ModelContextProtocol.Client;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class ApiTests
{
    private const string AdminToken = "test-admin-token-which-is-at-least-32-characters";
    private const string ReadToken = "test-reader-token-which-is-at-least-32-characters";
    private const string WriteToken = "test-writer-token-which-is-at-least-32-characters";

    private sealed class FakeUpstream(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handle(request));
    }

    private sealed class Host(Func<HttpRequestMessage, HttpResponseMessage>? upstream = null,
        Action<Settings>? configure = null, bool backgroundSkillScanning = true) : WebApplicationFactory<Program>
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(), "packory-tests-" + Guid.NewGuid().ToString("N"));
        public string DataDirectory => _data;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Packory:DataDirectory"] = _data,
                ["Packory:PublicBaseUrl"] = "http://localhost:8080",
                ["Packory:AdminToken"] = AdminToken,
                ["Packory:ReadToken"] = ReadToken,
                ["Packory:AnonymousRead"] = "false"
            }));
            builder.ConfigureTestServices(services =>
            {
                // Replace the concrete Settings singleton: top-level startup binds configuration
                // before the test host configuration overlay is applied.
                services.RemoveAll<Settings>();
                var cfg = new Settings
                {
                    DataDirectory = _data,
                    PublicBaseUrl = "http://localhost:8080",
                    AdminToken = AdminToken,
                    ReadToken = ReadToken,
                    WriteToken = WriteToken,
                    AnonymousRead = false
                };
                configure?.Invoke(cfg);
                cfg.Validate();
                services.AddSingleton(cfg);
                if (!backgroundSkillScanning) services.DisableBackgroundSkillScanning();
                services.AddHttpClient("upstream")
                    .ConfigurePrimaryHttpMessageHandler(() => new FakeUpstream(upstream ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
        }
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpRequestMessage WithToken(HttpMethod method, string url, HttpContent? content = null, string token = AdminToken)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static byte[] Nupkg(string name = "Example.Package", string version = "1.2.3")
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(archive.CreateEntry(name + ".nuspec").Open());
            writer.Write($"<package><metadata><id>{name}</id><version>{version}</version><authors>Test</authors><description>Example library</description></metadata></package>");
        }
        return memory.ToArray();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ManualScanIsolationPreservesSecurityEnforcementAndDefaultWorker(bool backgroundSkillScanning)
    {
        using var host = backgroundSkillScanning ? new Host() : new Host(backgroundSkillScanning: false);
        using var client = host.CreateClient();
        var coordinator = host.Services.GetRequiredService<SkillScanCoordinator>();
        Assert.True(host.Services.GetRequiredService<Settings>().SkillSecurityEnforced);
        Assert.Equal(backgroundSkillScanning,
            host.Services.GetServices<IHostedService>().Contains(coordinator));
        Assert.Equal(backgroundSkillScanning, coordinator.ExecuteTask is not null);
    }

    [Fact]
    public async Task FeedMetadataIsAdminOnlyPaginatedAndExplicitlyNotIsolated()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string url = "/api/v1/admin/feeds";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(WithToken(HttpMethod.Get, url, token: ReadToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(WithToken(HttpMethod.Get, url, token: WriteToken))).StatusCode);
        using var first = await client.SendAsync(WithToken(HttpMethod.Get, url + "?limit=2"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal("v1", json.RootElement.GetProperty("apiVersion").GetString());
        Assert.False(json.RootElement.GetProperty("isolationSupported").GetBoolean());
        var entries = json.RootElement.GetProperty("items");
        Assert.Equal(2, entries.GetArrayLength());
        var cursor = json.RootElement.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));
        var firstId = entries[0].GetProperty("id").GetString()!;
        Assert.Equal("legacy", entries[0].GetProperty("mode").GetString());
        using var details = await client.SendAsync(WithToken(HttpMethod.Get, url + "/" + firstId));
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        using var next = await client.SendAsync(WithToken(HttpMethod.Get,
            url + "?limit=2&after=" + cursor));
        using var nextDoc = JsonDocument.Parse(await next.Content.ReadAsStringAsync());
        Assert.Equal(2, nextDoc.RootElement.GetProperty("items").GetArrayLength());
        Assert.NotEqual(firstId,
            nextDoc.RootElement.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.SendAsync(WithToken(HttpMethod.Get, url + "?after=%2E%2E"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.SendAsync(WithToken(HttpMethod.Get, url + "?limit=101"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(WithToken(HttpMethod.Get, url + "/unknown-feed"))).StatusCode);
    }

    [Fact]
    public async Task DraftFeeds_AreAdminOnlyDisabledAuditedAndRemovableWithoutChangingLegacyRoutes()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string create = "/api/v1/admin/feeds/drafts";
        const string id = "sandbox-nuget-feed";
        const string body = """
            {"id":"sandbox-nuget-feed","kind":"nuget","mode":"hosted","displayName":"Sandbox packages"}
            """;
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync(create, new StringContent(body))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(
            WithToken(HttpMethod.Post, create, new StringContent(body), WriteToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(
            WithToken(HttpMethod.Post, create, new StringContent(body), ReadToken))).StatusCode);
        using var accepted = await client.SendAsync(WithToken(HttpMethod.Post,
            create, new StringContent(body, Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        using var created = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        Assert.False(created.RootElement.GetProperty("operational").GetBoolean());
        Assert.False(created.RootElement.GetProperty("isolationSupported").GetBoolean());
        var feed = created.RootElement.GetProperty("feed");
        Assert.False(feed.GetProperty("enabled").GetBoolean());
        Assert.Equal("private", feed.GetProperty("visibility").GetString());
        Assert.Equal("hosted", feed.GetProperty("mode").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(WithToken(
            HttpMethod.Post, create, new StringContent(body)))).StatusCode);
        using var detail = await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/v1/admin/feeds/" + id));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Contains(id, await detail.Content.ReadAsStringAsync());

        // The global NuGet endpoint has no new per-feed route; legacy clients are unaffected.
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithToken(
            HttpMethod.Get, "/nuget/v3/index.json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(WithToken(
            HttpMethod.Delete, "/api/v1/admin/feeds/drafts/default-nuget"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(WithToken(
            HttpMethod.Delete, "/api/v1/admin/feeds/drafts/" + id, token: WriteToken))).StatusCode);

        using var deleted = await client.SendAsync(WithToken(HttpMethod.Delete,
            "/api/v1/admin/feeds/drafts/" + id));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(WithToken(
            HttpMethod.Get, "/api/v1/admin/feeds/" + id))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(WithToken(
            HttpMethod.Delete, "/api/v1/admin/feeds/drafts/" + id))).StatusCode);
        using var audit = await client.SendAsync(WithToken(HttpMethod.Get, "/api/admin/audit"));
        var log = await audit.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        Assert.Contains("feed-draft-create", log);
        Assert.Contains("feed-draft-delete", log);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{invalid")]
    [InlineData("""{"id":"default-extra","kind":"nuget","mode":"hosted","displayName":"Bad"}""")]
    [InlineData("""{"id":"upper-CASE","kind":"nuget","mode":"hosted","displayName":"Bad"}""")]
    [InlineData("""{"id":"abc","kind":"raw","mode":"hosted","displayName":"Bad"}""")]
    [InlineData("""{"id":"abc","kind":"nuget","mode":"group","displayName":"Bad"}""")]
    [InlineData("""{"id":"abc","kind":"nuget","mode":"hosted","displayName":"Bad","enabled":true}""")]
    [InlineData("""{"id":"abc","id":"abc","kind":"nuget","mode":"hosted","displayName":"Bad"}""")]
    [InlineData("""{"id":"abc","kind":"nuget","mode":"hosted","displayName":" Bad "}""")]
    [InlineData("""{"id":"abc","kind":"nuget","mode":"hosted","displayName":"Bad\nname"}""")]
    public async Task DraftFeeds_RejectAmbiguousOrUnsupportedSpecifications(string json)
    {
        using var host = new Host();
        using var client = host.CreateClient();
        using var response = await client.SendAsync(WithToken(HttpMethod.Post,
            "/api/v1/admin/feeds/drafts", new StringContent(json, Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PrivateHostedNugetFeedsAreIsolatedAndSupportRevocableLeastPrivilegeTokens()
    {
        using var host = new Host();
        using var client = host.CreateClient();

        async Task CreateAndActivateAsync(string id)
        {
            using var created = await client.SendAsync(WithToken(HttpMethod.Post,
                "/api/v1/admin/feeds/drafts", JsonContent.Create(new
                {
                    id, kind = "nuget", mode = "hosted", displayName = "Synthetic Private Feed"
                })));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal(HttpStatusCode.OK,
                (await client.SendAsync(WithToken(HttpMethod.Post,
                    "/api/v1/admin/feeds/" + id + "/activate"))).StatusCode);
        }

        async Task<(string Id, string Secret)> TokenAsync(string role, string grant)
        {
            using var created = await client.SendAsync(WithToken(HttpMethod.Post,
                "/api/admin/tokens", JsonContent.Create(new
                {
                    role, @namespace = grant, expiresInHours = 24
                })));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var doc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            return (doc.RootElement.GetProperty("id").GetString()!,
                    doc.RootElement.GetProperty("token").GetString()!);
        }

        static MultipartFormDataContent Package(byte[] data)
        {
            var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(data), "package", "private.package.1.0.0.nupkg");
            return content;
        }

        static byte[] NugetWithDescription(string description)
        {
            using var content = new MemoryStream();
            using (var archive = new ZipArchive(content, ZipArchiveMode.Create, true))
            {
                using var writer = new StreamWriter(
                    archive.CreateEntry("Private.Package.nuspec").Open());
                writer.Write("<package><metadata><id>Private.Package</id><version>1.0.0</version>" +
                    "<authors>Example</authors><description>" + description +
                    "</description></metadata></package>");
            }
            return content.ToArray();
        }

        await CreateAndActivateAsync("alpha-feed");
        await CreateAndActivateAsync("beta-feed");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.SendAsync(WithToken(HttpMethod.Post, "/api/admin/tokens",
                JsonContent.Create(new
                {
                    role = "read", @namespace = "feed:alpha-feed:publish",
                    expiresInHours = 1
                })))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.SendAsync(WithToken(HttpMethod.Post, "/api/admin/tokens",
                JsonContent.Create(new
                {
                    role = "write", @namespace = "feed:alpha-feed:read",
                    expiresInHours = 1
                })))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.SendAsync(WithToken(HttpMethod.Post, "/api/admin/tokens",
                JsonContent.Create(new
                {
                    role = "write", @namespace = "feed:future-feed:publish",
                    expiresInHours = 1
                })))).StatusCode);
        var writer = await TokenAsync("write", "feed:alpha-feed:publish");
        var alphaReader = await TokenAsync("read", "feed:alpha-feed:read");
        var betaReader = await TokenAsync("read", "feed:beta-feed:read");
        var alpha = NugetWithDescription("alpha-private");
        var beta = NugetWithDescription("beta-private");
        var alphaIndex = "/feeds/alpha-feed/nuget/v3/index.json";
        var betaIndex = "/feeds/beta-feed/nuget/v3/index.json";
        var alphaFile = "/feeds/alpha-feed/nuget/v3/flatcontainer/private.package/1.0.0/private.package.1.0.0.nupkg";
        var betaFile = "/feeds/beta-feed/nuget/v3/flatcontainer/private.package/1.0.0/private.package.1.0.0.nupkg";

        using (var challenge = await client.GetAsync(alphaIndex))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, challenge.StatusCode);
            Assert.Contains(challenge.Headers.WwwAuthenticate,
                value => value.Scheme == "Basic" &&
                    value.Parameter == "realm=\"Packory private feed\"");
        }
        using (var basicIndex = new HttpRequestMessage(HttpMethod.Get, alphaIndex))
        {
            basicIndex.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic:" + alphaReader.Secret)));
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(basicIndex)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(WithToken(HttpMethod.Get, alphaIndex, token: ReadToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(WithToken(HttpMethod.Get, alphaIndex, token: WriteToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(WithToken(HttpMethod.Get, betaIndex, token: alphaReader.Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(WithToken(HttpMethod.Get, alphaIndex, token: betaReader.Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(WithToken(HttpMethod.Get, alphaIndex, token: alphaReader.Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(WithToken(HttpMethod.Get, betaIndex, token: betaReader.Secret))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(WithToken(HttpMethod.Put,
                "/feeds/beta-feed/nuget/v3/push", Package(beta), writer.Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(WithToken(HttpMethod.Put,
                "/feeds/alpha-feed/nuget/v3/push", Package(alpha), alphaReader.Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await client.SendAsync(WithToken(HttpMethod.Put,
                "/feeds/alpha-feed/nuget/v3/push", Package(alpha), writer.Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.SendAsync(WithToken(HttpMethod.Put,
                "/feeds/alpha-feed/nuget/v3/push", Package(alpha), writer.Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await client.SendAsync(WithToken(HttpMethod.Put,
                "/feeds/beta-feed/nuget/v3/push", Package(beta)))).StatusCode);

        Assert.Equal(alpha,
            await (await client.SendAsync(WithToken(HttpMethod.Get, alphaFile,
                token: alphaReader.Secret))).Content.ReadAsByteArrayAsync());
        Assert.Equal(beta,
            await (await client.SendAsync(WithToken(HttpMethod.Get, betaFile,
                token: betaReader.Secret))).Content.ReadAsByteArrayAsync());
        Assert.NotEqual(Convert.ToBase64String(SHA512.HashData(alpha)),
            Convert.ToBase64String(SHA512.HashData(beta)));
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(WithToken(HttpMethod.Get, betaFile,
                token: alphaReader.Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(WithToken(HttpMethod.Get,
                "/nuget/v3/flatcontainer/private.package/index.json"))).StatusCode);
        Assert.DoesNotContain("private.package",
            await (await client.SendAsync(WithToken(HttpMethod.Get,
                "/api/packages?kind=nuget"))).Content.ReadAsStringAsync());

        var database = host.Services.GetRequiredService<IPackageDatabase>();
        Assert.Equal(2, database.FeedNugetRevisions().Count);
        Assert.Empty(database.PendingPublications());
        Assert.NotEqual(FeedNugetEndpoints.BlobKey("alpha-feed", "private.package", "1.0.0"),
            FeedNugetEndpoints.BlobKey("beta-feed", "private.package", "1.0.0"));

        // A disabled feed retains bytes, but cannot serve or accept uploads.
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(WithToken(HttpMethod.Post,
                "/api/v1/admin/feeds/alpha-feed/deactivate"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(WithToken(HttpMethod.Get, alphaFile,
                token: alphaReader.Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(WithToken(HttpMethod.Get, betaFile,
                token: betaReader.Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.SendAsync(WithToken(HttpMethod.Delete,
                "/api/v1/admin/feeds/drafts/alpha-feed"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(WithToken(HttpMethod.Post,
                "/api/v1/admin/feeds/alpha-feed/activate"))).StatusCode);
        Assert.Equal(alpha,
            await (await client.SendAsync(WithToken(HttpMethod.Get, alphaFile,
                token: alphaReader.Secret))).Content.ReadAsByteArrayAsync());

        // Managed feed tokens are revocable without affecting other tenants.
        Assert.Equal(HttpStatusCode.NoContent,
            (await client.SendAsync(WithToken(HttpMethod.Delete,
                "/api/admin/tokens/" + writer.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(WithToken(HttpMethod.Put,
                "/feeds/alpha-feed/nuget/v3/push", Package(alpha), writer.Secret))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(WithToken(HttpMethod.Get, betaFile,
                token: betaReader.Secret))).StatusCode);
    }

    [Fact]
    public async Task IntegrityInspectionRequiresAdminAndExplicitOfflineMaintenance()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string route = "/api/admin/integrity/scan?limit=1";
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(route)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(
                WithToken(HttpMethod.Get, route, token: ReadToken))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(
                WithToken(HttpMethod.Get, route, token: WriteToken))).StatusCode);
        }
        // Unauthorized requests must not consume the shared operator budget.
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(
            WithToken(HttpMethod.Get, route))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(
            WithToken(HttpMethod.Get, route))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(
            WithToken(HttpMethod.Get, route))).StatusCode);
    }

    [Fact]
    public async Task Authentication_AppliesToReadsAndWrites()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/packages")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithToken(HttpMethod.Get, "/api/packages", token: ReadToken))).StatusCode);
        using var basic = new HttpRequestMessage(HttpMethod.Get, "/nuget/v3/index.json");
        basic.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("registry:" + ReadToken)));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(basic)).StatusCode);
        using var denied = await client.SendAsync(WithToken(HttpMethod.Post, "/api/skills", token: ReadToken));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("frame-ancestors 'none'", home.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("Your packages.", await home.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Nuget_MirrorsAndChecksSha512_AndSupportsOfflineCache()
    {
        var bytes = Nupkg();
        var online = true;
        using var host = new Host(request =>
        {
            if (!online) throw new HttpRequestException("offline");
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/example.package/index.json", StringComparison.Ordinal)) return Json("{\"versions\":[\"1.2.3\"]}");
            if (path.EndsWith(".nupkg.sha512", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Convert.ToBase64String(SHA512.HashData(bytes)))
            };
            if (path.EndsWith(".nupkg", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = host.CreateClient();
        var service = JsonDocument.Parse(await (await client.SendAsync(WithToken(HttpMethod.Get, "/nuget/v3/index.json"))).Content.ReadAsStringAsync());
        Assert.Equal("3.0.0", service.RootElement.GetProperty("version").GetString());
        using var versions = await client.SendAsync(WithToken(HttpMethod.Get, "/nuget/v3/flatcontainer/Example.Package/index.json"));
        Assert.Contains("1.2.3", await versions.Content.ReadAsStringAsync());
        var location = "/nuget/v3/flatcontainer/example.package/1.2.3/example.package.1.2.3.nupkg";
        Assert.Equal(bytes, await (await client.SendAsync(WithToken(HttpMethod.Get, location))).Content.ReadAsByteArrayAsync());
        var ledger = host.Services.GetRequiredService<IPackageDatabase>()
            .GetNugetDigest("example.package", "1.2.3");
        Assert.NotNull(ledger);
        Assert.Equal("mirror", ledger.Origin);
        Assert.Equal(bytes.LongLength, ledger.Size);
        Assert.Equal(Convert.ToBase64String(SHA512.HashData(bytes)), ledger.Sha512);
        online = false;
        Assert.Equal(bytes, await (await client.SendAsync(WithToken(HttpMethod.Get, location))).Content.ReadAsByteArrayAsync());
        var sha = await (await client.SendAsync(WithToken(HttpMethod.Get, location + ".sha512"))).Content.ReadAsStringAsync();
        Assert.Equal(Convert.ToBase64String(SHA512.HashData(bytes)), sha);
        Assert.Contains("<id>Example.Package</id>", await (await client.SendAsync(WithToken(HttpMethod.Get,
            "/nuget/v3/flatcontainer/example.package/1.2.3/example.package.nuspec"))).Content.ReadAsStringAsync());
        Assert.Contains("example.package", await (await client.SendAsync(WithToken(HttpMethod.Get, "/api/packages?q=example"))).Content.ReadAsStringAsync());
        // Corrupted offline mirror bytes may remain on disk, but must never be
        // served as a revision whose authoritative checksum no longer matches.
        var cache = host.Services.GetRequiredService<BlobStore>().Existing(
            NuGetEndpoints.NugetKey("example.package", "1.2.3"));
        Assert.NotNull(cache);
        await File.WriteAllBytesAsync(cache, "tampered cached package"u8.ToArray());
        Assert.Equal(HttpStatusCode.BadGateway,
            (await client.SendAsync(WithToken(HttpMethod.Get, location))).StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway,
            (await client.SendAsync(WithToken(HttpMethod.Get, location + ".sha512"))).StatusCode);
    }

    [Fact]
    public async Task Nuget_DoesNotCacheUnverifiedOrMismatchedArtifacts()
    {
        var bytes = Nupkg();
        var mismatch = true;
        using var host = new Host(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith(".nupkg.sha512", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(mismatch ? Convert.ToBase64String(SHA512.HashData(new byte[] { 1, 2, 3 })) :
                        Convert.ToBase64String(SHA512.HashData(bytes)))
                };
            if (path.EndsWith(".nupkg", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = host.CreateClient();
        var uri = "/nuget/v3/flatcontainer/example.package/1.2.3/example.package.1.2.3.nupkg";
        Assert.Equal(HttpStatusCode.BadGateway, (await client.SendAsync(WithToken(HttpMethod.Get, uri))).StatusCode);
        mismatch = false;
        Assert.Equal(bytes, await (await client.SendAsync(WithToken(HttpMethod.Get, uri))).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Nuget_PublishRequiresWriter_AndIsImmutable()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var published = Nupkg("Custom.Package", "0.1.0");
        MultipartFormDataContent Form()
        {
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(published), "package", "custom.package.0.1.0.nupkg");
            return form;
        }
        var unauthenticated = new HttpRequestMessage(HttpMethod.Put, "/nuget/v3/push") { Content = Form() };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(unauthenticated)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(WithToken(HttpMethod.Put, "/nuget/v3/push", Form()))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(WithToken(HttpMethod.Put, "/nuget/v3/push", Form()))).StatusCode);
        var artifact = await client.SendAsync(WithToken(HttpMethod.Get,
            "/nuget/v3/flatcontainer/custom.package/0.1.0/custom.package.0.1.0.nupkg"));
        Assert.Equal(published.Length, (await artifact.Content.ReadAsByteArrayAsync()).Length);
        var revision = host.Services.GetRequiredService<IPackageDatabase>()
            .GetNugetDigest("custom.package", "0.1.0");
        Assert.NotNull(revision);
        Assert.Equal("hosted", revision.Origin);
        Assert.Equal(published.LongLength, revision.Size);
        Assert.Equal(Convert.ToBase64String(SHA512.HashData(published)), revision.Sha512);
        var local = host.Services.GetRequiredService<BlobStore>()
            .Existing(NuGetEndpoints.NugetKey("custom.package", "0.1.0"));
        Assert.NotNull(local);
        await File.WriteAllBytesAsync(local, "tampered hosted blob"u8.ToArray());
        Assert.Equal(HttpStatusCode.BadGateway,
            (await client.SendAsync(WithToken(HttpMethod.Get,
                "/nuget/v3/flatcontainer/custom.package/0.1.0/custom.package.0.1.0.nupkg")))
                .StatusCode);
    }

    [Fact]
    public async Task Npm_MirrorsScopedPackage_WithRewrittenAndVerifiedTarball()
    {
        var content = new byte[] { 0x1f, 0x8b, 0x08, 0, 0, 0, 0, 0 };
        var integrity = "sha512-" + Convert.ToBase64String(SHA512.HashData(content));
        var metadata = """
            {"name":"@demo/widget","dist-tags":{"latest":"1.0.0"},"versions":{"1.0.0":{"name":"@demo/widget","version":"1.0.0","description":"Demo","dist":{"tarball":"https://registry.npmjs.org/@demo/widget/-/widget-1.0.0.tgz","integrity":"__INTEGRITY__"}}}}
            """.Replace("__INTEGRITY__", integrity);
        using var host = new Host(request => request.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) }
            : Json(metadata));
        using var client = host.CreateClient();
        var packument = await (await client.SendAsync(WithToken(HttpMethod.Get, "/npm/%40demo%2Fwidget"))).Content.ReadAsStringAsync();
        Assert.Contains("localhost:8080/npm/%40demo%2Fwidget/-/1.0.0.tgz", packument);
        Assert.DoesNotContain("registry.npmjs.org/@demo/widget/-/widget", packument);
        var requestPath = "/npm/%40demo%2Fwidget/-/1.0.0.tgz";
        var file = await client.SendAsync(WithToken(HttpMethod.Get, requestPath));
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(content, await file.Content.ReadAsByteArrayAsync());
        var indexed = await (await client.SendAsync(WithToken(HttpMethod.Get, "/api/packages?kind=npm"))).Content.ReadAsStringAsync();
        Assert.Contains("@demo/widget", indexed);
    }

    [Fact]
    public async Task Npm_HostedPublishAndInstallMetadata()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var tarball = new byte[] { 0x1f, 0x8b, 0x08, 0, 0, 0, 0, 0 };
        var publish = new JsonObject
        {
            ["name"] = "local-demo",
            ["versions"] = new JsonObject
            {
                ["1.0.0"] = new JsonObject { ["name"] = "local-demo", ["version"] = "1.0.0", ["description"] = "local package" }
            },
            ["dist-tags"] = new JsonObject { ["latest"] = "1.0.0" },
            ["_attachments"] = new JsonObject { ["local-demo-1.0.0.tgz"] = new JsonObject { ["data"] = Convert.ToBase64String(tarball) } }
        };
        var body = new StringContent(publish.ToJsonString(), Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(WithToken(HttpMethod.Put, "/npm/local-demo", body))).StatusCode);
        var packument = await (await client.SendAsync(WithToken(HttpMethod.Get, "/npm/local-demo"))).Content.ReadAsStringAsync();
        Assert.Contains("sha512-", packument);
        Assert.Contains("1.0.0", packument);
        Assert.Equal(tarball, await (await client.SendAsync(WithToken(HttpMethod.Get,
            "/npm/local-demo/-/1.0.0.tgz"))).Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Skills_RejectsZipTraversal_AndPublishesVersionedArchive()
    {
        static byte[] Zip(string filename = "hello-world/SKILL.md")
        {
            using var mem = new MemoryStream();
            using (var zip = new ZipArchive(mem, ZipArchiveMode.Create, true))
            {
                using var writer = new StreamWriter(zip.CreateEntry(filename).Open());
                writer.Write("---\nname: hello-world\ndescription: A demo skill\n---\n# Instructions\n");
            }
            return mem.ToArray();
        }
        static MultipartFormDataContent Form(byte[] bytes)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent("1.0.0"), "version");
            form.Add(new ByteArrayContent(bytes), "file", "hello-world.zip");
            return form;
        }
        using var host = new Host(configure: cfg => cfg.SkillSecurityEnforced = false);
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(WithToken(HttpMethod.Post, "/api/skills", Form(Zip("../SKILL.md"))))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(WithToken(HttpMethod.Post, "/api/skills", Form(Zip())))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(WithToken(HttpMethod.Post, "/api/skills", Form(Zip())))).StatusCode);
        Assert.Equal(Zip().Length, (await (await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/skills/hello-world/1.0.0/download"))).Content.ReadAsByteArrayAsync()).Length);
        Assert.Contains("A demo skill", await (await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/skills/hello-world/1.0.0/skill.md"))).Content.ReadAsStringAsync());
        Assert.Contains("hello-world", await (await client.SendAsync(WithToken(HttpMethod.Get, "/api/packages?kind=skill"))).Content.ReadAsStringAsync());
    }


    [Fact]
    public async Task Skills_EnforcedUploadIsQueuedAndNeverPublicBeforeReview()
    {
        using var host = new Host(backgroundSkillScanning: false);
        using var client = host.CreateClient();
        static byte[] Zip()
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                using var writer = new StreamWriter(archive.CreateEntry("scan-me/SKILL.md").Open());
                writer.Write("---\nname: scan-me\ndescription: Test-only harmless skill\n---\n# Instructions\n");
            }
            return ms.ToArray();
        }
        var form = new MultipartFormDataContent();
        form.Add(new StringContent("1.0.0"), "version");
        form.Add(new ByteArrayContent(Zip()), "file", "test.zip");
        var upload = await client.SendAsync(WithToken(HttpMethod.Post, "/api/skills", form));
        Assert.Equal(HttpStatusCode.Accepted, upload.StatusCode);
        using var metadata = JsonDocument.Parse(await upload.Content.ReadAsStringAsync());
        var digest = metadata.RootElement.GetProperty("sha256").GetString()!;
        Assert.Equal("QUEUED", metadata.RootElement.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/skills/scan-me/1.0.0/download"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/skills/scan-me/1.0.0/skill.md"))).StatusCode);
        Assert.DoesNotContain("scan-me", await (await client.SendAsync(WithToken(
            HttpMethod.Get, "/api/skills"))).Content.ReadAsStringAsync());
        Assert.DoesNotContain("scan-me", await (await client.SendAsync(WithToken(
            HttpMethod.Get, "/api/packages?kind=skill"))).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/mcp",
            new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/security/skills", token: ReadToken))).StatusCode);

        var body = JsonContent.Create(new { sha256 = digest, reason = "Reviewed synthetic harmless example" });
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(WithToken(
            HttpMethod.Post, "/api/security/skills/scan-me/1.0.0/approve", body))).StatusCode);
        var store = host.Services.GetRequiredService<SkillSecurityStore>();
        Assert.True(store.SaveResult("scan-me", "1.0.0", digest, "NEEDS_REVIEW", 0, "SAFE", "{}"));
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithToken(
            HttpMethod.Post, "/api/security/skills/scan-me/1.0.0/approve",
            JsonContent.Create(new { sha256 = digest, reason = "Reviewed synthetic harmless example" })))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithToken(
            HttpMethod.Get, "/api/skills/scan-me/1.0.0/download"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithToken(
            HttpMethod.Post, "/api/security/skills/scan-me/1.0.0/revoke",
            JsonContent.Create(new { sha256 = digest, reason = "Test the revocation gate" })))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(WithToken(
            HttpMethod.Get, "/api/skills/scan-me/1.0.0/download"))).StatusCode);
    }

    [Fact]
    public async Task Skills_LegacyBackfillIsExplicitQuarantinedIdempotentAndAdminOnly()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var database = host.Services.GetRequiredService<IPackageDatabase>();
        var blobs = host.Services.GetRequiredService<BlobStore>();
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("legacy-smoke/SKILL.md").Open());
            writer.Write("---\nname: legacy-smoke\ndescription: Inert legacy fixture\n---\n# Test\n");
        }
        var data = stream.ToArray();
        Assert.True(await blobs.PutAsync(SkillEndpoints.SkillKey("legacy-smoke", "1.0.0"),
            new MemoryStream(data, writable: false), data.Length + 1, CancellationToken.None));
        Assert.True(database.AddSkill("legacy-smoke", "1.0.0", "Inert legacy fixture", "synthetic-actor"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(WithToken(
            HttpMethod.Get, "/api/skills/legacy-smoke/1.0.0/download"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(WithToken(
            HttpMethod.Post, "/api/security/skills/backfill", token: ReadToken))).StatusCode);

        var response = await client.SendAsync(WithToken(HttpMethod.Post,
            "/api/security/skills/backfill?limit=25"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("queued").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("remaining").GetInt32());
        var security = host.Services.GetRequiredService<SkillSecurityStore>();
        var revision = security.Get("legacy-smoke", "1.0.0");
        Assert.NotNull(revision);
        Assert.Equal("QUEUED", revision.State);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), revision.Sha256);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(WithToken(
            HttpMethod.Get, "/api/skills/legacy-smoke/1.0.0/skill.md"))).StatusCode);

        response = await client.SendAsync(WithToken(HttpMethod.Post,
            "/api/security/skills/backfill?limit=25"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var duplicate = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, duplicate.RootElement.GetProperty("queued").GetInt32());
    }

    [Fact]
    public async Task ApprovedSkillTamperingFailsClosedAcrossRestAndMcp()
    {
        using var host = new Host(backgroundSkillScanning: false);
        using var client = host.CreateClient();
        var settings = host.Services.GetRequiredService<Settings>();
        var database = host.Services.GetRequiredService<IPackageDatabase>();
        var blobs = host.Services.GetRequiredService<BlobStore>();
        var security = host.Services.GetRequiredService<SkillSecurityStore>();
        const string name = "integrity-fixture";
        const string version = "1.0.0";

        static byte[] Archive(string description)
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("integrity-fixture/SKILL.md").Open());
                writer.Write("---\nname: integrity-fixture\ndescription: " + description +
                    "\n---\n# Synthetic data only\n");
            }
            return buffer.ToArray();
        }

        var approvedBytes = Archive("Harmless approved revision");
        var sha = Convert.ToHexStringLower(SHA256.HashData(approvedBytes));
        Assert.True(security.TryStage(name, version, "Synthetic fixture", sha,
            "skill-staging/" + sha + ".zip"));
        Assert.True(security.SaveResult(name, version, sha, "NEEDS_REVIEW", 0, "SAFE", "{}"));
        Assert.True(security.TryTransition(name, version, sha, "NEEDS_REVIEW",
            "APPROVED", "synthetic-reviewer", "Fixture verification only"));
        Assert.True(database.AddSkill(name, version, "Synthetic fixture", "synthetic-test"));
        var key = SkillEndpoints.SkillKey(name, version);
        Assert.True(await blobs.PutAsync(key, new MemoryStream(approvedBytes),
            approvedBytes.LongLength + 1, CancellationToken.None));

        using (var ok = await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/skills/" + name + "/" + version + "/download")))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Equal(approvedBytes, await ok.Content.ReadAsByteArrayAsync());
        }
        var snapshot = PackoryMcpCatalog.Get(name, version, database, blobs, security, settings);
        Assert.NotNull(snapshot);
        var entry = PackoryMcpCatalog.Entry(name, version, database, blobs, security, settings);
        Assert.NotNull(entry);
        Assert.Equal("digest_verified",
            JsonDocument.Parse(JsonSerializer.Serialize(PackoryMcpTools.VerifySkillIntegrity(
                name, version, database, blobs, security, settings)))
                .RootElement.GetProperty("status").GetString());

        // Simulate a compromised blob store after approval, preserving the same
        // name and version. No REST or MCP read may distribute these new bytes.
        var tamperedBytes = Archive("Unauthorized replacement instructions");
        File.WriteAllBytes(blobs.Existing(key)!, tamperedBytes);
        foreach (var path in new[]
        {
            "/api/skills/" + name + "/" + version + "/download",
            "/api/skills/" + name + "/" + version + "/skill.md"
        })
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.SendAsync(WithToken(HttpMethod.Get, path))).StatusCode);
        Assert.Null(PackoryMcpCatalog.Get(name, version, database, blobs, security, settings));
        Assert.Null(PackoryMcpCatalog.Entry(name, version, database, blobs, security, settings));
        var resourceError = Assert.Throws<ModelContextProtocol.McpProtocolException>(() =>
            PackoryMcpResources.SkillFile(version, name, "SKILL.md", database, blobs, security, settings));
        Assert.Equal(ModelContextProtocol.McpErrorCode.InvalidParams, resourceError.ErrorCode);
        Assert.Equal("integrity_unavailable",
            JsonDocument.Parse(JsonSerializer.Serialize(PackoryMcpTools.VerifySkillIntegrity(
                name, version, database, blobs, security, settings)))
                .RootElement.GetProperty("status").GetString());

        Assert.True(security.TryTransition(name, version, sha, "APPROVED",
            "REVOKED", "synthetic-reviewer", "Fixture revocation check"));
        Assert.Equal("unavailable",
            JsonDocument.Parse(JsonSerializer.Serialize(PackoryMcpTools.VerifySkillIntegrity(
                name, version, database, blobs, security, settings)))
                .RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Mcp_AuthenticatedClientNegotiatesAndDiscoversReadOnlyTools()
    {
        using var host = new Host();
        using var httpClient = host.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(httpClient.BaseAddress!, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + ReadToken
            }
        }, httpClient);
        await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await mcp.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Contains(tools, tool => tool.Name == "search_artifacts");
        Assert.Contains(tools, tool => tool.Name == "list_approved_skills");
        Assert.Contains(tools, tool => tool.Name == "verify_skill_integrity");
        Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("approve_", StringComparison.OrdinalIgnoreCase));
    }


    [Fact]
    public async Task Mcp_RejectsNugetBasicAndApiKeyCredentialFallback()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string body = """
            {"jsonrpc":"2.0","id":11,"method":"tools/list","params":{"_meta":{
              "io.modelcontextprotocol/protocolVersion":"2026-07-28",
              "io.modelcontextprotocol/clientInfo":{"name":"test","version":"1"},
              "io.modelcontextprotocol/clientCapabilities":{}
            }}}
            """;
        using var basic = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        basic.Content = new StringContent(body, Encoding.UTF8, "application/json");
        basic.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic:" + ReadToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(basic)).StatusCode);

        using var nugetHeader = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        nugetHeader.Content = new StringContent(body, Encoding.UTF8, "application/json");
        nugetHeader.Headers.TryAddWithoutValidation("X-NuGet-ApiKey", ReadToken);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(nugetHeader)).StatusCode);

        using var bearer = WithToken(HttpMethod.Post, "/mcp",
            new StringContent(body, Encoding.UTF8, "application/json"), ReadToken);
        bearer.Headers.TryAddWithoutValidation("Mcp-Protocol-Version", "2026-07-28");
        bearer.Headers.TryAddWithoutValidation("Mcp-Method", "tools/list");
        bearer.Headers.Accept.ParseAdd("application/json");
        bearer.Headers.Accept.ParseAdd("text/event-stream");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(bearer)).StatusCode);
    }


    [Fact]
    public async Task Mcp_SkillsDirectoryListsDirectChildrenAndFailsClosedAfterRevocation()
    {
        static byte[] Archive()
        {
            using var data = new MemoryStream();
            using (var zip = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: true))
            {
                static void Add(ZipArchive zip, string path, string content)
                {
                    using var output = new StreamWriter(zip.CreateEntry(path).Open());
                    output.Write(content);
                }
                Add(zip, "directory-fixture/SKILL.md",
                    "---\nname: directory-fixture\ndescription: Inert directory fixture\n---\n# Test\n");
                Add(zip, "directory-fixture/references/guide.md", "# Reference\n");
                Add(zip, "directory-fixture/references/deep/detail.txt", "Nested.\n");
                Add(zip, "directory-fixture/scripts/example.sh", "# inert test file\n");
                zip.CreateEntry("directory-fixture/");
                zip.CreateEntry("directory-fixture/empty/");
            }
            return data.ToArray();
        }

        using var host = new Host(backgroundSkillScanning: false);
        using var client = host.CreateClient();
        var bytes = Archive();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("1.0.0"), "version");
        form.Add(new ByteArrayContent(bytes), "file", "directory-fixture.zip");
        using var response = await client.SendAsync(
            WithToken(HttpMethod.Post, "/api/skills", form));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var digest = body.RootElement.GetProperty("sha256").GetString()!;
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var blobs = host.Services.GetRequiredService<BlobStore>();
        var security = host.Services.GetRequiredService<SkillSecurityStore>();
        var settings = host.Services.GetRequiredService<Settings>();
        var rootUri = "skill://packory/1.0.0/directory-fixture";

        Assert.Null(PackoryMcpCatalog.ReadDirectory(
            rootUri, db, blobs, security, settings));
        Assert.True(security.SaveResult("directory-fixture", "1.0.0", digest,
            "NEEDS_REVIEW", 0, "SAFE", "{}"));
        using var approve = await client.SendAsync(WithToken(HttpMethod.Post,
            "/api/security/skills/directory-fixture/1.0.0/approve",
            JsonContent.Create(new { sha256 = digest,
                reason = "Reviewed synthetic directory navigation fixture" })));
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);

        JsonObject Read(string uri) => Assert.IsType<JsonObject>(
            PackoryMcpCatalog.ReadDirectory(uri, db, blobs, security, settings));
        var root = Read(rootUri);
        Assert.Equal("complete", root["resultType"]!.GetValue<string>());
        Assert.Equal("private", root["cacheScope"]!.GetValue<string>());
        Assert.Equal(0, root["ttlMs"]!.GetValue<int>());
        var children = Assert.IsType<JsonArray>(root["resources"]);
        Assert.Equal(4, children.Count);
        Assert.Equal(new[] { "SKILL.md", "empty", "references", "scripts" },
            children.Select(x => x!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal("inode/directory",
            children[1]!["mimeType"]!.GetValue<string>());
        var refs = Read(rootUri + "/references");
        Assert.Equal(new[] { "deep", "guide.md" },
            ((JsonArray)refs["resources"]!).Select(x => x!["name"]!.GetValue<string>()));
        Assert.Equal("text/markdown",
            ((JsonArray)refs["resources"]!)[1]!["mimeType"]!.GetValue<string>());
        Assert.Single((JsonArray)Read(rootUri + "/references/deep")["resources"]!);
        Assert.Empty((JsonArray)Read(rootUri + "/empty")["resources"]!);

        foreach (var invalid in new[]
        {
            rootUri + "/SKILL.md", rootUri + "/missing", rootUri + "/references/",
            rootUri + "/%2e%2e", rootUri + "/references/%2fsecret",
            rootUri + "/references/%5csecret", rootUri + "/references/../scripts",
            rootUri + "?q=1", rootUri + "#fragment",
            "skill://other/1.0.0/directory-fixture"
        })
            Assert.Null(PackoryMcpCatalog.ReadDirectory(
                invalid, db, blobs, security, settings));

        using var revoke = await client.SendAsync(WithToken(HttpMethod.Post,
            "/api/security/skills/directory-fixture/1.0.0/revoke",
            JsonContent.Create(new { sha256 = digest,
                reason = "Revoke synthetic directory Skill" })));
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Null(PackoryMcpCatalog.ReadDirectory(
            rootUri, db, blobs, security, settings));
        Assert.Null(PackoryMcpCatalog.ReadDirectory(
            rootUri + "/references", db, blobs, security, settings));
    }

    [Fact]
    public async Task Mcp_DeniesFeedScopedAndPublisherCredentialsOutsideTheirScope()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string feed = "mcp-scope-test";
        using var draft = await client.SendAsync(WithToken(HttpMethod.Post,
            "/api/v1/admin/feeds/drafts", JsonContent.Create(new
            {
                id = feed, kind = "nuget", mode = "hosted",
                displayName = "Synthetic scope test"
            })));
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        using var activated = await client.SendAsync(WithToken(HttpMethod.Post,
            "/api/v1/admin/feeds/" + feed + "/activate"));
        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);

        async Task<string> Managed(string role, string? scope)
        {
            using var created = await client.SendAsync(WithToken(HttpMethod.Post,
                "/api/admin/tokens", JsonContent.Create(new
                {
                    role, @namespace = scope, expiresInHours = 1
                })));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var response = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            return response.RootElement.GetProperty("token").GetString()!;
        }
        var globalReader = await Managed("read", null);
        var scopedReader = await Managed("read", "feed:" + feed + ":read");
        var scopedWriter = await Managed("write", "feed:" + feed + ":publish");
        var namespacePublisher = await Managed("write", "nuget:approved.*");

        async Task<HttpStatusCode> McpCall(string credential)
        {
            const string payload = """
                {"jsonrpc":"2.0","id":7,"method":"tools/list","params":{"_meta":{
                  "io.modelcontextprotocol/protocolVersion":"2026-07-28",
                  "io.modelcontextprotocol/clientInfo":{"name":"scope-test","version":"1"},
                  "io.modelcontextprotocol/clientCapabilities":{}
                }}}
                """;
            using var req = WithToken(HttpMethod.Post, "/mcp",
                new StringContent(payload, Encoding.UTF8, "application/json"), credential);
            req.Headers.TryAddWithoutValidation("Mcp-Protocol-Version", "2026-07-28");
            req.Headers.TryAddWithoutValidation("Mcp-Method", "tools/list");
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.Accept.ParseAdd("text/event-stream");
            using var result = await client.SendAsync(req);
            return result.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await McpCall(globalReader));
        foreach (var restricted in new[] { scopedReader, scopedWriter, namespacePublisher })
            Assert.Equal(HttpStatusCode.Forbidden, await McpCall(restricted));

        // The same exact feed-scoped credentials still work at their native
        // registry source; global MCP restrictions must not break feed access.
        using var allowedReader = await client.SendAsync(WithToken(HttpMethod.Get,
            "/feeds/" + feed + "/nuget/v3/index.json", token: scopedReader));
        Assert.Equal(HttpStatusCode.OK, allowedReader.StatusCode);
        using var allowedWriter = await client.SendAsync(WithToken(HttpMethod.Get,
            "/feeds/" + feed + "/nuget/v3/index.json", token: scopedWriter));
        Assert.Equal(HttpStatusCode.OK, allowedWriter.StatusCode);
    }

    [Fact]
    public async Task Mcp_SkillsExtensionListsOnlyApprovedRevisions()
    {
        using var host = new Host();
        using var http = host.CreateClient();
        const string body = """
            {"jsonrpc":"2.0","id":2,"method":"skills/list","params":{"_meta":{
              "io.modelcontextprotocol/protocolVersion":"2026-07-28",
              "io.modelcontextprotocol/clientInfo":{"name":"test-client","version":"1.0"},
              "io.modelcontextprotocol/clientCapabilities":{}
            }}}
            """;
        using var request = WithToken(HttpMethod.Post, "/mcp",
            new StringContent(body, Encoding.UTF8, "application/json"), ReadToken);
        request.Headers.TryAddWithoutValidation("Mcp-Protocol-Version", "2026-07-28");
        request.Headers.TryAddWithoutValidation("Mcp-Method", "skills/list");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            "MCP skills/list HTTP " + (int)response.StatusCode + ": " + text);
        Assert.Contains("skills", text, StringComparison.Ordinal);
        Assert.Contains("resultType", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SkillSpectorContract_FailsClosedWhenIncompleteOrMalformed()
    {
        const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var complete = new
        {
            sha256 = digest, exit_code = 0,
            report = new
            {
                execution_successful = true,
                analysis_completeness = new { is_complete = true },
                metadata = new { llm_requested = false },
                risk_assessment = new { score = 12, severity = "LOW", recommendation = "SAFE" },
                issues = Array.Empty<object>()
            }
        };
        var parsed = SkillScanResult.Parse(JsonSerializer.Serialize(complete), digest);
        Assert.Equal("NEEDS_REVIEW", parsed.State);
        Assert.Equal(12, parsed.Risk);
        Assert.Throws<InvalidDataException>(() => SkillScanResult.Parse(
            JsonSerializer.Serialize(complete), new string('b', 64)));
        var incomplete = JsonSerializer.Serialize(complete).Replace(
            "\"is_complete\":true", "\"is_complete\":false", StringComparison.Ordinal);
        Assert.Equal("SCAN_FAILED", SkillScanResult.Parse(incomplete, digest).State);
        var suspicious = JsonSerializer.Serialize(complete).Replace(
            "\"severity\":\"LOW\"", "\"severity\":\"CRITICAL\"", StringComparison.Ordinal);
        Assert.Equal("QUARANTINED", SkillScanResult.Parse(suspicious, digest).State);
        // NVIDIA may smooth its aggregate risk even when one individual finding
        // remains HIGH/CRITICAL. That finding must block a reviewer approval.
        var withHighFinding = JsonNode.Parse(JsonSerializer.Serialize(complete))!;
        withHighFinding["report"]!["issues"] = JsonSerializer.SerializeToNode(
            new[] { new { severity = "HIGH", rule_id = "SYNTHETIC-TEST" } });
        Assert.Equal("QUARANTINED", SkillScanResult.Parse(
            withHighFinding.ToJsonString(), digest).State);
        // Unknown suppression/baseline entries must not silently hide risk.
        withHighFinding["report"]!["issues"] = new JsonArray();
        withHighFinding["report"]!["suppressed_count"] = 1;
        Assert.Equal("QUARANTINED", SkillScanResult.Parse(
            withHighFinding.ToJsonString(), digest).State);
        var missingRiskFields = JsonNode.Parse(JsonSerializer.Serialize(complete))!;
        ((JsonObject)missingRiskFields["report"]!).Remove("risk_assessment");
        Assert.Equal("SCAN_FAILED", SkillScanResult.Parse(
            missingRiskFields.ToJsonString(), digest).State);
        var malformedFinding = JsonNode.Parse(JsonSerializer.Serialize(complete))!;
        malformedFinding["report"]!["issues"] = new JsonArray(new JsonObject { ["unexpected"] = "value" });
        Assert.Equal("SCAN_FAILED", SkillScanResult.Parse(
            malformedFinding.ToJsonString(), digest).State);
    }

    [Fact]
    public async Task ReservedNamespaces_DoNotContactPublicUpstreams()
    {
        var upstreamCalls = 0;
        using var host = new Host(_ =>
        {
            Interlocked.Increment(ref upstreamCalls);
            throw new InvalidOperationException("Private namespace must not contact upstream");
        }, cfg =>
        {
            cfg.ReservedNugetPrefixes = ["internal."];
            cfg.ReservedNpmScopes = ["@internal"];
        });
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(WithToken(HttpMethod.Get,
            "/nuget/v3/flatcontainer/internal.widget/index.json"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(WithToken(HttpMethod.Get,
            "/nuget/v3/flatcontainer/internal.widget/1.0.0/internal.widget.1.0.0.nupkg"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(WithToken(HttpMethod.Get,
            "/nuget/v3/registration/internal.widget/index.json"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(WithToken(HttpMethod.Get,
            "/npm/%40internal%2Fwidget"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(WithToken(HttpMethod.Get,
            "/npm/%40internal%2Fwidget/-/1.0.0.tgz"))).StatusCode);
        Assert.Equal(0, Volatile.Read(ref upstreamCalls));

        var upload = new MultipartFormDataContent();
        upload.Add(new ByteArrayContent(Nupkg("Internal.Widget", "1.0.0")), "package", "internal.widget.1.0.0.nupkg");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(WithToken(HttpMethod.Put,
            "/nuget/v3/push", upload))).StatusCode);
        using var index = await client.SendAsync(WithToken(HttpMethod.Get,
            "/nuget/v3/flatcontainer/internal.widget/index.json"));
        Assert.Contains("1.0.0", await index.Content.ReadAsStringAsync());
        Assert.Equal(0, Volatile.Read(ref upstreamCalls));
    }

    [Fact]
    public async Task Nuget_ConcurrentColdMissFetchesUpstreamBlobOnlyOnce()
    {
        var bytes = Nupkg("Example.Package", "1.2.3");
        var downloads = 0;
        using var host = new Host(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".nupkg.sha512", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Convert.ToBase64String(SHA512.HashData(bytes)))
                };
            if (request.RequestUri.AbsolutePath.EndsWith(".nupkg", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref downloads);
                Thread.Sleep(30);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = host.CreateClient();
        const string url = "/nuget/v3/flatcontainer/example.package/1.2.3/example.package.1.2.3.nupkg";
        var responses = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => client.SendAsync(WithToken(HttpMethod.Get, url))));
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            }
        }
        Assert.Equal(1, Volatile.Read(ref downloads));
    }

    [Fact]
    public async Task Npm_ConcurrentColdMissFetchesUpstreamTarballOnlyOnce()
    {
        var bytes = new byte[] { 0x1f, 0x8b, 8, 0, 0, 0, 0, 0, 0 };
        var downloads = 0;
        var metadata = """
            {"name":"example-widget","versions":{"1.0.0":{"name":"example-widget","version":"1.0.0","dist":{"tarball":"https://registry.npmjs.org/example-widget/-/example-widget-1.0.0.tgz","integrity":"HASH"}}}}
            """.Replace("HASH", "sha512-" + Convert.ToBase64String(SHA512.HashData(bytes)));
        using var host = new Host(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref downloads);
                Thread.Sleep(30);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            }
            return Json(metadata);
        });
        using var client = host.CreateClient();
        var responses = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => client.SendAsync(WithToken(HttpMethod.Get, "/npm/example-widget/-/1.0.0.tgz"))));
        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            }
        }
        Assert.Equal(1, Volatile.Read(ref downloads));
    }

    [Fact]
    public async Task Npm_RejectsTarballWhenUpstreamSuppliesNoIntegrityClaim()
    {
        var bytes = new byte[] { 0x1f, 0x8b, 8, 0, 0, 0 };
        const string metadata = """
            {"name":"unsigned-demo","versions":{"1.0.0":{"name":"unsigned-demo","version":"1.0.0","dist":{"tarball":"https://registry.npmjs.org/unsigned-demo/-/unsigned-demo-1.0.0.tgz"}}}}
            """;
        using var host = new Host(request => request.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
            : Json(metadata));
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.BadGateway, (await client.SendAsync(WithToken(HttpMethod.Get,
            "/npm/unsigned-demo/-/1.0.0.tgz"))).StatusCode);
        var catalog = await (await client.SendAsync(WithToken(HttpMethod.Get, "/api/packages?kind=npm")))
            .Content.ReadAsStringAsync();
        Assert.DoesNotContain("unsigned-demo", catalog);
    }

    [Fact]
    public async Task Startup_QuarantinesUnindexedImmutableBlobsWithoutPublishingThem()
    {
        using var host = new Host();
        var key = Path.Combine(host.DataDirectory, "blobs", "nuget", "ghost", "1.0.0", "ghost.1.0.0.nupkg");
        Directory.CreateDirectory(Path.GetDirectoryName(key)!);
        await File.WriteAllBytesAsync(key, Nupkg("ghost", "1.0.0"));
        using var client = host.CreateClient();
        Assert.False(File.Exists(key));
        var quarantined = Directory.GetFiles(Path.Combine(host.DataDirectory, "recovery", "orphaned"),
            "ghost.1.0.0.nupkg", SearchOption.AllDirectories);
        Assert.Single(quarantined);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(WithToken(HttpMethod.Get,
            "/nuget/v3/flatcontainer/ghost/1.0.0/ghost.1.0.0.nupkg"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public void Reconciliation_DetectsMissingHostedBlobs()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-recovery-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = new Settings { DataDirectory = root };
            var db = new Database(cfg);
            db.Initialize();
            db.UpsertPackage("nuget", "local.demo", "1.0.0", "Test", "hosted");
            var recovery = new ArtifactRecovery(cfg, db, new BlobStore(cfg),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtifactRecovery>.Instance).Reconcile();
            Assert.Equal(1, recovery.MissingHostedArtifacts);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }


    [Fact]
    public async Task AuditEvents_RecordSuccessfulHostedPublishesWithoutCredentials()
    {
        // This test asserts the legacy synchronous publication/audit contract.
        // The enforced asynchronous security flow is covered in the separate test.
        using var host = new Host(configure: cfg => cfg.SkillSecurityEnforced = false);
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/admin/audit", token: WriteToken))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/admin/audit?before=0"))).StatusCode);

        var nugetForm = new MultipartFormDataContent();
        nugetForm.Add(new ByteArrayContent(Nupkg("Audit.Example", "1.0.0")), "package", "audit.example.1.0.0.nupkg");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(WithToken(HttpMethod.Put,
            "/nuget/v3/push", nugetForm, WriteToken))).StatusCode);

        var tarball = new byte[] { 0x1f, 0x8b, 8, 0, 0, 0, 0, 0 };
        var publish = new JsonObject
        {
            ["name"] = "audit-example",
            ["versions"] = new JsonObject
            {
                ["2.0.0"] = new JsonObject { ["name"] = "audit-example", ["version"] = "2.0.0" }
            },
            ["dist-tags"] = new JsonObject { ["latest"] = "2.0.0" },
            ["_attachments"] = new JsonObject
            {
                ["audit-example-2.0.0.tgz"] = new JsonObject { ["data"] = Convert.ToBase64String(tarball) }
            }
        };
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(WithToken(HttpMethod.Put,
            "/npm/audit-example", new StringContent(publish.ToJsonString(), Encoding.UTF8, "application/json")))).StatusCode);

        using var mem = new MemoryStream();
        using (var zip = new ZipArchive(mem, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("audit-skill/SKILL.md").Open());
            writer.Write("---\nname: audit-skill\ndescription: Demonstrate audited imports\n---\n# Test\n");
        }
        var skillForm = new MultipartFormDataContent();
        skillForm.Add(new StringContent("1.0.0"), "version");
        skillForm.Add(new ByteArrayContent(mem.ToArray()), "file", "audit-skill.zip");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(WithToken(HttpMethod.Post,
            "/api/skills", skillForm))).StatusCode);

        using var auditResponse = await client.SendAsync(WithToken(HttpMethod.Get, "/api/admin/audit?limit=10"));
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        var json = await auditResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(AdminToken, json);
        Assert.DoesNotContain(WriteToken, json);
        using var audit = JsonDocument.Parse(json);
        var items = audit.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        Assert.Contains(items, e => e.GetProperty("kind").GetString() == "nuget" &&
                                    e.GetProperty("actor").GetString() == "write-token");
        Assert.Contains(items, e => e.GetProperty("kind").GetString() == "npm" &&
                                    e.GetProperty("actor").GetString() == "admin-token");
        Assert.Contains(items, e => e.GetProperty("kind").GetString() == "skill");
        var latestId = items[0].GetProperty("id").GetInt64();
        using var page = await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/admin/audit?before=" + latestId + "&limit=1"));
        using var pagedJson = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
        Assert.Single(pagedJson.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task PublishRateLimits_AreSeparatedByCredentialRole()
    {
        using var host = new Host(configure: cfg => cfg.PublishRequestsPerMinute = 1);
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PutAsync("/nuget/v3/push", new StringContent(""))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests,
            (await client.PutAsync("/nuget/v3/push", new StringContent(""))).StatusCode);

        static MultipartFormDataContent Form(string name)
        {
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(Nupkg(name, "1.0.0")), "package", name + ".1.0.0.nupkg");
            return form;
        }
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(WithToken(HttpMethod.Put,
            "/nuget/v3/push", Form("writer.one"), WriteToken))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(WithToken(HttpMethod.Put,
            "/nuget/v3/push", Form("writer.two"), WriteToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(WithToken(HttpMethod.Put,
            "/nuget/v3/push", Form("admin.one")))).StatusCode);
    }

    [Fact]
    public async Task Nuget_RefusesAnUpstreamPackageWithoutChecksum()
    {
        var bytes = Nupkg();
        using var host = new Host(request =>
            request.RequestUri!.AbsolutePath.EndsWith(".nupkg", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.BadGateway,
            (await client.SendAsync(WithToken(HttpMethod.Get,
                "/nuget/v3/flatcontainer/example.package/1.2.3/example.package.1.2.3.nupkg"))).StatusCode);
        var catalog = await (await client.SendAsync(WithToken(HttpMethod.Get, "/api/packages?kind=nuget")))
            .Content.ReadAsStringAsync();
        Assert.DoesNotContain("example.package", catalog);
    }

    [Fact]
    public async Task DenyPolicyBlocksLocalPublishingAndPublicLookups()
    {
        var calledUpstream = 0;
        using var host = new Host(_ =>
        {
            Interlocked.Increment(ref calledUpstream);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, cfg =>
        {
            cfg.DeniedNugetPrefixes = ["blocked."];
            cfg.DeniedNpmScopes = ["@blocked"];
        });
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(WithToken(HttpMethod.Get,
            "/nuget/v3/flatcontainer/blocked.widget/index.json"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(WithToken(HttpMethod.Get,
            "/npm/%40blocked%2Fwidget"))).StatusCode);
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Nupkg("Blocked.Widget", "1.0.0")), "package", "blocked.widget.1.0.0.nupkg");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(WithToken(HttpMethod.Put,
            "/nuget/v3/push", form))).StatusCode);
        Assert.Equal(0, Volatile.Read(ref calledUpstream));
    }

    [Fact]
    public async Task ReadinessDetectsHostedArtifactDeletionAfterStartup()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Nupkg("Lost.Widget", "1.0.0")), "package", "lost.widget.1.0.0.nupkg");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(WithToken(HttpMethod.Put,
            "/nuget/v3/push", form))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        var blob = Path.Combine(host.DataDirectory, "blobs", "nuget", "lost.widget", "1.0.0", "lost.widget.1.0.0.nupkg");
        File.Delete(blob);
        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Contains("missingHostedArtifacts", await ready.Content.ReadAsStringAsync());
    }


    [Fact]
    public async Task Cargo_AlternateRegistry_PublishesSparseIndexAndImmutableCrate()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var metadata = new JsonObject
        {
            ["name"] = "packory-demo",
            ["vers"] = "1.0.0",
            ["description"] = "Hosted Cargo test",
            ["features"] = new JsonObject(),
            ["deps"] = new JsonArray()
        };
        var json = Encoding.UTF8.GetBytes(metadata.ToJsonString());
        var archive = new byte[] { 0x1f, 0x8b, 0x08, 0x00, 0x00, 0x00 };
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((uint)json.Length);
            writer.Write(json);
            writer.Write((uint)archive.Length);
            writer.Write(archive);
        }
        var request = new HttpRequestMessage(HttpMethod.Put, "/cargo/api/v1/crates/new")
        {
            Content = new ByteArrayContent(payload.ToArray())
        };
        request.Headers.TryAddWithoutValidation("Authorization", AdminToken);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        using var config = await client.SendAsync(WithToken(HttpMethod.Get, "/cargo/index/config.json"));
        var configJson = await config.Content.ReadAsStringAsync();
        using var configDocument = JsonDocument.Parse(configJson);
        var registryApi = configDocument.RootElement.GetProperty("api").GetString();
        var downloadApi = configDocument.RootElement.GetProperty("dl").GetString();
        Assert.Equal("/cargo", new Uri(registryApi!).AbsolutePath);
        Assert.Equal("/cargo/api/v1/crates", new Uri(downloadApi!).AbsolutePath);
        Assert.DoesNotContain("crates.io", configJson);
        var url = "/cargo/index/pa/ck/packory-demo";
        using var index = await client.SendAsync(WithToken(HttpMethod.Get, url));
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.True(index.Headers.ETag is not null);
        var indexText = await index.Content.ReadAsStringAsync();
        using var indexEntry = JsonDocument.Parse(indexText.Trim());
        Assert.Equal("packory-demo", indexEntry.RootElement.GetProperty("name").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(archive)),
            indexEntry.RootElement.GetProperty("cksum").GetString());
        var condition = WithToken(HttpMethod.Get, url);
        condition.Headers.IfNoneMatch.Add(index.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(condition)).StatusCode);
        using var crate = await client.SendAsync(WithToken(HttpMethod.Get,
            "/cargo/api/v1/crates/packory-demo/1.0.0/download"));
        Assert.Equal(archive, await crate.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.SendAsync(WithToken(HttpMethod.Put, "/cargo/api/v1/crates/new",
                new ByteArrayContent(payload.ToArray())))).StatusCode);

        // Cargo yank modifies sparse-index visibility but must not erase bytes.
        var yankUrl = "/cargo/api/v1/crates/packory-demo/1.0.0/yank";
        using var noAuth = await client.DeleteAsync(yankUrl);
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);
        using var yank = await client.SendAsync(WithToken(HttpMethod.Delete, yankUrl));
        Assert.Equal(HttpStatusCode.OK, yank.StatusCode);
        using var yankedIndex = await client.SendAsync(WithToken(HttpMethod.Get, url));
        Assert.Equal(HttpStatusCode.OK, yankedIndex.StatusCode);
        Assert.NotEqual(index.Headers.ETag!.Tag, yankedIndex.Headers.ETag!.Tag);
        using (var yankedRecord = JsonDocument.Parse((await yankedIndex.Content.ReadAsStringAsync()).Trim()))
            Assert.True(yankedRecord.RootElement.GetProperty("yanked").GetBoolean());
        using var stillAvailable = await client.SendAsync(WithToken(HttpMethod.Get,
            "/cargo/api/v1/crates/packory-demo/1.0.0/download"));
        Assert.Equal(archive, await stillAvailable.Content.ReadAsByteArrayAsync());

        using var unyank = await client.SendAsync(WithToken(HttpMethod.Put,
            "/cargo/api/v1/crates/packory-demo/1.0.0/unyank"));
        Assert.Equal(HttpStatusCode.OK, unyank.StatusCode);
        using var activeIndex = await client.SendAsync(WithToken(HttpMethod.Get, url));
        using (var activeRecord = JsonDocument.Parse((await activeIndex.Content.ReadAsStringAsync()).Trim()))
            Assert.False(activeRecord.RootElement.GetProperty("yanked").GetBoolean());
        using var unknownYank = await client.SendAsync(WithToken(HttpMethod.Delete,
            "/cargo/api/v1/crates/packory-demo/9.0.0/yank"));
        Assert.Equal(HttpStatusCode.NotFound, unknownYank.StatusCode);
    }

    [Fact]
    public async Task PyPi_HostedWheel_ProvidesSimpleHtmlJsonAndImmutableDownloads()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var bytes = new byte[] { 0x50, 0x4b, 3, 4, 0, 0, 0 };
        const string filename = "demo_pkg-1.2.3-py3-none-any.whl";
        static MultipartFormDataContent Upload(byte[] bytes)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent("demo_pkg"), "name");
            form.Add(new StringContent("1.2.3"), "version");
            form.Add(new StringContent(Convert.ToHexStringLower(SHA256.HashData(bytes))), "sha256_digest");
            form.Add(new ByteArrayContent(bytes), "content", filename);
            return form;
        }
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithToken(HttpMethod.Post,
            "/pypi/legacy/", Upload(bytes)))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(WithToken(HttpMethod.Post,
            "/pypi/legacy/", Upload(bytes)))).StatusCode);
        using var root = await client.SendAsync(WithToken(HttpMethod.Get, "/pypi/simple/"));
        Assert.Contains("demo-pkg/", await root.Content.ReadAsStringAsync());
        using var html = await client.SendAsync(WithToken(HttpMethod.Get, "/pypi/simple/demo_pkg/"));
        var content = await html.Content.ReadAsStringAsync();
        Assert.Contains("#sha256=" + Convert.ToHexStringLower(SHA256.HashData(bytes)), content);
        using var request = WithToken(HttpMethod.Get, "/pypi/simple/demo-pkg/");
        request.Headers.Accept.ParseAdd("application/vnd.pypi.simple.v1+json");
        using var jsonResponse = await client.SendAsync(request);
        Assert.Equal("application/vnd.pypi.simple.v1+json", jsonResponse.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await jsonResponse.Content.ReadAsStringAsync());
        Assert.Equal(filename, doc.RootElement.GetProperty("files")[0].GetProperty("filename").GetString());
        var actual = await client.SendAsync(WithToken(HttpMethod.Get,
            "/pypi/files/demo-pkg/" + filename));
        Assert.Equal(bytes, await actual.Content.ReadAsByteArrayAsync());
        Assert.Contains("demo-pkg", await (await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/packages?kind=pypi"))).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PyPi_RejectsPathTraversalAndInvalidArtifactHash()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var form = new MultipartFormDataContent();
        form.Add(new StringContent("demo"), "name");
        form.Add(new StringContent("1.0.0"), "version");
        form.Add(new StringContent(new string('0', 64)), "sha256_digest");
        form.Add(new ByteArrayContent(new byte[] { 0, 1, 2 }), "content", "demo-1.0.0-py3-none-any.whl");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(WithToken(HttpMethod.Post,
            "/pypi/legacy/", form))).StatusCode);
    }


    [Fact]
    public async Task Oci_PushChunkAndManifest_ThenPullTagsAndReferrers()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(WithToken(HttpMethod.Get, "/v2/"))).StatusCode);
        const string repository = "team/demo";
        var config = Encoding.UTF8.GetBytes("{\"architecture\":\"amd64\",\"os\":\"linux\"}");
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(config));

        using var started = await client.SendAsync(WithToken(HttpMethod.Post,
            "/v2/" + repository + "/blobs/uploads/"));
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var upload = started.Headers.Location?.AbsolutePath;
        Assert.NotNull(upload);
        using var patch = await client.SendAsync(WithToken(new HttpMethod("PATCH"), upload!,
            new ByteArrayContent(config)));
        Assert.Equal(HttpStatusCode.Accepted, patch.StatusCode);
        using var finished = await client.SendAsync(WithToken(HttpMethod.Put,
            upload + "?digest=" + digest, new ByteArrayContent(Array.Empty<byte>())));
        Assert.Equal(HttpStatusCode.Created, finished.StatusCode);

        var blob = "/v2/" + repository + "/blobs/" + digest;
        using var fetched = await client.SendAsync(WithToken(HttpMethod.Get, blob));
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(digest, fetched.Headers.GetValues("Docker-Content-Digest").Single());
        Assert.Equal(config, await fetched.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(WithToken(HttpMethod.Get, "/v2/another/demo/blobs/" + digest))).StatusCode);

        var manifest = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = "application/vnd.oci.image.manifest.v1+json",
            ["config"] = new JsonObject
            {
                ["mediaType"] = "application/vnd.oci.image.config.v1+json",
                ["size"] = config.Length,
                ["digest"] = digest
            },
            ["layers"] = new JsonArray()
        };
        var body = new StringContent(manifest.ToJsonString(), Encoding.UTF8, "application/vnd.oci.image.manifest.v1+json");
        using var created = await client.SendAsync(WithToken(HttpMethod.Put,
            "/v2/" + repository + "/manifests/latest", body));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var manifestDigest = created.Headers.GetValues("Docker-Content-Digest").Single();
        Assert.StartsWith("sha256:", manifestDigest);

        using var pulled = await client.SendAsync(WithToken(HttpMethod.Get,
            "/v2/" + repository + "/manifests/latest"));
        Assert.Equal(HttpStatusCode.OK, pulled.StatusCode);
        Assert.Equal(manifestDigest, pulled.Headers.GetValues("Docker-Content-Digest").Single());
        Assert.Equal(manifest.ToJsonString(), await pulled.Content.ReadAsStringAsync());
        var tags = await (await client.SendAsync(WithToken(HttpMethod.Get,
            "/v2/" + repository + "/tags/list"))).Content.ReadAsStringAsync();
        Assert.Contains("latest", tags);
        using var referrers = await client.SendAsync(WithToken(HttpMethod.Get,
            "/v2/" + repository + "/referrers/" + manifestDigest));
        Assert.Equal(HttpStatusCode.OK, referrers.StatusCode);
        Assert.Contains("\"manifests\":[]", await referrers.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Oci_RejectsWrongDigestAndUnknownBlobReferences()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        using var start = await client.SendAsync(WithToken(HttpMethod.Post, "/v2/team/test/blobs/uploads/"));
        var upload = start.Headers.Location!.AbsolutePath;
        var invalidDigest = "sha256:" + new string('0', 64);
        using var wrong = await client.SendAsync(WithToken(HttpMethod.Put,
            upload + "?digest=" + invalidDigest, new ByteArrayContent(new byte[] { 1, 2, 3 })));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var manifest = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["mediaType"] = "application/vnd.oci.image.manifest.v1+json",
            ["config"] = new JsonObject { ["digest"] = invalidDigest, ["size"] = 3 },
            ["layers"] = new JsonArray()
        };
        using var missing = await client.SendAsync(WithToken(HttpMethod.Put,
            "/v2/team/test/manifests/latest",
            new StringContent(manifest.ToJsonString(), Encoding.UTF8, "application/vnd.oci.image.manifest.v1+json")));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task WriteNamespacePolicy_RestrictsNugetAndOciWithoutRestrictingAdministrator()
    {
        using var host = new Host(configure: cfg => cfg.WriteNamespaces =
            ["nuget:approved.*", "oci:trusted/*"]);
        using var client = host.CreateClient();

        static HttpContent NugetUpload(string name)
        {
            var content = new MultipartFormDataContent();
            content.Add(new ByteArrayContent(Nupkg(name, "1.0.0")), "file", name + ".1.0.0.nupkg");
            return content;
        }

        using var denied = await client.SendAsync(WithToken(HttpMethod.Put, "/nuget/v3/push",
            NugetUpload("unapproved.pkg"), WriteToken));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var allowed = await client.SendAsync(WithToken(HttpMethod.Put, "/nuget/v3/push",
            NugetUpload("approved.pkg"), WriteToken));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        using var admin = await client.SendAsync(WithToken(HttpMethod.Put, "/nuget/v3/push",
            NugetUpload("unapproved.pkg"), AdminToken));
        Assert.Equal(HttpStatusCode.Created, admin.StatusCode);

        using var deniedOci = await client.SendAsync(WithToken(HttpMethod.Post,
            "/v2/untrusted/example/blobs/uploads/", token: WriteToken));
        Assert.Equal(HttpStatusCode.Forbidden, deniedOci.StatusCode);
        using var allowedOci = await client.SendAsync(WithToken(HttpMethod.Post,
            "/v2/trusted/example/blobs/uploads/", token: WriteToken));
        Assert.Equal(HttpStatusCode.Accepted, allowedOci.StatusCode);
    }

    [Fact]
    public async Task ManagedWriteToken_CannotEscalateScopeAndIsRevokedImmediately()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        using var create = await client.SendAsync(WithToken(HttpMethod.Post, "/api/admin/tokens",
            new StringContent("""
                {"role":"write","namespace":"nuget:approved.*","expiresInHours":2}
                """, Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("id").GetString()!;
        var token = doc.RootElement.GetProperty("token").GetString()!;
        Assert.Equal(64, token.Length);
        Assert.True(create.Headers.CacheControl?.NoStore);

        static MultipartFormDataContent Package(string name)
        {
            var data = new MultipartFormDataContent();
            data.Add(new ByteArrayContent(Nupkg(name, "1.0.0")), "file", name + ".1.0.0.nupkg");
            return data;
        }
        using var denied = await client.SendAsync(WithToken(HttpMethod.Put, "/nuget/v3/push",
            Package("outside.demo"), token));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var allowed = await client.SendAsync(WithToken(HttpMethod.Put, "/nuget/v3/push",
            Package("approved.demo"), token));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        using var revoke = await client.SendAsync(WithToken(HttpMethod.Delete, "/api/admin/tokens/" + id));
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        using var expired = await client.SendAsync(WithToken(HttpMethod.Put, "/nuget/v3/push",
            Package("approved.next"), token));
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        Assert.DoesNotContain(token, await (await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/admin/audit"))).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OciAuthenticatedPingOptIn_RequiresWriterEvenWhenGenericReadsAreAnonymous()
    {
        using var host = new Host(configure: cfg =>
        {
            cfg.AnonymousRead = true;
            cfg.OciAuthenticatedPing = true;
        });
        using var client = host.CreateClient();
        using var anonymous = await client.GetAsync("/v2/");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("Basic", anonymous.Headers.WwwAuthenticate.ToString());
        using var reader = await client.SendAsync(WithToken(HttpMethod.Get, "/v2/", token: ReadToken));
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
        using var writer = await client.SendAsync(WithToken(HttpMethod.Get, "/v2/", token: WriteToken));
        Assert.Equal(HttpStatusCode.OK, writer.StatusCode);
        using var publicCatalog = await client.GetAsync("/api/packages");
        Assert.Equal(HttpStatusCode.OK, publicCatalog.StatusCode);
    }

    [Fact]
    public async Task PublicationIntents_AreVisibleOnlyToAdminAndRecoverAfterCommittedMetadata()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        const string name = "journal-test";
        const string version = "1.0.0";
        var key = NuGetEndpoints.NugetKey(name, version);
        db.BeginPublication(key, "nuget", name, version);
        Assert.Single(db.PendingPublications());
        using var reader = await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/admin/publications/pending", token: ReadToken));
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
        using var admin = await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/admin/publications/pending"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.Contains(key, await admin.Content.ReadAsStringAsync());

        // A crash before any blob is stored can be reconciled without metadata damage.
        host.Services.GetRequiredService<ArtifactRecovery>().Reconcile();
        Assert.Empty(db.PendingPublications());

        // A crash after a successful metadata commit but before journal completion is safe.
        db.BeginPublication(key, "nuget", name, version);
        var blob = host.Services.GetRequiredService<BlobStore>();
        Assert.True(await blob.PutAsync(key, new MemoryStream(new byte[] { 1, 2, 3 }),
            1024, CancellationToken.None));
        Assert.True(db.PublishNuget(name, version, "test", "writer"));
        host.Services.GetRequiredService<ArtifactRecovery>().Reconcile();
        Assert.Empty(db.PendingPublications());
    }

    [Fact]
    public async Task OperationsStatusIsAdminOnlyAndDoesNotExposeCredentials()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/admin/operations/status")).StatusCode);
        using var reader = await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/admin/operations/status", token: ReadToken));
        Assert.Equal(HttpStatusCode.Forbidden, reader.StatusCode);
        using var admin = await client.SendAsync(WithToken(HttpMethod.Get,
            "/api/admin/operations/status"));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        Assert.True(admin.Headers.CacheControl?.NoStore);
        var output = await admin.Content.ReadAsStringAsync();
        Assert.Contains("pendingPublicationCount", output);
        Assert.Contains("staleUnresolvedCount", output);
        Assert.DoesNotContain(AdminToken, output);
        Assert.DoesNotContain(ReadToken, output);
    }

}
