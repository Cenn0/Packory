using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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

/// <summary>Offline regression coverage: no external MCP calls or real credentials.</summary>
public sealed class McpConnectorTests
{
    private const string Admin = "synthetic-mcp-connector-admin-credential-long";
    private const string Reader = "synthetic-mcp-connector-reader-credential-long";
    private const string Secret = "synthetic-bearer-not-real";
    private static readonly string Payload = JsonSerializer.Serialize(new
    {
        id = "team-database", name = "Synthetic database MCP",
        endpoint = "https://mcp.example.org/mcp", enabled = false,
        allowedTools = Array.Empty<string>(), bearerToken = Secret,
        clearBearerToken = false, revision = 0
    });

    private sealed class FakeRuntime : IMcpConnectorRuntime
    {
        public int DiscoverCalls { get; private set; }
        public int ToolCalls { get; private set; }
        public Task<IReadOnlyList<McpConnectorToolSummary>> DiscoverAsync(
            McpConnectorRecord connector, CancellationToken cancellationToken)
        {
            DiscoverCalls++;
            return Task.FromResult<IReadOnlyList<McpConnectorToolSummary>>(
                [new("query_readonly", "Synthetic tool", connector.AllowedTools.Contains("query_readonly"))]);
        }
        public Task<JsonElement> CallAsync(McpConnectorRecord connector, string tool,
            JsonElement arguments, CancellationToken cancellationToken)
        {
            ToolCalls++;
            return Task.FromResult(JsonDocument.Parse("{\"ok\":true}").RootElement.Clone());
        }
    }

    private sealed class Host : WebApplicationFactory<Program>
    {
        private readonly string data = Path.Combine(Path.GetTempPath(),
            "packory-connectors-" + Guid.NewGuid().ToString("N"));
        public FakeRuntime Runtime { get; } = new();
        public string Keyring => Path.Combine(data, "keyring.json");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(data);
            File.WriteAllText(Keyring, JsonSerializer.Serialize(new
            {
                active = "k1", keys = new Dictionary<string, string>
                { ["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
            }));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Packory:DataDirectory"] = data,
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
                    DataDirectory = data, PublicBaseUrl = "http://localhost:8080",
                    AdminToken = Admin, ReadToken = Reader, AnonymousRead = false
                };
                settings.Validate();
                services.AddSingleton(settings);
                services.RemoveAll<McpConnectorPolicy>();
                services.AddSingleton(new McpConnectorPolicy(["https://mcp.example.org"]));
                services.RemoveAll<McpConnectorSecrets>();
                services.AddSingleton(new McpConnectorSecrets(Keyring));
                services.RemoveAll<IMcpConnectorRuntime>();
                services.AddSingleton<IMcpConnectorRuntime>(Runtime);
                services.DisableBackgroundSkillScanning();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    private static HttpRequestMessage WithToken(HttpMethod method, string url, string token,
        string? json = null)
    {
        var message = new HttpRequestMessage(method, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null)
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return message;
    }

    [Fact]
    public async Task AdministrativeCrudDoesNotExposeSecretsAndRejectsStaleRevisions()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string path = "/api/v1/admin/mcp/connectors";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(
            WithToken(HttpMethod.Get, path, Reader))).StatusCode);

