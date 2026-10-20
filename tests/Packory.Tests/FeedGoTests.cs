using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

public sealed class FeedGoTests
{
    private const string Admin = "private-go-feed-test-admin-token-long-enough";
    private const string Reader = "private-go-feed-test-reader-token-long-enough";
    private const string Writer = "private-go-feed-test-writer-token-long-enough";
    private const string Module = "example.com/private-go";
    private const string Version = "v1.2.3";
    private const string Mod = "module example.com/private-go\n\ngo 1.23\n";

    private sealed class Host : WebApplicationFactory<Program>
    {
        private readonly string root = Path.Combine(Path.GetTempPath(),
            "packory-e0-private-go-" + Guid.NewGuid().ToString("N"));
        public string Root => root;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Packory:DataDirectory"] = root,
                    ["Packory:PublicBaseUrl"] = "http://localhost:8080",
                    ["Packory:AdminToken"] = Admin,
                    ["Packory:WriteToken"] = Writer,
                    ["Packory:ReadToken"] = Reader,
                    ["Packory:AnonymousRead"] = "true"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                var cfg = new Settings
                {
                    DataDirectory = root, PublicBaseUrl = "http://localhost:8080",
                    AdminToken = Admin, WriteToken = Writer, ReadToken = Reader,
                    AnonymousRead = true, McpEnabled = false,
                    MaxArtifactBytes = GoModuleBundle.MaxBundleBytes + 1048576
                };
                cfg.Validate();
                services.AddSingleton(cfg);
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] Zip(string code = "alpha")
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string suffix, byte[] content)
            {
                var entry = archive.CreateEntry(Module + "@" + Version + "/" + suffix);
                entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(content);
            }
            Add("go.mod", Encoding.UTF8.GetBytes(Mod));
            Add("hello/hello.go", Encoding.UTF8.GetBytes(
                "package hello\nfunc Message() string { return \"" + code + "\" }\n"));
        }
        return memory.ToArray();
    }

    private static HttpRequestMessage Auth(HttpMethod method, string path, string? token)
    {
        var result = new HttpRequestMessage(method, path);
        if (token is not null)
            result.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return result;
    }

    private static async Task<HttpStatusCode> Status(HttpClient client, HttpMethod method,
        string path, string? token)
    {
        using var request = Auth(method, path, token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static HttpRequestMessage Publish(string feed, string? token, byte[] zip)
    {
        var request = Auth(HttpMethod.Post, "/feeds/" + feed + "/api/go/modules", token);
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(Module), "module");
        form.Add(new StringContent(Version), "version");
        form.Add(new StringContent("2026-10-20T08:00:00Z"), "time");
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(Mod)), "mod", "go.mod");
        form.Add(new ByteArrayContent(zip), "zip", "module.zip");
        request.Content = form;
        return request;
    }

    private static async Task<string> Token(HttpClient client, string role, string grant)
    {
        using var req = Auth(HttpMethod.Post, "/api/admin/tokens", Admin);
        req.Content = JsonContent.Create(new { role, @namespace = grant, expiresInHours = 1 });
        using var response = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return json.GetProperty("token").GetString()!;
    }

    private static async Task Create(HttpClient client, string feed, string mode = "hosted")
    {
        using var req = Auth(HttpMethod.Post, "/api/v1/admin/feeds/drafts", Admin);
        req.Content = JsonContent.Create(new
        {
            id = feed, kind = "go", mode, displayName = "Synthetic Go"
        });
        using var response = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task SameModuleVersionIsIsolatedAndNativeProxyResourcesAreImmutable()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        await Create(client, "go-alpha");
        await Create(client, "go-beta");
        foreach (var feed in new[] { "go-alpha", "go-beta" })
            Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
                "/api/v1/admin/feeds/" + feed + "/activate", Admin));

        var aRead = await Token(client, "read", "feed:go-alpha:read");
        var bRead = await Token(client, "read", "feed:go-beta:read");
        var aWrite = await Token(client, "write", "feed:go-alpha:publish");
        var bWrite = await Token(client, "write", "feed:go-beta:publish");
        var alphaZip = Zip("alpha");
        var betaZip = Zip("beta");

        using (var denied = Publish("go-beta", aWrite, alphaZip))
        using (var response = await client.SendAsync(denied))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var publish = Publish("go-alpha", aRead, alphaZip))
        using (var response = await client.SendAsync(publish))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var publish = Publish("go-alpha", aWrite, alphaZip))
        using (var response = await client.SendAsync(publish))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal(GoModuleZipInspector.Inspect(new MemoryStream(alphaZip),
                Module + "/@v/" + Version + ".zip").ZipH1,
                body.GetProperty("zipH1").GetString());
        }
        using (var publish = Publish("go-beta", bWrite, betaZip))
        using (var response = await client.SendAsync(publish))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using (var publish = Publish("go-alpha", aWrite, betaZip))
        using (var response = await client.SendAsync(publish))
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var a = "/feeds/go-alpha/go/" + Module + "/@v/" + Version;
        var b = "/feeds/go-beta/go/" + Module + "/@v/" + Version;
        Assert.Equal(HttpStatusCode.Unauthorized,
            await Status(client, HttpMethod.Get, a + ".zip", null));
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Get, a + ".zip", bRead));
        Assert.Equal(HttpStatusCode.NotFound,
            await Status(client, HttpMethod.Get, "/go/" + Module + "/@v/" + Version + ".zip", Reader));
        using (var req = Auth(HttpMethod.Get, a + ".zip", aRead))
        using (var response = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(alphaZip, await response.Content.ReadAsByteArrayAsync());
        }
        using (var req = Auth(HttpMethod.Get, b + ".zip", bRead))
        using (var response = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(betaZip, await response.Content.ReadAsByteArrayAsync());
        }
        using (var req = Auth(HttpMethod.Get, a + ".mod", aRead))
        using (var response = await client.SendAsync(req))
            Assert.Equal(Mod, await response.Content.ReadAsStringAsync());
        using (var req = Auth(HttpMethod.Get, a + ".info", aRead))
        using (var response = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var info = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal(Version, info.GetProperty("Version").GetString());
        }
        Assert.Equal(HttpStatusCode.OK,
            await Status(client, HttpMethod.Head, a + ".zip", aRead));
        using (var req = Auth(HttpMethod.Get,
            "/feeds/go-alpha/go/" + Module + "/@v/list", aRead))
        using (var response = await client.SendAsync(req))
            Assert.Equal(Version + "\n", await response.Content.ReadAsStringAsync());

        var db = host.Services.GetRequiredService<IPackageDatabase>();
        Assert.Equal(2, db.FeedAssets().Count(x => x.Kind == "go"));
        Assert.Equal(new[] { Version }, db.FeedGoVersions("go-alpha", Module));
        Assert.Empty(db.GoModules());
        Assert.Empty(db.PendingPublications());
        Assert.Equal(0, host.Services.GetRequiredService<ArtifactRecovery>().MissingHostedCount());

        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
            "/api/v1/admin/feeds/go-alpha/deactivate", Admin));
        Assert.Equal(HttpStatusCode.NotFound,
            await Status(client, HttpMethod.Get, a + ".zip", aRead));
        Assert.Equal(HttpStatusCode.OK,
            await Status(client, HttpMethod.Get, b + ".zip", bRead));
    }

    [Fact]
    public async Task PrivateProxyDraftCannotActivateAndBundleCorruptionFailsClosed()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        await Create(client, "go-proxy", "proxy");
        Assert.Equal(HttpStatusCode.Conflict, await Status(client, HttpMethod.Post,
            "/api/v1/admin/feeds/go-proxy/activate", Admin));
        await Create(client, "go-private");
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
            "/api/v1/admin/feeds/go-private/activate", Admin));
        var read = await Token(client, "read", "feed:go-private:read");
        var write = await Token(client, "write", "feed:go-private:publish");
        var root = "/feeds/go-private/go/" + Module + "/@v/" + Version;
        using (var req = Publish("go-private", write, Zip()))
        using (var response = await client.SendAsync(req))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            await Status(client, HttpMethod.Get, root + ".invalid", read));
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Get, root + ".zip", Reader));
        var ledger = Assert.Single(host.Services.GetRequiredService<IPackageDatabase>()
            .FeedAssets(), x => x.Kind == "go");
        await File.WriteAllTextAsync(Path.Combine(host.Root, "blobs",
            FeedAssetEntry.BlobKey(ledger).Replace('/', Path.DirectorySeparatorChar)),
            "corrupted bundle");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            await Status(client, HttpMethod.Get, root + ".zip", read));
    }
}
