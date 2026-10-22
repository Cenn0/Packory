using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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

internal sealed class FeedEcosystemHost : WebApplicationFactory<Program>
{
    internal const string Admin = "synthetic-feed-ecosystem-admin-token";
    internal const string Reader = "synthetic-feed-ecosystem-reader-token";
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "packory-feed-ecosystems-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Packory:DataDirectory"] = Root, ["Packory:PublicBaseUrl"] = "http://localhost:8080",
            ["Packory:AdminToken"] = Admin, ["Packory:ReadToken"] = Reader, ["Packory:AnonymousRead"] = "true"
        }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Settings>();
            var settings = new Settings
            {
                DataDirectory = Root, PublicBaseUrl = "http://localhost:8080", AdminToken = Admin,
                ReadToken = Reader, AnonymousRead = true, McpEnabled = false, MaxArtifactBytes = 1024 * 1024
            };
            settings.Validate();
            services.AddSingleton(settings);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(Root)) Directory.Delete(Root, true);
    }

    internal static HttpRequestMessage Request(HttpMethod method, string path, string? token = null, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    internal static async Task<HttpStatusCode> Status(HttpClient client, HttpMethod method, string path,
        string? token = null, HttpContent? content = null)
    {
        using var request = Request(method, path, token, content);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    internal static async Task Create(HttpClient client, string id, string kind)
    {
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Post, "/api/v1/admin/feeds/drafts", Admin,
            JsonContent.Create(new { id, kind, mode = "hosted", displayName = "Synthetic isolated feed" })));
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post, "/api/v1/admin/feeds/" + id + "/activate", Admin));
    }

    internal static async Task<(string Id, string Token)> Grant(HttpClient client, string id, bool publish)
    {
        using var request = Request(HttpMethod.Post, "/api/admin/tokens", Admin,
            JsonContent.Create(new { role = publish ? "write" : "read", @namespace = "feed:" + id + ":" + (publish ? "publish" : "read"), expiresInHours = 1 }));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (document.RootElement.GetProperty("id").GetString()!, document.RootElement.GetProperty("token").GetString()!);
    }
}

public sealed class FeedCargoTests
{
    private const string Name = "e0-cargo-demo";
    private const string Version = "1.2.3";

