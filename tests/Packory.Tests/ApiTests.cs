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

    private sealed class FakeUpstream(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handle(request));
    }

    private sealed class Host(Func<HttpRequestMessage, HttpResponseMessage>? upstream = null) : WebApplicationFactory<Program>
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(), "packory-tests-" + Guid.NewGuid().ToString("N"));
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
                services.RemoveAll<Settings>();
                services.AddSingleton(new Settings
                {
                    DataDirectory = _data,
                    PublicBaseUrl = "http://localhost:8080",
                    AdminToken = AdminToken,
                    ReadToken = ReadToken,
                    AnonymousRead = false
                });
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
}
