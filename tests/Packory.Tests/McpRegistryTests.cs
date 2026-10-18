using System.Net;
using ModelContextProtocol.Client;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Data.Sqlite;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class McpRegistryTests
{
    private const string AdminToken = "synthetic-admin-token-which-is-long-enough";
    private const string ReaderToken = "synthetic-reader-token-which-is-long-enough";
    private static readonly string ValidJson = $$"""
        {
          "$schema": "{{McpRegistryManifest.Schema}}",
          "name": "org.example/synthetic-server",
          "title": "Inert example",
          "description": "Synthetic metadata fixture only",
          "version": "1.2.3",
          "remotes": [{"type":"streamable-http","url":"https://mcp.example.org/mcp"}],
          "repository": {"source":"github","url":"https://github.com/example/example"}
        }
        """;

    private static byte[] Payload(string json) => Encoding.UTF8.GetBytes(json);

    private sealed class Host : WebApplicationFactory<Program>
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(), "packory-mcp-registry-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Packory:DataDirectory"] = _data,
                    ["Packory:PublicBaseUrl"] = "http://localhost:8080",
                    ["Packory:AnonymousRead"] = "false",
                    ["Packory:AdminToken"] = AdminToken,
                    ["Packory:ReadToken"] = ReaderToken
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                var settings = new Settings
                {
                    DataDirectory = _data,
                    PublicBaseUrl = "http://localhost:8080",
                    AnonymousRead = false,
                    AdminToken = AdminToken,
                    ReadToken = ReaderToken
                };
                settings.Validate();
                services.AddSingleton(settings);
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_data)) Directory.Delete(_data, true);
        }
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string uri, string token,
        string? json = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    [Fact]
    public void AcceptsStrictRemoteOnlyImmutableServerJson()
    {
        var manifest = McpRegistryManifest.Parse(Payload(ValidJson));
        Assert.Equal("org.example/synthetic-server", manifest.Name);
        Assert.Equal("1.2.3", manifest.Version);
        Assert.Equal("https://mcp.example.org/mcp", manifest.RemoteUrl);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Payload(ValidJson))), manifest.Sha256);
        Assert.True(McpRegistryManifest.ValidName(manifest.Name));
        Assert.False(McpRegistryManifest.ValidName("org.example/../private"));
        Assert.False(McpRegistryManifest.ValidVersion("latest"));
    }

    [Theory]
    [InlineData("https://mcp.example.org/mcp", "http://mcp.example.org/mcp")]
    [InlineData("https://mcp.example.org/mcp", "https://127.0.0.1/mcp")]
    [InlineData("https://mcp.example.org/mcp", "https://localhost/mcp")]
    [InlineData("https://mcp.example.org/mcp", "https://host.internal/mcp")]
    [InlineData("https://mcp.example.org/mcp", "https://user:pass@example.com/mcp")]
    [InlineData("https://mcp.example.org/mcp", "https://mcp.example.org/mcp?token=synthetic")]
    [InlineData("https://mcp.example.org/mcp", "https://mcp.example.org/mcp/{tenant}")]
    public void RejectsUnsafeRemoteDescriptors(string original, string replacement)
    {
        Assert.Throws<InvalidDataException>(() =>
            McpRegistryManifest.Parse(Payload(ValidJson.Replace(original, replacement,
                StringComparison.Ordinal))));
    }

    [Fact]
    public void RejectsDuplicatePropertiesUnknownFieldsAndUnpinnedSchemas()
    {
        Assert.Throws<InvalidDataException>(() =>
            McpRegistryManifest.Parse(Payload(ValidJson.Replace(
                "\"title\": \"Inert example\",",
                "\"title\": \"Inert example\", \"title\":\"shadow\",",
                StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() =>
            McpRegistryManifest.Parse(Payload(ValidJson.Replace(
                "\"title\": \"Inert example\",",
                "\"title\": \"Inert example\", \"headers\":{\"Authorization\":\"Bearer synthetic\"},",
                StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() =>
            McpRegistryManifest.Parse(Payload(ValidJson.Replace(
                McpRegistryManifest.Schema, "https://example.org/unpinned"))));
    }

    [Fact]
    public void ReviewedStateIsDurableDigestBoundImmutableAndRevocable()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-registry-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new McpRegistryStore(new Settings { DataDirectory = root });
            store.Initialize();
            var entry = McpRegistryManifest.Parse(Payload(ValidJson));
            Assert.True(store.Stage(entry));
            Assert.False(store.Stage(entry));
            Assert.Null(store.Find(entry.Name, entry.Version));
            Assert.Equal("PENDING", store.Find(entry.Name, entry.Version, admin: true)?.State);
            Assert.Empty(store.List(""));
            Assert.False(store.Decide(entry.Name, entry.Version,
                new string('a', 64), "publish", "Reviewed inert fixture", "admin-token"));
            Assert.False(store.Decide(entry.Name, entry.Version,
                entry.Sha256, "revoke", "Wrong state", "admin-token"));
            Assert.True(store.Decide(entry.Name, entry.Version,
                entry.Sha256, "publish", "Reviewed inert fixture", "admin-token"));
            Assert.Equal("PUBLISHED", store.Find(entry.Name, entry.Version)?.State);
            Assert.Single(store.List("synthetic"));
            Assert.Empty(store.List("no-match"));
            Assert.False(store.Stage(entry));
            Assert.True(store.Decide(entry.Name, entry.Version,
                entry.Sha256, "revoke", "Revoked metadata fixture", "admin-token"));
            Assert.Null(store.Find(entry.Name, entry.Version));
            Assert.Empty(store.List(""));
            Assert.Equal("REVOKED", store.Find(entry.Name, entry.Version, admin: true)?.State);
            Assert.False(store.Decide(entry.Name, entry.Version,
                entry.Sha256, "publish", "Must not reactivate", "admin-token"));
            store.Initialize();
            Assert.Equal("REVOKED", store.Find(entry.Name, entry.Version, admin: true)?.State);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ModifiedPublishedJsonCannotBeDiscoveredAsTheApprovedRevision()
    {
        var data = Path.Combine(Path.GetTempPath(), "packory-registry-corruption-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(data);
            var store = new McpRegistryStore(new Settings { DataDirectory = data });
            store.Initialize();
            var metadata = McpRegistryManifest.Parse(Payload(ValidJson));
            Assert.True(store.Stage(metadata));
            Assert.True(store.Decide(metadata.Name, metadata.Version, metadata.Sha256,
                "publish", "Inert synthetic review", "synthetic-admin"));
            Assert.NotNull(store.Find(metadata.Name, metadata.Version));
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(data, "packory.sqlite") }.ToString()))
            {
                db.Open();
                using var update = db.CreateCommand();
                update.CommandText = """
                    UPDATE mcp_registry_manifests
                    SET document_json = '{"name":"tampered-mcp-metadata"}'
                    WHERE name = @name AND version = @version
                    """;
                update.Parameters.AddWithValue("@name", metadata.Name);
                update.Parameters.AddWithValue("@version", metadata.Version);
                Assert.Equal(1, update.ExecuteNonQuery());
            }
            Assert.Null(store.Find(metadata.Name, metadata.Version));
            Assert.Empty(store.List("synthetic"));
        }
        finally
        {
            if (Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    [Fact]
    public async Task OfficialMcpClientDiscoversAndInvokesPublishedServerMetadataOnly()
    {
        using var host = new Host();
        using var httpClient = host.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(httpClient.BaseAddress!, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + ReaderToken
            }
        }, httpClient);
        await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await mcp.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Contains(tools, tool => tool.Name == "search_mcp_servers");
        Assert.Contains(tools, tool => tool.Name == "get_mcp_server");
        Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("publish_mcp_", StringComparison.Ordinal));

        var before = await mcp.CallToolAsync("search_mcp_servers",
            new Dictionary<string, object?> { ["query"] = "synthetic" },
            cancellationToken: timeout.Token);
        Assert.DoesNotContain("synthetic-server", JsonSerializer.Serialize(before));
        var store = host.Services.GetRequiredService<McpRegistryStore>();
        var metadata = McpRegistryManifest.Parse(Payload(ValidJson));
        Assert.True(store.Stage(metadata));
        Assert.True(store.Decide(metadata.Name, metadata.Version, metadata.Sha256,
            "publish", "Verified inert metadata for transport test", "test-admin"));

        var after = await mcp.CallToolAsync("search_mcp_servers",
            new Dictionary<string, object?> { ["query"] = "synthetic" },
            cancellationToken: timeout.Token);
        Assert.Contains("synthetic-server", JsonSerializer.Serialize(after));
        Assert.Contains("publisherVerified", JsonSerializer.Serialize(after));
        var detail = await mcp.CallToolAsync("get_mcp_server",
            new Dictionary<string, object?>
            {
                ["name"] = metadata.Name, ["version"] = metadata.Version
            }, cancellationToken: timeout.Token);
        Assert.Contains(metadata.Sha256, JsonSerializer.Serialize(detail));
        Assert.True(store.Decide(metadata.Name, metadata.Version, metadata.Sha256,
            "revoke", "Synthetic transport revoke", "test-admin"));
        var revoked = await mcp.CallToolAsync("get_mcp_server",
            new Dictionary<string, object?>
            {
                ["name"] = metadata.Name, ["version"] = metadata.Version
            }, cancellationToken: timeout.Token);
        Assert.Contains("unavailable", JsonSerializer.Serialize(revoked));
    }

    [Fact]
    public async Task RestAndMcpNeverExposePendingOrRevokedMetadata()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string catalog = "/api/v1/mcp/servers";
        const string detail = "/api/v1/mcp/server?name=org.example%2Fsynthetic-server&version=1.2.3";
        const string stage = "/api/v1/admin/mcp/servers/stage";
        const string decide = "/api/v1/admin/mcp/servers/decide";

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(catalog)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PostAsync(stage, new StringContent(ValidJson))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Bearer(HttpMethod.Post, stage, ReaderToken, ValidJson))).StatusCode);
        using var accepted = await client.SendAsync(Bearer(HttpMethod.Post, stage, AdminToken, ValidJson));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var acceptedBody = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        var digest = acceptedBody.GetProperty("sha256").GetString()!;
        Assert.False(acceptedBody.GetProperty("publisherVerified").GetBoolean());

        using var listBefore = await client.SendAsync(Bearer(HttpMethod.Get, catalog, ReaderToken));
        Assert.Equal(HttpStatusCode.OK, listBefore.StatusCode);
        Assert.Equal("no-store", listBefore.Headers.CacheControl?.ToString());
        using var empty = JsonDocument.Parse(await listBefore.Content.ReadAsStringAsync());
        Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Bearer(HttpMethod.Get, detail, ReaderToken))).StatusCode);
        var store = host.Services.GetRequiredService<McpRegistryStore>();
        Assert.Contains("unavailable", JsonSerializer.Serialize(McpRegistryMcpTools.GetMcpServer(
            "org.example/synthetic-server", "1.2.3", store)));
        var decisionJson = JsonSerializer.Serialize(new
        {
            name = "org.example/synthetic-server", version = "1.2.3", sha256 = digest,
            action = "publish", reason = "Synthetic manifest reviewed for test"
        });
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Bearer(HttpMethod.Post, decide, ReaderToken, decisionJson))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(Bearer(HttpMethod.Post, decide, AdminToken, decisionJson))).StatusCode);
        using var detailPublished = await client.SendAsync(Bearer(HttpMethod.Get, detail, ReaderToken));
        Assert.Equal(HttpStatusCode.OK, detailPublished.StatusCode);
        using var published = JsonDocument.Parse(await detailPublished.Content.ReadAsStringAsync());
        Assert.Equal("org.example/synthetic-server",
            published.RootElement.GetProperty("server").GetProperty("name").GetString());
        Assert.False(published.RootElement.GetProperty("publisherVerified").GetBoolean());
        Assert.False(published.RootElement.GetProperty("safeToConnect").GetBoolean());
        Assert.Contains("synthetic-server",
            JsonSerializer.Serialize(McpRegistryMcpTools.SearchMcpServers("synthetic", store)));
        Assert.Contains("synthetic-server",
            JsonSerializer.Serialize(McpRegistryMcpTools.GetMcpServer(
                "org.example/synthetic-server", "1.2.3", store)));

        var revoke = decisionJson.Replace("\"publish\"", "\"revoke\"", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(Bearer(HttpMethod.Post, decide, AdminToken, revoke))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Bearer(HttpMethod.Get, detail, ReaderToken))).StatusCode);
        Assert.DoesNotContain("synthetic-server",
            JsonSerializer.Serialize(McpRegistryMcpTools.SearchMcpServers("synthetic", store)));
        Assert.Contains("unavailable", JsonSerializer.Serialize(McpRegistryMcpTools.GetMcpServer(
            "org.example/synthetic-server", "1.2.3", store)));
    }
}
