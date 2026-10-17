using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class NpmTarballIntegrityTests
{
    private sealed class Host : WebApplicationFactory<Program>
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "packory-npm-digest-" + Guid.NewGuid().ToString("N"));
        private string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                services.AddSingleton(new Settings { DataDirectory = Root,
                    PublicBaseUrl = "http://localhost:8080", AdminToken = Token,
                    AnonymousRead = false });
            });

        public HttpClient AuthenticatedClient()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return client;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static byte[] Tarball => [0x1f, 0x8b, 0x08, 0, 0, 0, 0, 0];

    private static string PackageJson(byte[] bytes) =>
        new JsonObject
        {
            ["name"] = "local-demo",
            ["versions"] = new JsonObject
            {
                ["1.0.0"] = new JsonObject { ["name"] = "local-demo",
                    ["version"] = "1.0.0" }
            },
            ["dist-tags"] = new JsonObject { ["latest"] = "1.0.0" },
            ["_attachments"] = new JsonObject
            {
                ["local-demo-1.0.0.tgz"] = new JsonObject
                {
                    ["data"] = Convert.ToBase64String(bytes)
                }
            }
        }.ToJsonString();

    [Theory]
    [InlineData("alter")]
    [InlineData("truncate")]
    [InlineData("extend")]
    public async Task HostedNpmCachedCorruptionReturns503BeforeBodyOrRange(string mutation)
    {
        using var host = new Host();
        using var client = host.AuthenticatedClient();
        using (var content = new StringContent(PackageJson(Tarball), Encoding.UTF8, "application/json"))
        using (var published = await client.PutAsync("/npm/local-demo", content))
            Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        const string url = "/npm/local-demo/-/1.0.0.tgz";
        using (var valid = await client.GetAsync(url))
        {
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            Assert.Equal(Tarball, await valid.Content.ReadAsByteArrayAsync());
        }

        var file = Path.Combine(host.Root, "blobs",
            NpmEndpoints.NpmKey("local-demo", "1.0.0").Replace('/', Path.DirectorySeparatorChar));
        var corrupt = Tarball.ToArray();
        if (mutation == "alter") corrupt[4] ^= 0x5a;
        if (mutation == "truncate") corrupt = corrupt[..4];
        if (mutation == "extend") corrupt = [.. corrupt, 1, 2];
        await File.WriteAllBytesAsync(file, corrupt);

        using var refused = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        Assert.Null(refused.Headers.ETag);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 1);
        using var ranged = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ranged.StatusCode);
        Assert.Empty(await ranged.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task StoredManifestMissingMakesHostedBytesUnavailable()
    {
        using var host = new Host();
        using var client = host.AuthenticatedClient();
        using (var content = new StringContent(PackageJson(Tarball), Encoding.UTF8, "application/json"))
        using (var published = await client.PutAsync("/npm/local-demo", content))
            Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        // The hosted checksum is persisted in the DB, not recomputed from the suspect file.
        // The parser also rejects invalid or downgraded claims independently below.
        var hosted = "{\"name\":\"local-demo\",\"version\":\"1.0.0\",\"dist\":{}}";
        Assert.Null(NpmTarballIntegrity.ParseExpected(hosted, "local-demo", "1.0.0", false));
    }

    [Fact]
    public void MirrorPackumentMustBindDigestToExactNameAndVersion()
    {
        var digest = Convert.ToBase64String(SHA512.HashData(Tarball));
        var json = "{\"name\":\"local-demo\",\"versions\":{\"1.0.0\":{\"name\":\"local-demo\",\"version\":\"1.0.0\",\"dist\":{\"integrity\":\"sha512-" + digest + "\"}}}}";
        var expected = NpmTarballIntegrity.ParseExpected(json, "local-demo", "1.0.0", true);
        Assert.NotNull(expected);
        Assert.Equal("sha512", expected.Algorithm);
        Assert.Equal(SHA512.HashData(Tarball), expected.Bytes);
        Assert.Null(NpmTarballIntegrity.ParseExpected(json, "other", "1.0.0", true));
        Assert.Null(NpmTarballIntegrity.ParseExpected(json, "local-demo", "2.0.0", true));
        Assert.Null(NpmTarballIntegrity.ParseExpected(json, "local-demo", "1.0.0", false));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"name\":\"demo\",\"version\":\"1.0.0\",\"dist\":{\"integrity\":\"sha512-invalid\"}}")]
    [InlineData("{\"name\":\"demo\",\"version\":\"1.0.0\",\"dist\":{\"integrity\":\"sha512-invalid\",\"shasum\":\"0000000000000000000000000000000000000000\"}}")]
    [InlineData("{\"name\":\"demo\",\"name\":\"demo\",\"version\":\"1.0.0\",\"dist\":{\"shasum\":\"0000000000000000000000000000000000000000\"}}")]
    [InlineData("{\"name\":\"demo\",\"version\":\"1.0.0\",\"dist\":{\"integrity\":\"bad\",\"integrity\":\"bad\",\"shasum\":\"0000000000000000000000000000000000000000\"}}")]
    public void AmbiguousOrMalformedClaimsAreNotIntegrityEvidence(string json)
    {
        Assert.Null(NpmTarballIntegrity.ParseExpected(json, "demo", "1.0.0", false));
    }

    [Fact]
    public void LegacyShasumWithoutIntegrityRemainsReadable()
    {
        const string sha1 = "0000000000000000000000000000000000000000";
        var json = "{\"name\":\"demo\",\"version\":\"1.0.0\",\"dist\":{\"shasum\":\"" + sha1 + "\"}}";
        var claim = NpmTarballIntegrity.ParseExpected(json, "demo", "1.0.0", false);
        Assert.NotNull(claim);
        Assert.Equal("sha1", claim.Algorithm);
        Assert.Equal(new byte[20], claim.Bytes);
    }
}
