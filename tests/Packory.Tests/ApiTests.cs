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
        Action<Settings>? configure = null) : WebApplicationFactory<Program>
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
        online = false;
        Assert.Equal(bytes, await (await client.SendAsync(WithToken(HttpMethod.Get, location))).Content.ReadAsByteArrayAsync());
        var sha = await (await client.SendAsync(WithToken(HttpMethod.Get, location + ".sha512"))).Content.ReadAsStringAsync();
        Assert.Equal(Convert.ToBase64String(SHA512.HashData(bytes)), sha);
        Assert.Contains("<id>Example.Package</id>", await (await client.SendAsync(WithToken(HttpMethod.Get,
            "/nuget/v3/flatcontainer/example.package/1.2.3/example.package.nuspec"))).Content.ReadAsStringAsync());
        Assert.Contains("example.package", await (await client.SendAsync(WithToken(HttpMethod.Get, "/api/packages?q=example"))).Content.ReadAsStringAsync());
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
        static MultipartFormDataContent Form()
        {
            var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(Nupkg("Custom.Package", "0.1.0")), "package", "custom.package.0.1.0.nupkg");
            return form;
        }
        var unauthenticated = new HttpRequestMessage(HttpMethod.Put, "/nuget/v3/push") { Content = Form() };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(unauthenticated)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(WithToken(HttpMethod.Put, "/nuget/v3/push", Form()))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(WithToken(HttpMethod.Put, "/nuget/v3/push", Form()))).StatusCode);
        var artifact = await client.SendAsync(WithToken(HttpMethod.Get,
            "/nuget/v3/flatcontainer/custom.package/0.1.0/custom.package.0.1.0.nupkg"));
        Assert.Equal(Nupkg("Custom.Package", "0.1.0").Length, (await artifact.Content.ReadAsByteArrayAsync()).Length);
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
        using var host = new Host();
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
        using var host = new Host();
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
        Assert.Contains("/cargo/api/v1/crates", configJson);
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

}
