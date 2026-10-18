using System.Net;
using System.Net.Http.Headers;
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

public sealed class McpOAuthMetadataTests
{
    private const string Issuer = "https://identity.example.org/realms/packory";
    private const string BaseUrl = "https://registry.example.org";
    private const string Reader = "synthetic-reader-token-with-32-characters";

    private sealed class Host(bool oidc, bool https) : WebApplicationFactory<Program>
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(),
            "packory-mcp-rfc9728-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Packory:DataDirectory"] = _data,
                    ["Packory:PublicBaseUrl"] = https ? BaseUrl : "http://localhost:8080",
                    ["Packory:ReadToken"] = Reader,
                    ["Packory:AnonymousRead"] = "false"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                var cfg = new Settings
                {
                    DataDirectory = _data,
                    PublicBaseUrl = https ? BaseUrl : "http://localhost:8080",
                    ReadToken = Reader, AnonymousRead = false,
                    OidcAuthority = oidc ? Issuer : null,
                    OidcAudience = oidc ? "packory" : null
                };
                cfg.Validate();
                services.AddSingleton(cfg);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_data)) Directory.Delete(_data, true);
        }
    }

    [Fact]
    public void ResourceMetadataRequiresHttpsOidcAndEnabledMcp()
    {
        var oidc = new Settings
        {
            PublicBaseUrl = BaseUrl, OidcAuthority = Issuer,
            OidcAudience = "packory"
        };
        oidc.Validate();
        Assert.True(McpOAuthResourceMetadata.Enabled(oidc));
        Assert.Equal(BaseUrl + "/mcp", McpOAuthResourceMetadata.Resource(oidc));
        Assert.Equal(BaseUrl + "/.well-known/oauth-protected-resource/mcp",
            McpOAuthResourceMetadata.DocumentUrl(oidc));
        var json = JsonSerializer.Serialize(McpOAuthResourceMetadata.Describe(oidc));
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(BaseUrl + "/mcp",
            parsed.RootElement.GetProperty("resource").GetString());
        Assert.Equal(Issuer,
            parsed.RootElement.GetProperty("authorization_servers")[0].GetString());
        Assert.Equal("packory.read",
            parsed.RootElement.GetProperty("scopes_supported")[0].GetString());
        Assert.Equal("header",
            parsed.RootElement.GetProperty("bearer_methods_supported")[0].GetString());
        Assert.DoesNotContain("client_secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);

        var staticToken = new Settings
        {
            PublicBaseUrl = BaseUrl, ReadToken = Reader
        };
        Assert.False(McpOAuthResourceMetadata.Enabled(staticToken));
        Assert.Null(McpOAuthResourceMetadata.Describe(staticToken));
        Assert.Equal("Bearer realm=\"Packory MCP\"",
            McpOAuthResourceMetadata.Challenge(staticToken));

        var http = new Settings
        {
            PublicBaseUrl = "http://localhost:8080",
            OidcAuthority = Issuer, OidcAudience = "packory"
        };
        http.Validate();
        Assert.False(McpOAuthResourceMetadata.Enabled(http));
        oidc.McpEnabled = false;
        Assert.False(McpOAuthResourceMetadata.Enabled(oidc));
    }

    [Fact]
    public async Task HttpsOidcExposesWellKnownMetadataWithoutClientCredential()
    {
        using var host = new Host(oidc: true, https: true);
        using var client = host.CreateClient();
        var metadata = await client.GetAsync(McpOAuthResourceMetadata.WellKnownPath);
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.Equal("no-store", metadata.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync());
        Assert.Equal(BaseUrl + "/mcp", json.RootElement.GetProperty("resource").GetString());
        Assert.Equal(Issuer,
            json.RootElement.GetProperty("authorization_servers")[0].GetString());

        // Unauthorized clients should discover the correct OAuth resource;
        // this does NOT test the identity provider's OAuth 2.1 behavior.
        using var response = await client.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains("resource_metadata=\"" + BaseUrl +
            McpOAuthResourceMetadata.WellKnownPath + "\"", challenge);
        Assert.Contains("scope=\"packory.read\"", challenge);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task StaticOrHttpConfigurationsDoNotAdvertiseFakeOauth(bool oidc, bool https)
    {
        using var host = new Host(oidc, https);
        using var client = host.CreateClient();
        using var metadata = await client.GetAsync(McpOAuthResourceMetadata.WellKnownPath);
        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        using var response = await client.GetAsync("/mcp");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("resource_metadata",
            response.Headers.WwwAuthenticate.ToString());
    }
}