    private static byte[] Crate(string body, string version = Version, string? member = null)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            var prefix = Name + "-" + version + "/";
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, prefix + "Cargo.toml")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("[package]\nname=\"" + Name + "\"\nversion=\"" + version + "\"\n"))
            });
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, member ?? prefix + "src/lib.rs")
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(body))
            });
        }
        return output.ToArray();
    }

    private static ByteArrayContent Publish(byte[] crate, string version = Version, object[]? deps = null)
    {
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new
        {
            name = Name, vers = version, deps = deps ?? [], features = new { }, description = "Synthetic E0 fixture"
        });
        var bytes = new byte[8 + metadata.Length + crate.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)metadata.Length);
        metadata.CopyTo(bytes, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(metadata.Length + 4), (uint)crate.Length);
        crate.CopyTo(bytes, metadata.Length + 8);
        return new ByteArrayContent(bytes);
    }

    [Fact]
    public async Task IndependentCratesNativeBareAuthAndExactFeedDenials()
    {
        using var host = new FeedEcosystemHost();
        using var client = host.CreateClient();
        foreach (var id in new[] { "cargo-alpha", "cargo-beta" }) await FeedEcosystemHost.Create(client, id, "cargo");
        var alphaRead = await FeedEcosystemHost.Grant(client, "cargo-alpha", false);
        var alphaWrite = await FeedEcosystemHost.Grant(client, "cargo-alpha", true);
        var betaRead = await FeedEcosystemHost.Grant(client, "cargo-beta", false);
        var betaWrite = await FeedEcosystemHost.Grant(client, "cargo-beta", true);
        var alpha = Crate("pub fn answer() -> u32 { 11 }");
        var beta = Crate("pub fn answer() -> u32 { 22 }");
        var a = "/feeds/cargo-alpha/cargo";
        var b = "/feeds/cargo-beta/cargo";
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Put, a + "/api/v1/crates/new", alphaWrite.Token, Publish(alpha)));
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Put, b + "/api/v1/crates/new", betaWrite.Token, Publish(beta)));
        Assert.Equal(HttpStatusCode.Conflict, await FeedEcosystemHost.Status(client, HttpMethod.Put, a + "/api/v1/crates/new", alphaWrite.Token, Publish(beta)));
        Assert.Equal(HttpStatusCode.Forbidden, await FeedEcosystemHost.Status(client, HttpMethod.Put, a + "/api/v1/crates/new", alphaRead.Token, Publish(beta)));

        foreach (var suffix in new[] { "/index/config.json", "/index/" + CargoEndpoints.IndexPath(Name), "/api/v1/crates/" + Name + "/" + Version + "/download" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await FeedEcosystemHost.Status(client, HttpMethod.Get, a + suffix));
            Assert.Equal(HttpStatusCode.Forbidden, await FeedEcosystemHost.Status(client, HttpMethod.Get, a + suffix, betaRead.Token));
            Assert.Equal(HttpStatusCode.Forbidden, await FeedEcosystemHost.Status(client, HttpMethod.Get, a + suffix, FeedEcosystemHost.Reader));
            using var request = new HttpRequestMessage(HttpMethod.Get, a + suffix);
            request.Headers.TryAddWithoutValidation("Authorization", alphaRead.Token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using (var response = await client.SendAsync(FeedEcosystemHost.Request(HttpMethod.Get, a + "/index/config.json", alphaRead.Token)))
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("auth-required").GetBoolean());
            Assert.Contains(a, doc.RootElement.GetProperty("dl").GetString());
            Assert.Contains(a, doc.RootElement.GetProperty("api").GetString());
        }
        foreach (var (url, token, expected) in new[] { (a, alphaRead.Token, alpha), (b, betaRead.Token, beta) })
        {
            using var response = await client.SendAsync(FeedEcosystemHost.Request(HttpMethod.Get, url + "/api/v1/crates/" + Name + "/" + Version + "/download", token));
            Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(HttpStatusCode.NotFound, await FeedEcosystemHost.Status(client, HttpMethod.Get, "/cargo/index/" + CargoEndpoints.IndexPath(Name)));
        Assert.Equal(HttpStatusCode.NotFound, await FeedEcosystemHost.Status(client, HttpMethod.Get, a + "/index/mi/ss/missing", alphaRead.Token));
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Post, "/api/v1/admin/feeds/cargo-alpha/deactivate", FeedEcosystemHost.Admin));
        Assert.Equal(HttpStatusCode.NotFound, await FeedEcosystemHost.Status(client, HttpMethod.Get, a + "/index/config.json", alphaRead.Token));
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Get, b + "/index/config.json", betaRead.Token));
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Post, "/api/v1/admin/feeds/cargo-alpha/activate", FeedEcosystemHost.Admin));
        Assert.Equal(HttpStatusCode.NoContent, await FeedEcosystemHost.Status(client, HttpMethod.Delete, "/api/admin/tokens/" + alphaRead.Id, FeedEcosystemHost.Admin));
        Assert.Equal(HttpStatusCode.Unauthorized, await FeedEcosystemHost.Status(client, HttpMethod.Get, a + "/index/config.json", alphaRead.Token));
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        Assert.Equal(2, db.FeedPackageAssets("cargo-alpha", "cargo").Count + db.FeedPackageAssets("cargo-beta", "cargo").Count);
        Assert.Empty(db.PendingPublications());
    }

    [Fact]
    public async Task PrivateYankIsNativeFeedScopedDurableAndDoesNotDeleteCrateBytes()
    {
        using var host = new FeedEcosystemHost();
        using var client = host.CreateClient();
        await FeedEcosystemHost.Create(client, "cargo-yank-alpha", "cargo");
        await FeedEcosystemHost.Create(client, "cargo-yank-beta", "cargo");
        var alphaWrite = await FeedEcosystemHost.Grant(client, "cargo-yank-alpha", true);
        var alphaRead = await FeedEcosystemHost.Grant(client, "cargo-yank-alpha", false);
        var betaWrite = await FeedEcosystemHost.Grant(client, "cargo-yank-beta", true);
        var betaRead = await FeedEcosystemHost.Grant(client, "cargo-yank-beta", false);
        var bytesA = Crate("pub fn answer() -> u32 { 41 }");
        var bytesB = Crate("pub fn answer() -> u32 { 42 }");
        var alpha = "/feeds/cargo-yank-alpha/cargo";
        var beta = "/feeds/cargo-yank-beta/cargo";
        foreach (var (path, token, bytes) in new[] {
            (alpha, alphaWrite.Token, bytesA), (beta, betaWrite.Token, bytesB) })
            Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(
                client, HttpMethod.Put, path + "/api/v1/crates/new", token, Publish(bytes)));

        var index = "/index/" + CargoEndpoints.IndexPath(Name);
        var versionPath = "/api/v1/crates/" + Name + "/" + Version;
        async Task<(bool Yanked, string? ETag)> State(string feed, string credential)
        {
            using var response = await client.SendAsync(FeedEcosystemHost.Request(
                HttpMethod.Get, feed + index, credential));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var data = JsonDocument.Parse((await response.Content.ReadAsStringAsync()).Trim());
            return (data.RootElement.GetProperty("yanked").GetBoolean(),
                response.Headers.ETag?.Tag);
        }

        var beforeA = await State(alpha, alphaRead.Token);
        Assert.False(beforeA.Yanked);
        Assert.False((await State(beta, betaRead.Token)).Yanked);
        var yank = alpha + versionPath + "/yank";
        Assert.Equal(HttpStatusCode.Unauthorized, await FeedEcosystemHost.Status(
            client, HttpMethod.Delete, yank));
        Assert.Equal(HttpStatusCode.Forbidden, await FeedEcosystemHost.Status(
            client, HttpMethod.Delete, yank, alphaRead.Token));
        Assert.Equal(HttpStatusCode.Forbidden, await FeedEcosystemHost.Status(
            client, HttpMethod.Delete, yank, betaWrite.Token));
        Assert.Equal(HttpStatusCode.NotFound, await FeedEcosystemHost.Status(
            client, HttpMethod.Delete, alpha + "/api/v1/crates/" + Name + "/9.9.9/yank", alphaWrite.Token));
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(
            client, HttpMethod.Delete, yank, alphaWrite.Token));

        var afterA = await State(alpha, alphaRead.Token);
        Assert.True(afterA.Yanked);
        Assert.NotEqual(beforeA.ETag, afterA.ETag);
        Assert.False((await State(beta, betaRead.Token)).Yanked);
        foreach (var (path, token, bytes) in new[] {
            (alpha, alphaRead.Token, bytesA), (beta, betaRead.Token, bytesB) })
        {
            using var download = await client.SendAsync(FeedEcosystemHost.Request(
                HttpMethod.Get, path + versionPath + "/download", token));
            Assert.Equal(HttpStatusCode.OK, download.StatusCode);
            Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
        }

        var settings = host.Services.GetRequiredService<Settings>();
        var reopened = new Database(settings);
        reopened.Initialize();
        Assert.True(reopened.FeedCargoYanked("cargo-yank-alpha", Name, Version));
        Assert.Null(reopened.FeedCargoYanked("cargo-yank-beta", Name, Version));
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(
            client, HttpMethod.Put, alpha + versionPath + "/unyank", alphaWrite.Token));
        Assert.False((await State(alpha, alphaRead.Token)).Yanked);
        Assert.False(reopened.FeedCargoYanked("cargo-yank-alpha", Name, Version));
    }

    [Fact]
    public async Task CorruptionAndInvalidArchivesNeverBecomeServable()
    {
        using var host = new FeedEcosystemHost();
        using var client = host.CreateClient();
        await FeedEcosystemHost.Create(client, "cargo-safe", "cargo");
        var write = await FeedEcosystemHost.Grant(client, "cargo-safe", true);
        var url = "/feeds/cargo-safe/cargo/api/v1/crates/new";
        Assert.Equal(HttpStatusCode.BadRequest, await FeedEcosystemHost.Status(client, HttpMethod.Put, url, write.Token, Publish([0x1f, 0x8b, 0, 1])));
        Assert.Equal(HttpStatusCode.BadRequest, await FeedEcosystemHost.Status(client, HttpMethod.Put, url, write.Token, Publish(Crate("unsafe", member: "../escape"))));
        var external = new object[] { new { name = "other", version_req = "^1", features = Array.Empty<string>(), optional = false, default_features = true, registry = "https://registry.example/index" } };
        Assert.Equal(HttpStatusCode.BadRequest, await FeedEcosystemHost.Status(client, HttpMethod.Put, url, write.Token, Publish(Crate("synthetic"), deps: external)));
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Put, url, write.Token, Publish(Crate("synthetic"))));
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var entry = Assert.Single(db.FeedPackageAssets("cargo-safe", "cargo"));
        var blobs = host.Services.GetRequiredService<BlobStore>();
        await File.WriteAllTextAsync(blobs.Existing(FeedAssetEntry.BlobKey(entry))!, "corrupt");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await FeedEcosystemHost.Status(client, HttpMethod.Get,
            "/feeds/cargo-safe/cargo/index/" + CargoEndpoints.IndexPath(Name), write.Token));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await FeedEcosystemHost.Status(client, HttpMethod.Get,
            "/feeds/cargo-safe/cargo/api/v1/crates/" + Name + "/" + Version + "/download", write.Token));
    }

    [Fact]
    public async Task SemverBuildMetadataCannotDuplicateIndexRevision()
    {
        using var host = new FeedEcosystemHost();
        using var client = host.CreateClient();
        await FeedEcosystemHost.Create(client, "cargo-build", "cargo");
        var write = await FeedEcosystemHost.Grant(client, "cargo-build", true);
        var url = "/feeds/cargo-build/cargo/api/v1/crates/new";
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Put, url, write.Token, Publish(Crate("first"))));
        Assert.Equal(HttpStatusCode.Conflict, await FeedEcosystemHost.Status(client, HttpMethod.Put, url, write.Token,
            Publish(Crate("second", Version + "+build"), Version + "+build")));
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3-alpha.1+build.1", true)]
    [InlineData("01.2.3", false)]
    [InlineData("1.2.3-01", false)]
    [InlineData("1.2.3\n", false)]
    public void CargoCoordinatesUseSemver(string version, bool valid) => Assert.Equal(valid, FeedCargoEndpoints.IsVersion(version));
}