        using var created = await client.SendAsync(WithToken(HttpMethod.Post, path, Admin, Payload));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var creationText = await created.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, creationText);
        Assert.DoesNotContain("ciphertext", creationText, StringComparison.OrdinalIgnoreCase);
        using (var doc = JsonDocument.Parse(creationText))
            Assert.Equal(1, doc.RootElement.GetProperty("revision").GetInt64());

        var stored = host.Services.GetRequiredService<McpConnectorStore>().Get("team-database")!;
        Assert.NotNull(stored.SecretCiphertext);
        Assert.DoesNotContain(Secret, stored.SecretCiphertext);
        var decrypted = host.Services.GetRequiredService<McpConnectorSecrets>()
            .Unprotect(stored.SecretCiphertext!, stored.Id, stored.Endpoint);
        Assert.Equal(Secret, decrypted);

        using var listed = await client.SendAsync(WithToken(HttpMethod.Get, path, Admin));
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var listText = await listed.Content.ReadAsStringAsync();
        Assert.Contains("team-database", listText);
        Assert.DoesNotContain(Secret, listText);
        Assert.DoesNotContain("ciphertext", listText, StringComparison.OrdinalIgnoreCase);
        using (var doc = JsonDocument.Parse(listText))
            Assert.True(doc.RootElement.GetProperty("items")[0].GetProperty("hasBearerToken").GetBoolean());

        var update = JsonSerializer.Serialize(new {
            id = "team-database", name = "Updated connector",
            endpoint = "https://mcp.example.org/mcp", enabled = true,
            allowedTools = new[] { "query_readonly" }, bearerToken = (string?)null,
            clearBearerToken = false, revision = 1
        });
        using var edited = await client.SendAsync(WithToken(HttpMethod.Put,
            path + "/team-database", Admin, update));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        Assert.Equal(2, host.Services.GetRequiredService<McpConnectorStore>().Get("team-database")!.Revision);

        using var stale = await client.SendAsync(WithToken(HttpMethod.Put,
            path + "/team-database", Admin, update));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(WithToken(
            HttpMethod.Delete, path + "/team-database?revision=1", Admin))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(WithToken(
            HttpMethod.Delete, path + "/team-database?revision=2", Admin))).StatusCode);
        Assert.Null(host.Services.GetRequiredService<McpConnectorStore>().Get("team-database"));
    }

    [Fact]
    public async Task ProbeIsNotPermissionAndOnlyExplicitlyAllowedToolsMayRun()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string root = "/api/v1/admin/mcp/connectors";
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(
            WithToken(HttpMethod.Post, root, Admin, Payload))).StatusCode);
        var probe = await client.SendAsync(WithToken(HttpMethod.Post,
            root + "/team-database/probe", Admin));
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        using (var body = JsonDocument.Parse(await probe.Content.ReadAsStringAsync()))
            Assert.False(body.RootElement.GetProperty("tools")[0].GetProperty("allowed").GetBoolean());
        Assert.Equal(1, host.Runtime.DiscoverCalls);

        var call = "{\"tool\":\"query_readonly\",\"arguments\":{\"query\":\"SELECT 1\"}}";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(WithToken(
            HttpMethod.Post, root + "/team-database/call", Admin, call))).StatusCode);
        Assert.Equal(0, host.Runtime.ToolCalls);

        var update = JsonSerializer.Serialize(new {
            id = "team-database", name = "Test connector",
            endpoint = "https://mcp.example.org/mcp", enabled = true,
            allowedTools = new[] { "query_readonly" }, bearerToken = (string?)null,
            clearBearerToken = false, revision = 1
        });
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(WithToken(
            HttpMethod.Put, root + "/team-database", Admin, update))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(WithToken(
            HttpMethod.Post, root + "/team-database/call", Reader, call))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(WithToken(
            HttpMethod.Post, root + "/team-database/call", Admin,
                "{\"tool\":\"drop_all\",\"arguments\":{}}"))).StatusCode);
        Assert.Equal(0, host.Runtime.ToolCalls);

        using var result = await client.SendAsync(WithToken(HttpMethod.Post,
            root + "/team-database/call", Admin, call));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(1, host.Runtime.ToolCalls);
        Assert.DoesNotContain(Secret, await result.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RejectsUnapprovedOriginsAndCrossSiteRequests()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string path = "/api/v1/admin/mcp/connectors";
        var invalid = Payload.Replace("https://mcp.example.org/mcp",
            "https://169.254.169.254/mcp", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(WithToken(
            HttpMethod.Post, path, Admin, invalid))).StatusCode);
        var external = Payload.Replace("https://mcp.example.org/mcp",
            "http://mcp.example.org/mcp", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(WithToken(
            HttpMethod.Post, path, Admin, external))).StatusCode);
        using var crossSite = WithToken(HttpMethod.Post, path, Admin, Payload);
        crossSite.Headers.Add("Origin", "https://attacker.example.org");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossSite)).StatusCode);
    }

    [Fact]
    public void SecretEnvelopeIsBoundToExactConnectorAndEndpoint()
    {
        var folder = Path.Combine(Path.GetTempPath(), "packory-connector-key-test-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "ring.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                active = "k1", keys = new Dictionary<string, string>
                    { ["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
            }));
            var secrets = new McpConnectorSecrets(path);
            var envelope = secrets.Protect(Secret, "one", "https://mcp.example.org/mcp");
            Assert.DoesNotContain(Secret, envelope);
            Assert.Equal(Secret, secrets.Unprotect(envelope, "one", "https://mcp.example.org/mcp"));
            Assert.ThrowsAny<CryptographicException>(() =>
                secrets.Unprotect(envelope, "two", "https://mcp.example.org/mcp"));
            Assert.ThrowsAny<CryptographicException>(() =>
                secrets.Unprotect(envelope, "one", "https://mcp.example.org/other"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void OperatorEgressPolicyDeniesUnsafeAndPrivateTargets()
    {
        var policy = new McpConnectorPolicy(["https://mcp.example.org"]);
        Assert.Equal("https://mcp.example.org/mcp", policy.ValidateEndpoint(
            "https://mcp.example.org/mcp").AbsoluteUri);
        Assert.False(policy.AddressAllowed(new Uri("https://mcp.example.org/mcp"),
            IPAddress.Parse("127.0.0.1")));
        Assert.False(policy.AddressAllowed(new Uri("https://mcp.example.org/mcp"),
            IPAddress.Parse("169.254.169.254")));
        Assert.False(policy.AddressAllowed(new Uri("https://mcp.example.org/mcp"),
            IPAddress.Parse("10.1.2.3")));
        Assert.Throws<InvalidDataException>(() => policy.ValidateEndpoint(
            "https://mcp.example.org/mcp?token=leak"));
        Assert.Throws<InvalidDataException>(() => policy.ValidateEndpoint(
            "https://unapproved.example.org/mcp"));
        Assert.Throws<InvalidDataException>(() => new McpConnectorPolicy(["http://mcp.example.org"]));
        var privateApproved = new McpConnectorPolicy(["https://mcp.example.org"], ["127.0.0.0/8"]);
        Assert.True(privateApproved.AddressAllowed(new Uri("https://mcp.example.org/mcp"),
            IPAddress.Loopback));
        Assert.False(privateApproved.AddressAllowed(new Uri("https://mcp.example.org/mcp"),
            IPAddress.Parse("169.254.169.254")));
    }
}
