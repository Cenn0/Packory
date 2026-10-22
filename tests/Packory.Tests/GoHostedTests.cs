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

public sealed class GoHostedTests
{
    private const string Admin = "go-hosted-admin-token-with-sufficient-length";
    private const string Reader = "go-hosted-reader-token-with-sufficient-length";
    private const string Writer = "go-hosted-writer-token-with-sufficient-length";
    private const string Module = "example.com/hello";
    private const string Mod = "module example.com/hello\n\ngo 1.23\n";

    private sealed class Host(Action<Settings>? configure = null) : WebApplicationFactory<Program>
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(),
            "packory-go-hosted-" + Guid.NewGuid().ToString("N"));
        public string Root => _path;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Packory:DataDirectory"] = _path,
                    ["Packory:PublicBaseUrl"] = "http://localhost:8080",
                    ["Packory:AdminToken"] = Admin,
                    ["Packory:WriteToken"] = Writer,
                    ["Packory:ReadToken"] = Reader,
                    ["Packory:AnonymousRead"] = "false"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                var cfg = new Settings
                {
                    DataDirectory = _path,
                    PublicBaseUrl = "http://localhost:8080",
                    AdminToken = Admin, WriteToken = Writer,
                    ReadToken = Reader, AnonymousRead = false,
                    McpEnabled = false,
                    MaxArtifactBytes = GoModuleBundle.MaxBundleBytes + 1048576
                };
                configure?.Invoke(cfg);
                cfg.Validate();
                services.AddSingleton(cfg);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_path))
                Directory.Delete(_path, recursive: true);
        }
    }

    private static byte[] Zip(string module = Module, string version = "v1.2.3",
        string mod = Mod, bool includesMod = true)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (includesMod)
            {
                var goMod = zip.CreateEntry(module + "@" + version + "/go.mod");
                goMod.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var writer = goMod.Open();
                writer.Write(Encoding.UTF8.GetBytes(mod));
            }
            var code = zip.CreateEntry(module + "@" + version + "/hello/hello.go");
            code.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using (var writer = code.Open())
                writer.Write("package hello\nfunc Name() string { return \"ok\" }\n"u8);
        }
        return buffer.ToArray();
    }

    private static HttpRequestMessage Auth(HttpMethod method, string url, string? token)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage Publish(string token, string module = Module,
        string version = "v1.2.3", byte[]? zip = null, string mod = Mod)
    {
        var request = Auth(HttpMethod.Post, "/api/go/modules", token);
        var multi = new MultipartFormDataContent();
        multi.Add(new StringContent(module), "module");
        multi.Add(new StringContent(version), "version");
        multi.Add(new StringContent("2026-10-20T08:00:00Z"), "time");
        multi.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(mod)), "mod", "go.mod");
        multi.Add(new ByteArrayContent(zip ?? Zip(module, version, mod)), "zip", "module.zip");
        request.Content = multi;
        return request;
    }

    private static async Task<HttpResponseMessage> Get(HttpClient client, string url,
        string? token) 
    {
        using var request = Auth(HttpMethod.Get, url, token);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task NativeWireTripletIsAtomicImmutableAndDigestVerified()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string path = "/go/example.com/hello/@v/";
        var archived = Zip();
        using (var unauth = Publish(Reader))
            Assert.Equal(HttpStatusCode.Forbidden,
                (await client.SendAsync(unauth)).StatusCode);
        using (var publish = Publish(Writer))
        using (var response = await client.SendAsync(publish))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            var expected = GoModuleZipInspector.Inspect(new MemoryStream(archived),
                "example.com/hello/@v/v1.2.3.zip").ZipH1;
            Assert.Equal(expected, body.GetProperty("zipH1").GetString());
            Assert.StartsWith("h1:", expected, StringComparison.Ordinal);
        }
        using (var repeat = Publish(Writer))
        using (var response = await client.SendAsync(repeat))
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using (var invalidMajor = await Get(client, path + "v9.9.9.zip", Reader))
            Assert.Equal(HttpStatusCode.BadRequest, invalidMajor.StatusCode);
        using (var missing = await Get(client, path + "v1.9.9.zip", Reader))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using (var noAuth = await Get(client, path + "v1.2.3.zip", null))
            Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);
        using (var resource = await Get(client, path + "v1.2.3.zip", Reader))
        {
            Assert.Equal(HttpStatusCode.OK, resource.StatusCode);
            Assert.Equal(archived, await resource.Content.ReadAsByteArrayAsync());
        }
        using (var mod = await Get(client, path + "v1.2.3.mod", Reader))
        {
            Assert.Equal(HttpStatusCode.OK, mod.StatusCode);
            Assert.Equal(Mod, await mod.Content.ReadAsStringAsync());
        }
        using (var info = await Get(client, path + "v1.2.3.info", Reader))
        {
            Assert.Equal(HttpStatusCode.OK, info.StatusCode);
            var json = await info.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal("v1.2.3", json.GetProperty("Version").GetString());
            Assert.NotNull(json.GetProperty("Time").GetString());
        }
        using (var list = await Get(client, path + "list", Reader))
            Assert.Equal("v1.2.3\n", await list.Content.ReadAsStringAsync());

        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var record = Assert.IsType<GoModuleRevision>(db.GetGoModule(Module, "v1.2.3"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(archived)),
            record.ZipSha256);
        Assert.Equal("hosted", db.PackageOrigin("go", Module, "v1.2.3"));
        Assert.Single(db.GoModules());
        Assert.Empty(db.PendingPublications());
        Assert.Contains(db.AuditEvents(null, 30), x => x.Kind == "go" && x.Version == "v1.2.3");

        var blobPath = Path.Combine(host.Root, "blobs",
            GoModuleBundle.Key(Module, "v1.2.3").Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(blobPath, "corrupted bundle");
        using (var damaged = await Get(client, path + "v1.2.3.zip", Reader))
            Assert.Equal(HttpStatusCode.ServiceUnavailable, damaged.StatusCode);
    }

    [Fact]
    public async Task LatestUsesSemverAndNoPartialReleaseIsVisible()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        foreach (var version in new[] { "v1.10.0", "v1.2.3", "v1.10.0-rc.1", "v1.11.0-rc.1" })
        {
            using var req = Publish(Admin, version: version);
            using var result = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Created, result.StatusCode);
        }
        using var latest = await Get(client, "/go/example.com/hello/@latest", Reader);
        Assert.Equal(HttpStatusCode.OK, latest.StatusCode);
        var payload = await latest.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal("v1.10.0", payload.GetProperty("Version").GetString());
        using var versions = await Get(client, "/go/example.com/hello/@v/list", Reader);
        Assert.Equal("v1.2.3\nv1.10.0-rc.1\nv1.10.0\nv1.11.0-rc.1\n",
            await versions.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InvalidAndUnauthorizedPublicationNeverCreatesMetadata()
    {
        using var host = new Host(cfg => cfg.WriteNamespaces = ["go:example.com/approved*"]);
        using var client = host.CreateClient();
        using (var denied = Publish(Writer))
        using (var response = await client.SendAsync(denied))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using (var invalid = Publish(Admin, zip: Zip(mod: "module example.com/other\n")))
        using (var response = await client.SendAsync(invalid))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using (var forged = Publish(Admin, zip: Zip(module: "example.com/other")))
        using (var response = await client.SendAsync(forged))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using (var invalidVersion = Publish(Admin, version: "v2.0.0"))
        using (var response = await client.SendAsync(invalidVersion))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Services.GetRequiredService<IPackageDatabase>().GoModules());
    }

    [Fact]
    public async Task ManagedScopedTokenCannotReadGlobalGoRegistry()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        using (var publish = Publish(Admin))
        using (var response = await client.SendAsync(publish))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var token = new string('c', 64);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        db.CreateManagedToken(new ManagedTokenRecord(Guid.NewGuid().ToString("N"),
            digest, "read", "feed:private-feed:read",
            DateTimeOffset.UtcNow.AddHours(1).ToString("O")));
        using var read = await Get(client,
            "/go/example.com/hello/@v/v1.2.3.zip", token);
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
    }

    [Fact]
    public void SchemaV7AndInterruptionJournalRemainRecoverable()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-go-schema-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = new Settings { DataDirectory = root };
            var db = new Database(cfg);
            db.Initialize();
            Assert.Equal(14, SchemaVersionGuard.Current);
            var pkg = GoModuleBundle.Create(Module, "v1.2.3",
                Encoding.UTF8.GetBytes(Mod), Zip(),
                DateTimeOffset.Parse("2026-10-20T08:00:00Z"));
            var entry = new GoModuleRevision(Module, "v1.2.3", pkg.BundleSha256,
                pkg.Bundle.LongLength, pkg.ZipSha256, pkg.ZipSize,
                pkg.ModSha256, pkg.ModSize, pkg.PublishedAt);
            var key = GoModuleBundle.Key(Module, "v1.2.3");
            db.BeginPublication(key, "go", Module, "v1.2.3");
            Assert.True(db.PublishGoModule(entry, "test-role"));
            Assert.False(db.PublishGoModule(entry, "test-role"));
            var blobs = new BlobStore(cfg);
            var recovery = new ArtifactRecovery(cfg, db, blobs,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtifactRecovery>.Instance);
            Assert.Equal("missing-hosted-blob", recovery.State(Assert.Single(db.PendingPublications())));
            Assert.Equal(1, recovery.MissingHostedCount());
            db.Initialize();
            Assert.Single(db.GoModules());
            Assert.Equal("hosted", db.PackageOrigin("go", Module, "v1.2.3"));
            Assert.Single(db.PendingPublications());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SemverComparisonIncludesPrereleasesAndIncompatibleSuffix()
    {
        Assert.True(GoModuleBundle.CompareVersions("v1.10.0", "v1.9.99") > 0);
        Assert.True(GoModuleBundle.CompareVersions("v1.2.3", "v1.2.3-rc.2") > 0);
        Assert.True(GoModuleBundle.CompareVersions("v1.2.3-rc.10", "v1.2.3-rc.2") > 0);
        Assert.Equal(0, GoModuleBundle.CompareVersions("v2.0.0+incompatible", "v2.0.0"));
    }
}
