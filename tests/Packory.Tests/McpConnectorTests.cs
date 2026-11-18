using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Packory;
using ModelContextProtocol;
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
        public Exception? DiscoveryError { get; set; }
        public Exception? CallError { get; set; }
        public Action? BeforeDiscovery { get; set; }
        public Action? BeforeCall { get; set; }
        public Task<IReadOnlyList<McpConnectorToolSummary>> DiscoverAsync(
            McpConnectorRecord connector, CancellationToken cancellationToken)
        {
            DiscoverCalls++;
            BeforeDiscovery?.Invoke();
            if (DiscoveryError is not null)
                return Task.FromException<IReadOnlyList<McpConnectorToolSummary>>(DiscoveryError);
            return Task.FromResult<IReadOnlyList<McpConnectorToolSummary>>(
                [new("query_readonly", "Synthetic tool", connector.AllowedTools.Contains("query_readonly"))]);
        }
        public Task<JsonElement> CallAsync(McpConnectorRecord connector, string tool,
            JsonElement arguments, CancellationToken cancellationToken)
        {
            ToolCalls++;
            BeforeCall?.Invoke();
            if (CallError is not null) return Task.FromException<JsonElement>(CallError);
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
        // An old OIDC grant must never attach to a new connector that reuses the ID.
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(WithToken(
            HttpMethod.Post, path, Admin, Payload))).StatusCode);
        // Simulate upgrading the earlier preview: its audit survives a deletion,
        // but the permanent-ID table did not yet exist.
        using (var metadata = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(Path.GetDirectoryName(host.Keyring)!, "packory.sqlite") }.ToString()))
        {
            metadata.Open();
            using var command = metadata.CreateCommand();
            command.CommandText = "DROP TABLE mcp_connector_ids";
            command.ExecuteNonQuery();
        }
        host.Services.GetRequiredService<McpConnectorStore>().Initialize();
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(WithToken(
            HttpMethod.Post, path, Admin, Payload))).StatusCode);
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
    public async Task DisabledConnectorCannotDiscoverThroughMcpEvenWithScopedGrant()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string root = "/api/v1/admin/mcp/connectors";
        using var created = await client.SendAsync(WithToken(HttpMethod.Post, root, Admin, Payload));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Bearer synthetic-scoped-connector-token";
        Access.RecordValidatedJwt(ctx, new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("scope", "packory.read packory.connector.call"),
            new Claim("packory_connector", "team-database:query_readonly")
        ], "jwt")));
        var scopedSettings = new Settings
        {
            OidcAuthority = "https://issuer.example.org",
            OidcAudience = "synthetic-resource"
        };
        Assert.True(Access.HasConnectorGrant(ctx, scopedSettings, "team-database"));
        var tools = new McpConnectorMcpTools(
            new HttpContextAccessor { HttpContext = ctx }, scopedSettings,
            host.Services.GetRequiredService<McpConnectorStore>(), host.Runtime);

        // A disabled connector must not use its saved bearer to contact a remote MCP server.
        await Assert.ThrowsAsync<McpProtocolException>(
            () => tools.ListConnectorTools("team-database"));
        Assert.Equal(0, host.Runtime.DiscoverCalls);

        // Administrators can still explicitly test a disabled endpoint via the REST probe.
        using var probe = await client.SendAsync(WithToken(
            HttpMethod.Post, root + "/team-database/probe", Admin));
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode);
        Assert.Equal(1, host.Runtime.DiscoverCalls);

        var update = JsonSerializer.Serialize(new
        {
            id = "team-database", name = "Synthetic database MCP",
            endpoint = "https://mcp.example.org/mcp", enabled = true,
            allowedTools = new[] { "query_readonly" }, bearerToken = (string?)null,
            clearBearerToken = false, revision = 1
        });
        using var enabled = await client.SendAsync(WithToken(
            HttpMethod.Put, root + "/team-database", Admin, update));
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        await tools.ListConnectorTools("team-database");
        Assert.Equal(2, host.Runtime.DiscoverCalls);
    }

    [Fact]
    public async Task HostileRemoteProtocolErrorsAreOpaqueForMcpAndRest()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string root = "/api/v1/admin/mcp/connectors";
        using var created = await client.SendAsync(WithToken(HttpMethod.Post, root, Admin, Payload));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var update = JsonSerializer.Serialize(new
        {
            id = "team-database", name = "Synthetic remote MCP",
            endpoint = "https://mcp.example.org/mcp", enabled = true,
            allowedTools = new[] { "query_readonly" }, bearerToken = (string?)null,
            clearBearerToken = false, revision = 1
        });
        using var enabled = await client.SendAsync(WithToken(HttpMethod.Put,
            root + "/team-database", Admin, update));
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        host.Runtime.DiscoveryError = new McpProtocolException(
            "Upstream credential " + Secret + "; SELECT private_data FROM synthetic_table", McpErrorCode.InternalError);
        host.Runtime.CallError = host.Runtime.DiscoveryError;
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Bearer " + Admin;
        var settings = host.Services.GetRequiredService<Settings>();
        Assert.Null(Access.RequireAdmin(ctx, settings));
        var tools = new McpConnectorMcpTools(new HttpContextAccessor { HttpContext = ctx }, settings,
            host.Services.GetRequiredService<McpConnectorStore>(), host.Runtime,
            host.Services.GetRequiredService<ConnectorOperationGate>(),
            host.Services.GetRequiredService<PostgresSingleWriterLease>());
        var discovery = await Assert.ThrowsAsync<McpProtocolException>(() => tools.ListConnectorTools("team-database"));
        var call = await Assert.ThrowsAsync<McpProtocolException>(() =>
            tools.CallConnectorTool("team-database", "query_readonly", "{}"));
        foreach (var error in new[] { discovery, call })
        {
            Assert.Equal("Remote connector failed or is unavailable", error.Message);
            Assert.DoesNotContain(Secret, error.Message);
            Assert.DoesNotContain("SELECT", error.Message);
        }
        using var rest = await client.SendAsync(WithToken(HttpMethod.Post,
            root + "/team-database/probe", Admin));
        Assert.Equal(HttpStatusCode.BadGateway, rest.StatusCode);
        var body = await rest.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Secret, body);
        Assert.DoesNotContain("SELECT", body);
    }

    private static McpConnectorMcpTools EnabledTools(Host host)
    {
        var store = host.Services.GetRequiredService<McpConnectorStore>();
        store.Save(new McpConnectorChange("team-database", "Synthetic remote MCP",
            "https://mcp.example.org/mcp", true, ["query_readonly"], Secret, false, 0), "admin-token");
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Bearer " + Admin;
        var settings = host.Services.GetRequiredService<Settings>();
        Assert.Null(Access.RequireAdmin(ctx, settings));
        return new McpConnectorMcpTools(new HttpContextAccessor { HttpContext = ctx }, settings,
            store, host.Runtime, host.Services.GetRequiredService<ConnectorOperationGate>(),
            host.Services.GetRequiredService<PostgresSingleWriterLease>());
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("deadline")]
    [InlineData("busy")]
    public async Task RemoteRuntimeTimeoutsAndBusyErrorsHaveOpaqueResponses(string failure)
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var tools = EnabledTools(host);
        var sensitiveMessage = Secret + "; SELECT private_data FROM synthetic_table";
        host.Runtime.DiscoveryError = failure switch
        {
            "timeout" => new TimeoutException(sensitiveMessage),
            "deadline" => new OperationCanceledException(sensitiveMessage),
            _ => new ConnectorBusyException()
        };
        host.Runtime.CallError = host.Runtime.DiscoveryError;
        var expectedMessage = failure == "busy" ? "Connector is busy; retry shortly" : "Remote MCP request timed out";
        var discovery = await Assert.ThrowsAsync<McpProtocolException>(() => tools.ListConnectorTools("team-database"));
        var call = await Assert.ThrowsAsync<McpProtocolException>(() =>
            tools.CallConnectorTool("team-database", "query_readonly", "{}"));
        Assert.Equal(expectedMessage, discovery.Message);
        Assert.Equal(expectedMessage, call.Message);
        foreach (var operation in new[] { "probe", "call" })
        {
            using var rest = await client.SendAsync(WithToken(HttpMethod.Post,
                "/api/v1/admin/mcp/connectors/team-database/" + operation, Admin,
                operation == "call" ? "{\"tool\":\"query_readonly\",\"arguments\":{}}" : null));
            Assert.Equal(failure == "busy" ? HttpStatusCode.TooManyRequests : HttpStatusCode.GatewayTimeout, rest.StatusCode);
            using var body = JsonDocument.Parse(await rest.Content.ReadAsStringAsync());
            Assert.Equal(expectedMessage + ".", body.RootElement.GetProperty("error").GetString());
            if (failure == "busy") Assert.Equal(TimeSpan.FromSeconds(2), rest.Headers.RetryAfter!.Delta);
        }
    }

    [Fact]
    public async Task ContendedAdmissionGateReturnsExplicitBusyErrorsWithoutCallingRemote()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var tools = EnabledTools(host);
        using var held = await host.Services.GetRequiredService<ConnectorOperationGate>()
            .EnterAsync("mcp:team-database", CancellationToken.None);
        var discovery = Assert.ThrowsAsync<McpProtocolException>(() => tools.ListConnectorTools("team-database"));
        var call = Assert.ThrowsAsync<McpProtocolException>(() =>
            tools.CallConnectorTool("team-database", "query_readonly", "{}"));
        var restTask = client.SendAsync(WithToken(HttpMethod.Post,
            "/api/v1/admin/mcp/connectors/team-database/probe", Admin));
        await Task.WhenAll(discovery, call, restTask);
        Assert.Equal("Connector is busy; retry shortly", (await discovery).Message);
        Assert.Equal("Connector is busy; retry shortly", (await call).Message);
        using var rest = await restTask;
        Assert.Equal(HttpStatusCode.TooManyRequests, rest.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), rest.Headers.RetryAfter!.Delta);
        Assert.Equal(0, host.Runtime.DiscoverCalls);
        Assert.Equal(0, host.Runtime.ToolCalls);
    }

    [Fact]
    public async Task CallerCancellationRemainsCancellationAtRuntimeAndAdmission()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var tools = EnabledTools(host);
        using var discoveryCancellation = new CancellationTokenSource();
        host.Runtime.BeforeDiscovery = discoveryCancellation.Cancel;
        host.Runtime.DiscoveryError = new OperationCanceledException(discoveryCancellation.Token);
        var discovery = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tools.ListConnectorTools("team-database", discoveryCancellation.Token));
        Assert.Same(host.Runtime.DiscoveryError, discovery);
        using var callCancellation = new CancellationTokenSource();
        host.Runtime.BeforeCall = callCancellation.Cancel;
        host.Runtime.CallError = new OperationCanceledException(callCancellation.Token);
        var call = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tools.CallConnectorTool("team-database", "query_readonly", "{}", callCancellation.Token));
        Assert.Same(host.Runtime.CallError, call);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            tools.ListConnectorTools("team-database", discoveryCancellation.Token));
        Assert.Equal(1, host.Runtime.DiscoverCalls);
        Assert.Equal(1, host.Runtime.ToolCalls);
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
            foreach (var malformed in new[] {
                "{}", "null", "{\"keyId\":null}",
                "{\"keyId\":\"k1\",\"nonce\":null,\"ciphertext\":\"AA==\",\"tag\":\"AA==\"}",
                "{\"keyId\":\"k1\",\"nonce\":\"!\",\"ciphertext\":\"AA==\",\"tag\":\"AA==\"}",
                "{\"keyId\":\"k1\",\"nonce\":\"AA==\",\"ciphertext\":\"AA==\",\"tag\":\"AA==\"}"
            })
                Assert.ThrowsAny<CryptographicException>(() =>
                    secrets.Unprotect(malformed, "one", "https://mcp.example.org/mcp"));
            File.WriteAllText(path, new string(' ', 16385));
            Assert.Throws<McpConnectorKeyringException>(() =>
                secrets.Protect(Secret, "one", "https://mcp.example.org/mcp"));
            File.WriteAllText(path, "{\"active\":\"k1\",\"keys\":{\"k1\":null}}");
            Assert.Throws<McpConnectorKeyringException>(() =>
                secrets.Protect(Secret, "one", "https://mcp.example.org/mcp"));
        }
        finally { Directory.Delete(folder, true); }
    }


    [Theory]
    [InlineData("fixture-key", true)]
    [InlineData("fixture_key", true)]
    [InlineData("Key09_-", true)]
    [InlineData("fixture.key", false)]
    [InlineData("fixture/key", false)]
    [InlineData("fixture:key", false)]
    [InlineData("fixture key", false)]
    [InlineData("fixture\\key", false)]
    [InlineData("fixtureékey", false)]
    public void KeyringIdsAcceptOnlyAsciiLettersDigitsUnderscoreAndHyphen(string keyId, bool valid)
    {
        var folder = Path.Combine(Path.GetTempPath(), "packory-keyring-id-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "ring.json");
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var keys = new Dictionary<string, string> { ["baseline"] = key };
            File.WriteAllText(path, JsonSerializer.Serialize(new { active = "baseline", keys }));
            var secrets = new McpConnectorSecrets(path);
            var previousEnvelope = secrets.Protect(Secret, "one", "https://mcp.example.org/mcp");
            keys.Add(keyId, key);
            File.WriteAllText(path, JsonSerializer.Serialize(new { active = keyId, keys }));
            if (valid)
            {
                var envelope = secrets.Protect(Secret, "one", "https://mcp.example.org/mcp");
                using var json = JsonDocument.Parse(envelope);
                Assert.Equal(keyId, json.RootElement.GetProperty("keyId").GetString());
                Assert.Equal(Secret, secrets.Unprotect(envelope, "one", "https://mcp.example.org/mcp"));
                Assert.Equal(Secret, secrets.Unprotect(previousEnvelope, "one", "https://mcp.example.org/mcp"));
            }
            else
            {
                Assert.Throws<McpConnectorKeyringException>(() =>
                    secrets.Protect(Secret, "one", "https://mcp.example.org/mcp"));
                Assert.Throws<McpConnectorKeyringException>(() =>
                    secrets.Unprotect(previousEnvelope, "one", "https://mcp.example.org/mcp"));
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ScopedMcpCapabilityRequiresValidatedBearerAndExactToolClaim()
    {
        var settings = new Settings
        {
            OidcAuthority = "https://issuer.example.org",
            OidcAudience = "synthetic-resource"
        };
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Bearer synthetic-oidc-token";
        var unverified = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("scope", "packory.read packory.connector.call"),
            new Claim("packory_connector", "team-database:query_readonly")
        ], "jwt"));
        ctx.User = unverified;
        Assert.False(Access.HasConnectorGrant(ctx, settings, "team-database", "query_readonly"));
        Access.RecordValidatedJwt(ctx, unverified);
        Assert.True(Access.HasConnectorGrant(ctx, settings, "team-database", "query_readonly"));
        Assert.True(Access.HasConnectorGrant(ctx, settings, "team-database"));
        Assert.False(Access.HasConnectorGrant(ctx, settings, "team-database", "drop_all"));
        Assert.False(Access.HasConnectorGrant(ctx, settings, "other-database", "query_readonly"));

        ctx.Request.Headers.Authorization = "";
        Assert.False(Access.HasConnectorGrant(ctx, settings, "team-database", "query_readonly"));
        ctx.Request.Headers.Authorization = "Bearer synthetic-oidc-token";

        var noDedicatedScope = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("scope", "packory.read"),
            new Claim("packory_connector", "team-database:query_readonly")
        ], "jwt"));
        Access.RecordValidatedJwt(ctx, noDedicatedScope);
        Assert.False(Access.HasConnectorGrant(ctx, settings, "team-database", "query_readonly"));
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
