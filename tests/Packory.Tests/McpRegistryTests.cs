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

    private sealed class Host(bool anonymousRead = false) : WebApplicationFactory<Program>
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(), "packory-mcp-registry-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Packory:DataDirectory"] = _data,
                    ["Packory:PublicBaseUrl"] = "http://localhost:8080",
                    ["Packory:AnonymousRead"] = anonymousRead.ToString(),
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
                    AnonymousRead = anonymousRead,
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
        Assert.Throws<InvalidDataException>(() =>
            McpRegistryManifest.Parse(Payload(ValidJson.Replace(
                "Inert example", new string('x', 101), StringComparison.Ordinal))));
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
        const string adminDetail = "/api/v1/admin/mcp/server?name=org.example%2Fsynthetic-server&version=1.2.3";
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
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Bearer(HttpMethod.Get, adminDetail, ReaderToken))).StatusCode);
        using (var pending = JsonDocument.Parse(await (await client.SendAsync(Bearer(
            HttpMethod.Get, adminDetail, AdminToken))).Content.ReadAsStringAsync()))
        {
            Assert.Equal("PENDING", pending.RootElement.GetProperty("entry").GetProperty("reviewState").GetString());
            Assert.Empty(pending.RootElement.GetProperty("history").EnumerateArray());
        }
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
        using (var adminPublished = JsonDocument.Parse(await (await client.SendAsync(Bearer(
            HttpMethod.Get, adminDetail, AdminToken))).Content.ReadAsStringAsync()))
            Assert.Equal("PUBLISHED", adminPublished.RootElement.GetProperty("history")[0].GetProperty("newState").GetString());
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
        using (var adminRevoked = JsonDocument.Parse(await (await client.SendAsync(Bearer(
            HttpMethod.Get, adminDetail, AdminToken))).Content.ReadAsStringAsync()))
            Assert.Equal("REVOKED", adminRevoked.RootElement.GetProperty("history")[0].GetProperty("newState").GetString());
        Assert.DoesNotContain("synthetic-server",
            JsonSerializer.Serialize(McpRegistryMcpTools.SearchMcpServers("synthetic", store)));
        Assert.Contains("unavailable", JsonSerializer.Serialize(McpRegistryMcpTools.GetMcpServer(
            "org.example/synthetic-server", "1.2.3", store)));
    }

    private static McpRegistryManifest Publish(McpRegistryStore store, string name,
        string version = "1.0.0")
    {
        var manifest = McpRegistryManifest.Parse(Payload(ValidJson
            .Replace("org.example/synthetic-server", name, StringComparison.Ordinal)
            .Replace("1.2.3", version, StringComparison.Ordinal)));
        Assert.True(store.Stage(manifest));
        Assert.True(store.Decide(name, version, manifest.Sha256,
            "publish", "Synthetic reviewed metadata", "test-admin"));
        return manifest;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GlobalRegistryRespectsManagedScopesAndConfiguredAnonymousRead(bool anonymousRead)
    {
        using var host = new Host(anonymousRead);
        using var client = host.CreateClient();
        Publish(host.Services.GetRequiredService<McpRegistryStore>(), "org.example/global-fixture");
        const string feed = "registry-isolated-feed";
        using (var request = Bearer(HttpMethod.Post, "/api/v1/admin/feeds/drafts", AdminToken,
            JsonSerializer.Serialize(new { id = feed, kind = "nuget", mode = "hosted", displayName = "Synthetic isolated feed" })))
        using (var response = await client.SendAsync(request)) Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using (var request = Bearer(HttpMethod.Post, "/api/v1/admin/feeds/" + feed + "/activate", AdminToken))
        using (var response = await client.SendAsync(request)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        async Task<string> CreateToken(string role, string? grant)
        {
            using var request = Bearer(HttpMethod.Post, "/api/admin/tokens", AdminToken,
                JsonSerializer.Serialize(new { role, @namespace = grant, expiresInHours = 1 }));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        }
        var globalReader = await CreateToken("read", null);
        var feedReader = await CreateToken("read", "feed:" + feed + ":read");
        var feedPublisher = await CreateToken("write", "feed:" + feed + ":publish");
        var namespacePublisher = await CreateToken("write", "skill:other-*");
        string[] routes =
        [
            "/v0.1/servers", "/v0.1/servers/org.example%2Fglobal-fixture/versions",
            "/v0.1/servers/org.example%2Fglobal-fixture/versions/1.0.0",
            "/v0.1/servers/org.example%2Fglobal-fixture/versions/latest",
            "/api/v1/mcp/servers", "/api/v1/mcp/server?name=org.example%2Fglobal-fixture&version=1.0.0"
        ];
        foreach (var route in routes)
        {
            using (var response = await client.GetAsync(route))
                Assert.Equal(anonymousRead ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
            foreach (var token in new[] { feedReader, feedPublisher, namespacePublisher })
            {
                using var request = Bearer(HttpMethod.Get, route, token);
                using var response = await client.SendAsync(request);
                Assert.Equal(anonymousRead ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
            }
            using var globalRequest = Bearer(HttpMethod.Get, route, globalReader);
            using var globalResponse = await client.SendAsync(globalRequest);
            Assert.Equal(HttpStatusCode.OK, globalResponse.StatusCode);
            Assert.Contains("org.example/global-fixture", await globalResponse.Content.ReadAsStringAsync());
        }
        // Denying global discovery leaves native private-feed grants usable,
        // including the Basic credentials used by NuGet service-index readers.
        foreach (var token in new[] { feedReader, feedPublisher })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/feeds/" + feed + "/nuget/v3/index.json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("synthetic-client:" + token)));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using var unrelatedRequest = Bearer(HttpMethod.Get,
            "/feeds/" + feed + "/nuget/v3/index.json", namespacePublisher);
        using var unrelatedResponse = await client.SendAsync(unrelatedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, unrelatedResponse.StatusCode);
    }

    private static async Task<JsonElement> GetRegistry(HttpClient client, string uri)
    {
        using var response = await client.SendAsync(Bearer(HttpMethod.Get, uri, ReaderToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task V01CoreRoutesPreserveReviewedDocumentAndProtectEveryRead()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var store = host.Services.GetRequiredService<McpRegistryStore>();
        const string name = "org.example/encoded-server";
        const string version = "1.2.3+build.4";
        var staged = McpRegistryManifest.Parse(Payload(ValidJson
            .Replace("org.example/synthetic-server", name, StringComparison.Ordinal)
            .Replace("1.2.3", version, StringComparison.Ordinal)));
        Assert.True(store.Stage(staged));
        var history = "/v0.1/servers/" + Uri.EscapeDataString(name) + "/versions";
        var detail = history + "/" + Uri.EscapeDataString(version);
        foreach (var path in new[] { "/v0.1/servers", history, detail, history + "/latest" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        Assert.Empty((await GetRegistry(client, "/v0.1/servers"))
            .GetProperty("servers").EnumerateArray());
        foreach (var path in new[] { history, detail, history + "/latest" })
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.SendAsync(Bearer(HttpMethod.Get, path, ReaderToken))).StatusCode);
        Assert.True(store.Decide(name, version, staged.Sha256, "publish",
            "Synthetic route review", "test-admin"));
        var found = await GetRegistry(client, detail);
        using var original = JsonDocument.Parse(staged.Json);
        Assert.True(JsonElement.DeepEquals(original.RootElement, found.GetProperty("server")));
        Assert.False(found.GetProperty("_meta").TryGetProperty(
            "io.modelcontextprotocol.registry/official", out _));
        var metadata = found.GetProperty("_meta").GetProperty(McpRegistryDiscovery.MetadataNamespace);
        Assert.False(metadata.GetProperty("publisherVerified").GetBoolean());
        Assert.False(metadata.GetProperty("safeToConnect").GetBoolean());
        Assert.Equal(staged.Sha256, metadata.GetProperty("sha256").GetString());
        Assert.Equal("active", metadata.GetProperty("status").GetString());
        var versions = await GetRegistry(client, history);
        Assert.Equal(1, versions.GetProperty("metadata").GetProperty("count").GetInt32());
        Assert.Single(versions.GetProperty("servers").EnumerateArray());
        Assert.True((await GetRegistry(client, history + "/latest")).GetProperty("_meta")
            .GetProperty(McpRegistryDiscovery.MetadataNamespace).GetProperty("isLatest").GetBoolean());
        Assert.True(store.Decide(name, version, staged.Sha256, "revoke",
            "Synthetic route revocation", "test-admin"));
        Assert.Empty((await GetRegistry(client, "/v0.1/servers"))
            .GetProperty("servers").EnumerateArray());
        foreach (var path in new[] { history, detail, history + "/latest" })
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.SendAsync(Bearer(HttpMethod.Get, path, ReaderToken))).StatusCode);
    }

    [Theory]
    [InlineData("limit=0")]
    [InlineData("limit=101")]
    [InlineData("limit=2147483648")]
    [InlineData("limit=-1")]
    [InlineData("limit=abc")]
    [InlineData("limit=10&limit=20")]
    [InlineData("search=first&search=second")]
    [InlineData("search=%0A")]
    [InlineData("version=1.*")]
    [InlineData("cursor=")]
    [InlineData("cursor=not-a-cursor")]
    [InlineData("include_deleted=true")]
    [InlineData("include_deleted=perhaps")]
    [InlineData("updated_since=2026-01-01T00%3A00%3A00Z")]
    [InlineData("unrecognized=1")]
    public async Task V01RejectsMalformedAndUnsupportedFilters(string query)
    {
        using var host = new Host();
        using var client = host.CreateClient();
        using var response = await client.SendAsync(Bearer(HttpMethod.Get,
            "/v0.1/servers?" + query, ReaderToken));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task V01CursorPagesBeyondOneHundredAndSurvivesEarlierRevocation()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var store = host.Services.GetRequiredService<McpRegistryStore>();
        McpRegistryManifest? first = null;
        for (var i = 0; i < 125; i++)
        {
            var entry = Publish(store, $"org.example/page-{i:D3}");
            first ??= entry;
        }
        var initial = await GetRegistry(client, "/v0.1/servers?search=PAGE-&limit=40");
        var seen = initial.GetProperty("servers").EnumerateArray()
            .Select(x => x.GetProperty("server").GetProperty("name").GetString()!).ToList();
        Assert.Equal(40, seen.Count);
        Assert.True(store.Decide(first!.Name, first.Version, first.Sha256, "revoke",
            "Synthetic earlier-row removal", "test-admin"));
        // An insertion before the cursor must not repeat or skip later rows.
        Publish(store, "org.example/page--before");
        Publish(store, "org.example/page-before");
        var cursor = initial.GetProperty("metadata").GetProperty("nextCursor").GetString()!;
        for (var pages = 0; ; pages++)
        {
            Assert.True(pages < 10);
            var page = await GetRegistry(client, "/v0.1/servers?search=page-&limit=25&cursor=" + cursor);
            var items = page.GetProperty("servers").EnumerateArray().ToArray();
            Assert.Equal(items.Length, page.GetProperty("metadata").GetProperty("count").GetInt32());
            seen.AddRange(items.Select(x => x.GetProperty("server").GetProperty("name").GetString()!));
            if (!page.GetProperty("metadata").TryGetProperty("nextCursor", out var next)) break;
            cursor = next.GetString()!;
        }
        // The new "page-before" key is after numeric names and is therefore
        // visible in this live keyset traversal, without shifting those names.
        Assert.Equal(126, seen.Count);
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("org.example/page--before", seen);
        Assert.Contains("org.example/page-040", seen);
        Assert.Contains("org.example/page-124", seen);
    }

    [Fact]
    public async Task V01CursorBindsRouteAndFiltersButAllowsPageSizeChanges()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var store = host.Services.GetRequiredService<McpRegistryStore>();
        Publish(store, "org.example/cursor-a");
        Publish(store, "org.example/cursor-b");
        Publish(store, "org.example/cursor-c");
        var first = await GetRegistry(client, "/v0.1/servers?search=cursor-&limit=1");
        var cursor = first.GetProperty("metadata").GetProperty("nextCursor").GetString()!;
        var second = await GetRegistry(client,
            "/v0.1/servers?search=CURSOR-&limit=2&cursor=" + cursor);
        Assert.Equal(2, second.GetProperty("servers").GetArrayLength());
        foreach (var path in new[]
        {
            "/v0.1/servers?search=different&cursor=" + cursor,
            "/v0.1/servers?search=cursor-&version=latest&cursor=" + cursor,
            "/v0.1/servers/org.example%2Fcursor-a/versions?cursor=" + cursor,
            "/v0.1/servers?search=cursor-&cursor=" + cursor + "="
        })
            Assert.Equal(HttpStatusCode.BadRequest,
                (await client.SendAsync(Bearer(HttpMethod.Get, path, ReaderToken))).StatusCode);
        Assert.Empty((await GetRegistry(client, "/v0.1/servers?search=%25"))
            .GetProperty("servers").EnumerateArray());
        Assert.Empty((await GetRegistry(client, "/v0.1/servers?search=%5F"))
            .GetProperty("servers").EnumerateArray());
    }

    [Fact]
    public async Task V01LatestUsesSemanticPrecedenceAndRechecksRevocation()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var store = host.Services.GetRequiredService<McpRegistryStore>();
        const string name = "org.example/versioned";
        Publish(store, name, "2.0.0");
        var highest = Publish(store, name, "10.0.0");
        Publish(store, name, "9.0.0");
        Publish(store, name, "10.0.0-rc.10");
        const string route = "/v0.1/servers/org.example%2Fversioned/versions";
        var latest = await GetRegistry(client, route + "/latest");
        Assert.Equal("10.0.0", latest.GetProperty("server").GetProperty("version").GetString());
        var selected = await GetRegistry(client, "/v0.1/servers?version=latest&limit=1");
        Assert.Equal("10.0.0", selected.GetProperty("servers")[0]
            .GetProperty("server").GetProperty("version").GetString());
        var exact = await GetRegistry(client, "/v0.1/servers?version=9.0.0");
        Assert.Single(exact.GetProperty("servers").EnumerateArray());
        Assert.True(store.Decide(name, highest.Version, highest.Sha256, "revoke",
            "Synthetic latest revocation", "test-admin"));
        latest = await GetRegistry(client, route + "/latest");
        Assert.Equal("10.0.0-rc.10", latest.GetProperty("server").GetProperty("version").GetString());
        var versions = await GetRegistry(client, route + "?limit=1");
        Assert.Equal("10.0.0-rc.10", versions.GetProperty("servers")[0]
            .GetProperty("server").GetProperty("version").GetString());
        var next = versions.GetProperty("metadata").GetProperty("nextCursor").GetString();
        versions = await GetRegistry(client, route + "?limit=1&cursor=" + next);
        Assert.Equal("9.0.0", versions.GetProperty("servers")[0]
            .GetProperty("server").GetProperty("version").GetString());
    }

    [Theory]
    [InlineData("2.0.0", "10.0.0")]
    [InlineData("10.0.0-rc.2", "10.0.0-rc.10")]
    [InlineData("10.0.0-rc.10", "10.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("999999999999999999999.0.0", "1000000000000000000000.0.0")]
    public void RegistryVersionPrecedenceIsNotLexicalOrFixedWidth(string smaller, string larger)
    {
        Assert.True(McpRegistryManifest.ValidVersion(smaller));
        Assert.True(McpRegistryManifest.ValidVersion(larger));
        Assert.True(McpRegistryDiscovery.CompareVersions(smaller, larger) < 0);
        Assert.True(McpRegistryDiscovery.CompareVersions(larger, smaller) > 0);
        Assert.Equal(0, McpRegistryDiscovery.CompareVersions(smaller, smaller));
        Assert.False(McpRegistryManifest.ValidVersion("1.0.0-01"));
    }

    [Fact]
    public async Task CorruptRegistryRowsDoNotTerminateLaterPagesOrBecomeLatest()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var store = host.Services.GetRequiredService<McpRegistryStore>();
        Publish(store, "org.example/corrupt-a");
        Publish(store, "org.example/corrupt-b");
        Publish(store, "org.example/corrupt-c");
        var settings = host.Services.GetRequiredService<Settings>();
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(settings.DataDirectory, "packory.sqlite") }.ToString()))
        {
            db.Open();
            using var update = db.CreateCommand();
            update.CommandText = "UPDATE mcp_registry_manifests SET document_json='{}' WHERE name <> 'org.example/corrupt-c'";
            Assert.Equal(2, update.ExecuteNonQuery());
        }
        var empty = await GetRegistry(client, "/v0.1/servers?limit=1");
        Assert.Empty(empty.GetProperty("servers").EnumerateArray());
        var cursor = empty.GetProperty("metadata").GetProperty("nextCursor").GetString();
        var healthy = await GetRegistry(client, "/v0.1/servers?limit=1&cursor=" + cursor);
        Assert.Equal("org.example/corrupt-c", healthy.GetProperty("servers")[0]
            .GetProperty("server").GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Bearer(HttpMethod.Get,
            "/v0.1/servers/org.example%2Fcorrupt-a/versions/latest", ReaderToken))).StatusCode);
    }

    [Fact]
    public async Task OfficialMcpClientContinuesRegistrySearchWithoutLosingPublishedResults()
    {
        using var host = new Host();
        using var httpClient = host.CreateClient();
        var store = host.Services.GetRequiredService<McpRegistryStore>();
        Publish(store, "org.example/native-a");
        Publish(store, "org.example/native-b");
        Publish(store, "org.example/native-c");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(httpClient.BaseAddress!, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
                { ["Authorization"] = "Bearer " + ReaderToken }
        }, httpClient);
        await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var first = await mcp.CallToolAsync("search_mcp_servers", new Dictionary<string, object?>
            { ["query"] = "native-", ["limit"] = 1 }, cancellationToken: timeout.Token);
        Assert.False(first.IsError == true);
        var content = Assert.Single(first.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>());
        using var firstDocument = JsonDocument.Parse(content.Text);
        Assert.Equal("org.example/native-a", firstDocument.RootElement.GetProperty("items")[0]
            .GetProperty("name").GetString());
        var cursor = firstDocument.RootElement.GetProperty("nextCursor").GetString();
        var second = await mcp.CallToolAsync("search_mcp_servers", new Dictionary<string, object?>
            { ["query"] = "native-", ["limit"] = 2, ["cursor"] = cursor }, cancellationToken: timeout.Token);
        Assert.False(second.IsError == true);
        using var secondDocument = JsonDocument.Parse(Assert.Single(second.Content
            .OfType<ModelContextProtocol.Protocol.TextContentBlock>()).Text);
        Assert.Equal(2, secondDocument.RootElement.GetProperty("items").GetArrayLength());
        Assert.DoesNotContain("native-a", secondDocument.RootElement.GetRawText());
    }

    [Fact]
    public async Task PackorySummaryCursorPreservesTitleSearchAndOffsetCompatibility()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var store = host.Services.GetRequiredService<McpRegistryStore>();
        Publish(store, "org.example/summary-a");
        Publish(store, "org.example/summary-b");
        Publish(store, "org.example/summary-c");
        var first = await GetRegistry(client, "/api/v1/mcp/servers?q=Inert&limit=1");
        Assert.Equal(1, first.GetProperty("nextOffset").GetInt32());
        var cursor = first.GetProperty("nextCursor").GetString();
        var continuation = await GetRegistry(client,
            "/api/v1/mcp/servers?q=Inert&limit=2&cursor=" + cursor);
        Assert.Equal(2, continuation.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, continuation.GetProperty("nextCursor").ValueKind);
        var legacy = await GetRegistry(client, "/api/v1/mcp/servers?q=Inert&limit=2&offset=1");
        Assert.True(JsonElement.DeepEquals(legacy.GetProperty("items"), continuation.GetProperty("items")));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Bearer(HttpMethod.Get,
            "/api/v1/mcp/servers?q=Inert&offset=1&cursor=" + cursor, ReaderToken))).StatusCode);
        Assert.Empty((await GetRegistry(client, "/v0.1/servers?search=Inert"))
            .GetProperty("servers").EnumerateArray());
    }
}
