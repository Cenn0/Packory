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

public sealed class McpRegistryPackageOnlyApiTests
{
    private const string Admin = "synthetic-mcp-r2-admin-token-with-enough-characters";
    private const string Reader = "synthetic-mcp-r2-reader-token-with-enough-characters";
    private const string Descriptor = """
        {
          "$schema":"https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json",
          "name":"org.example/package-only",
          "version":"1.0.0",
          "description":"Synthetic package-only fixture",
          "packages":[{"registryType":"npm","identifier":"@example/inert-server",
            "version":"1.0.0","transport":{"type":"stdio"}}]
        }
        """;

    private sealed class Host : WebApplicationFactory<Program>
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(),
            "packory-r2-http-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Packory:DataDirectory"] = _data,
                    ["Packory:PublicBaseUrl"] = "http://localhost:8080",
                    ["Packory:AdminToken"] = Admin,
                    ["Packory:ReadToken"] = Reader,
                    ["Packory:AnonymousRead"] = "false"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                var settings = new Settings
                {
                    DataDirectory = _data, PublicBaseUrl = "http://localhost:8080",
                    AdminToken = Admin, ReadToken = Reader, AnonymousRead = false
                };
                settings.Validate();
                services.AddSingleton(settings);
                services.DisableBackgroundSkillScanning();
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_data)) Directory.Delete(_data, true);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string route, string token,
        HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, route) { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    [Fact]
    public async Task PackageOnlyRegistryStagingPublicationDiscoveryAndRevocationAreEndToEnd()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string stage = "/api/v1/admin/mcp/servers/stage";
        const string decide = "/api/v1/admin/mcp/servers/decide";
        const string detail = "/v0.1/servers/org.example%2Fpackage-only/versions/1.0.0";

        using (var anonymous = await client.PostAsync(stage, new StringContent(Descriptor)))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var denied = await client.SendAsync(Request(HttpMethod.Post, stage, Reader,
            new StringContent(Descriptor, Encoding.UTF8, "application/json"))))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var accepted = await client.SendAsync(Request(HttpMethod.Post, stage, Admin,
            new StringContent(Descriptor, Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var acceptedJson = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        var digest = acceptedJson.GetProperty("sha256").GetString()!;
        Assert.Equal(64, digest.Length);

        using (var before = await client.SendAsync(Request(HttpMethod.Get, detail, Reader)))
            Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);
        using var publish = await client.SendAsync(Request(HttpMethod.Post, decide, Admin,
            JsonContent.Create(new
            {
                name = "org.example/package-only",
                version = "1.0.0",
                sha256 = digest,
                action = "publish",
                reason = "Reviewed an inert synthetic package-only fixture"
            })));
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        using (var summary = await client.SendAsync(Request(HttpMethod.Get,
            "/api/v1/mcp/servers?q=package-only", Reader)))
        {
            Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
            var result = await summary.Content.ReadFromJsonAsync<JsonElement>();
            var entry = result.GetProperty("items")[0];
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("remoteType").ValueKind);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("remoteUrl").ValueKind);
            Assert.False(entry.GetProperty("publisherVerified").GetBoolean());
        }
        using (var published = await client.SendAsync(Request(HttpMethod.Get, detail, Reader)))
        {
            Assert.Equal(HttpStatusCode.OK, published.StatusCode);
            var result = await published.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, result.GetProperty("server").GetProperty("packages").GetArrayLength());
            Assert.False(result.GetProperty("_meta")
                .GetProperty(McpRegistryDiscovery.MetadataNamespace)
                .GetProperty("safeToConnect").GetBoolean());
        }
        using (var registry = await client.SendAsync(Request(HttpMethod.Get, "/v0.1/servers", Reader)))
        {
            Assert.Equal(HttpStatusCode.OK, registry.StatusCode);
            var result = await registry.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Single(result.GetProperty("servers").EnumerateArray());
        }
        using (var revoke = await client.SendAsync(Request(HttpMethod.Post, decide, Admin,
            JsonContent.Create(new
            {
                name = "org.example/package-only",
                version = "1.0.0",
                sha256 = digest,
                action = "revoke",
                reason = "Synthetic package-only distribution revoked"
            }))))
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        using var after = await client.SendAsync(Request(HttpMethod.Get, detail, Reader));
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
    }
}
