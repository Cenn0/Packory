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

public sealed class RawArtifactTests
{
    private const string Admin = "raw-test-admin-token-of-sufficient-length";
    private const string Read = "raw-test-reader-token-of-sufficient-length";
    private const string Write = "raw-test-writer-token-of-sufficient-length";

    private sealed class Host(Action<Settings>? configure = null) : WebApplicationFactory<Program>
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(), "packory-raw-tests-" + Guid.NewGuid().ToString("N"));
        public string DataDirectory => _data;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Packory:DataDirectory"] = _data,
                    ["Packory:PublicBaseUrl"] = "http://localhost:8080",
                    ["Packory:AdminToken"] = Admin,
                    ["Packory:ReadToken"] = Read,
                    ["Packory:WriteToken"] = Write,
                    ["Packory:AnonymousRead"] = "false"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                var cfg = new Settings
                {
                    DataDirectory = _data,
                    PublicBaseUrl = "http://localhost:8080",
                    AdminToken = Admin,
                    ReadToken = Read,
                    WriteToken = Write,
                    AnonymousRead = false,
                    McpEnabled = false,
                    MaxArtifactBytes = 1024 * 1024
                };
                configure?.Invoke(cfg);
                cfg.Validate();
                services.AddSingleton(cfg);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_data))
                Directory.Delete(_data, recursive: true);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string url,
        string token, byte[]? bytes = null, string? mediaType = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (bytes is not null)
        {
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType ?? "application/octet-stream");
        }
        return request;
    }

    [Fact]
    public async Task HostedRaw_AuthenticatesStoresImmutablyAndRejectsTampering()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var bytes = Encoding.UTF8.GetBytes("synthetic raw fixture version one");
        var uri = "/raw/demo.artifact/1.0.0";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(uri)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Request(HttpMethod.Put, uri, Read, bytes))).StatusCode);
        using var published = await client.SendAsync(Request(HttpMethod.Put, uri, Write, bytes));
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        var data = await published.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(hash, data.GetProperty("sha256").GetString());
        Assert.Equal(bytes.LongLength, data.GetProperty("size").GetInt64());

        using var duplicate = await client.SendAsync(Request(HttpMethod.Put, uri, Write, bytes));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var fetched = await client.SendAsync(Request(HttpMethod.Get, uri, Read));
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(bytes, await fetched.Content.ReadAsByteArrayAsync());
        Assert.Equal("\"" + hash + "\"", fetched.Headers.ETag?.ToString());
        Assert.True(fetched.Content.Headers.ContentDisposition?.DispositionType == "attachment");

        using var metadata = await client.SendAsync(Request(HttpMethod.Get, uri + "/metadata", Read));
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        var stored = await metadata.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Equal(hash, stored.GetProperty("sha256").GetString());

        using var head = await client.SendAsync(Request(HttpMethod.Head, uri, Read));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);

        var ifNoneMatch = Request(HttpMethod.Get, uri, Read);
        ifNoneMatch.Headers.IfNoneMatch.ParseAdd("\"" + hash + "\"");
        using (ifNoneMatch)
        using (var notModified = await client.SendAsync(ifNoneMatch))
            Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        // Corruption must never be served even if the catalog still refers to the version.
        var payload = Path.Combine(host.DataDirectory, "blobs", "raw", "demo.artifact", "1.0.0", "payload.blob");
        await File.WriteAllTextAsync(payload, "tampered");
        using var corrupted = await client.SendAsync(Request(HttpMethod.Get, uri, Read));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, corrupted.StatusCode);
    }

    [Fact]
    public async Task Raw_EnforcesNamespaceBoundariesAndRejectsInvalidInput()
    {
        using var host = new Host(cfg => cfg.WriteNamespaces = ["raw:approved.*"]);
        using var client = host.CreateClient();
        var bytes = Encoding.UTF8.GetBytes("fixture");
        using var blocked = await client.SendAsync(Request(HttpMethod.Put,
            "/raw/other.package/1.0.0", Write, bytes));
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        using var approved = await client.SendAsync(Request(HttpMethod.Put,
            "/raw/approved.package/1.0.0", Write, bytes));
        Assert.Equal(HttpStatusCode.Created, approved.StatusCode);

        using var invalid = await client.SendAsync(Request(HttpMethod.Put,
            "/raw/double..dot/1.0.0", Admin, bytes));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var badMedia = await client.SendAsync(Request(HttpMethod.Put,
            "/raw/approved.package/2.0.0", Admin, bytes, "text/plain"));
        Assert.Equal(HttpStatusCode.Created, badMedia.StatusCode);

        using var withheld = await client.SendAsync(Request(HttpMethod.Get,
            "/raw/approved.package/2.0.0", Read));
        Assert.Equal(HttpStatusCode.OK, withheld.StatusCode);
        Assert.Equal("attachment", withheld.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task ScopedManagedTokenCannotReadGlobalRawArtifacts()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var bytes = Encoding.UTF8.GetBytes("synthetic scoped access test");
        var uri = "/raw/global.asset/1.0.0";
        using var create = await client.SendAsync(Request(HttpMethod.Put, uri, Admin, bytes));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var token = new string('b', 64);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        db.CreateManagedToken(new ManagedTokenRecord(
            Guid.NewGuid().ToString("N"), digest, "read",
            "feed:private-feed:read", DateTimeOffset.UtcNow.AddHours(1).ToString("O")));
        using var scopedBytes = await client.SendAsync(Request(HttpMethod.Get, uri, token));
        Assert.Equal(HttpStatusCode.Forbidden, scopedBytes.StatusCode);
        using var scopedMetadata = await client.SendAsync(Request(HttpMethod.Get, uri + "/metadata", token));
        Assert.Equal(HttpStatusCode.Forbidden, scopedMetadata.StatusCode);
        using var unscopedRead = await client.SendAsync(Request(HttpMethod.Get, uri, Read));
        Assert.Equal(HttpStatusCode.OK, unscopedRead.StatusCode);
    }

    [Fact]
    public void Raw_DatabaseMigrationAndRecoveryReferencesRemainVerifiable()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-raw-schema-" + Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Database(new Settings { DataDirectory = root });
            db.Initialize();
            Assert.Equal(7, SchemaVersionGuard.Current);
            Assert.True(db.PublishRaw("example", "1.0.0", "application/octet-stream",
                new string('a', 64), 2, "synthetic"));
            db.Initialize();
            Assert.Equal("hosted", db.PackageOrigin("raw", "example", "1.0.0"));
            Assert.Equal(2, db.GetRawArtifact("example", "1.0.0")?.Size);
            Assert.Contains(db.StoredArtifacts(), x => x.Kind == "raw" && x.Name == "example");
            Assert.False(db.PublishRaw("example", "1.0.0", "application/octet-stream",
                new string('a', 64), 2, "synthetic"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CapabilityInventoryNeverPromotesExperimentalAdaptersToProduction()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        using var response = await client.GetAsync("/api/v1/registry-capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.False(payload.GetProperty("productionRecommended").GetBoolean());
        var protocols = payload.GetProperty("protocols").EnumerateArray().ToArray();
        var raw = Assert.Single(protocols, x => x.GetProperty("ecosystem").GetString() == "raw");
        Assert.True(raw.GetProperty("hosted").GetBoolean());
        Assert.False(raw.GetProperty("proxy").GetBoolean());
        Assert.False(raw.GetProperty("nativePublish").GetBoolean());
        Assert.False(raw.GetProperty("isolatedHostedFeed").GetBoolean());
        Assert.All(protocols.Where(entry => entry.GetProperty("ecosystem").GetString() != "go"),
            entry => Assert.Equal("experimental", entry.GetProperty("level").GetString()));
        var go = Assert.Single(protocols, x => x.GetProperty("ecosystem").GetString() == "go");
        Assert.Equal("native-client-tested", go.GetProperty("level").GetString());
        Assert.False(go.GetProperty("nativePublish").GetBoolean());
        Assert.False(go.GetProperty("proxy").GetBoolean());
    }

    [Theory]
    [InlineData("valid.component", true)]
    [InlineData("scope-with-dash", true)]
    [InlineData("name..escaped", false)]
    [InlineData(".prefix", false)]
    [InlineData("Uppercase", false)]
    [InlineData("../escape", false)]
    [InlineData("with space", false)]
    public void RawNameValidationIsCanonical(string input, bool expected) =>
        Assert.Equal(expected, RawEndpoints.ValidName(input));
}
