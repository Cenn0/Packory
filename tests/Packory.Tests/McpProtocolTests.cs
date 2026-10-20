using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class McpProtocolTests
{
    private const string AdminToken = "synthetic-protocol-admin-credential-only";
    private const string ReaderToken = "synthetic-protocol-reader-credential-only";
    private const string CurrentProtocol = "2026-07-28";

    private sealed class Host : WebApplicationFactory<Program>
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(),
            "packory-mcp-protocol-" + Guid.NewGuid().ToString("N"));

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
                services.DisableBackgroundSkillScanning();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_data)) Directory.Delete(_data, true);
        }

        public async Task<(string Digest, Dictionary<string, byte[]> Files)> Publish(
            string name, string version = "1.0.0", Dictionary<string, byte[]>? extraFiles = null,
            string extraFrontmatter = "")
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["SKILL.md"] = Encoding.UTF8.GetBytes("---\nname: " + name +
                    "\ndescription: Inert protocol fixture\n" + extraFrontmatter + "---\n# Fixture\n")
            };
            foreach (var (path, bytes) in extraFiles ?? []) files.Add(path, bytes);
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (path, bytes) in files)
                {
                    using var entry = zip.CreateEntry(name + "/" + path).Open();
                    entry.Write(bytes);
                }
            var archive = buffer.ToArray();
            Assert.Equal(name, SkillEndpoints.InspectZip(archive).Name);
            var digest = Convert.ToHexStringLower(SHA256.HashData(archive));
            var db = Services.GetRequiredService<IPackageDatabase>();
            var blobs = Services.GetRequiredService<BlobStore>();
            var security = Services.GetRequiredService<SkillSecurityStore>();
            Assert.True(await blobs.PutAsync(SkillEndpoints.SkillKey(name, version),
                new MemoryStream(archive, writable: false), archive.LongLength + 1,
                CancellationToken.None));
            Assert.True(security.TryStage(name, version, "Inert protocol fixture", digest,
                "skill-staging/" + digest + ".zip"));
            Assert.True(security.SaveResult(name, version, digest, "NEEDS_REVIEW", 0,
                "SAFE", "{}"));
            Assert.True(security.TryTransition(name, version, digest, "NEEDS_REVIEW",
                "APPROVED", "synthetic-reviewer", "Inert protocol fixture"));
            Assert.True(db.AddSkill(name, version, "Inert protocol fixture", "synthetic-fixture"));
            return (digest, files);
        }

        public void Revoke(string name, string digest, string version = "1.0.0") =>
            Assert.True(Services.GetRequiredService<SkillSecurityStore>().TryTransition(
                name, version, digest, "APPROVED", "REVOKED", "synthetic-reviewer",
                "Protocol revocation fixture"));
    }

    private static Task<McpClient> Connect(HttpClient http, CancellationToken ct,
        string version = CurrentProtocol) => McpClient.CreateAsync(
        new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + ReaderToken
            }
        }, http), new McpClientOptions
        {
            ProtocolVersion = version,
            ClientInfo = new Implementation { Name = "Packory.Protocol.Tests", Version = "1.0" }
        }, cancellationToken: ct);

    private static async Task<JsonElement> Request(McpClient mcp, string method,
        JsonObject parameters, CancellationToken ct) =>
        await mcp.SendRequestAsync<JsonObject, JsonElement>(method, parameters, cancellationToken: ct);

    private static void AssertFreshPrivate(JsonElement result)
    {
        Assert.Equal("complete", result.GetProperty("resultType").GetString());
        Assert.Equal("private", result.GetProperty("cacheScope").GetString());
        Assert.Equal(0, result.GetProperty("ttlMs").GetInt64());
        var meta = result.GetProperty("_meta");
        Assert.True(meta.TryGetProperty("io.modelcontextprotocol/serverInfo", out var server));
        Assert.False(string.IsNullOrEmpty(server.GetProperty("name").GetString()));
    }

    private static string[] SkillUris(JsonElement page) => page.GetProperty("skills")
        .EnumerateArray().Select(x => x.GetProperty("uri").GetString()!).ToArray();

    [Theory]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    [InlineData(CurrentProtocol)]
    public async Task OfficialClientNegotiatesLegacyAndCurrentReadPrimitives(string protocol)
    {
        using var host = new Host();
        using var http = host.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var mcp = await Connect(http, timeout.Token, protocol);
        Assert.NotNull(mcp.ServerCapabilities.Tools);
        Assert.NotNull(mcp.ServerCapabilities.Resources);
        Assert.NotNull(mcp.ServerCapabilities.Prompts);
        if (protocol == CurrentProtocol)
        {
            var capabilities = JsonSerializer.SerializeToElement(mcp.ServerCapabilities);
            Assert.True(capabilities.GetProperty("extensions")
                .GetProperty("io.modelcontextprotocol/skills").GetProperty("directoryRead").GetBoolean());
        }
        var tools = await mcp.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Contains(tools, tool => tool.Name == "search_artifacts");
        var resources = await mcp.ListResourcesAsync(cancellationToken: timeout.Token);
        Assert.Contains(resources, resource => resource.Uri == "packory://catalog/info");
        var templates = await mcp.ListResourceTemplatesAsync(cancellationToken: timeout.Token);
        Assert.Contains(templates, template => template.UriTemplate == "skill://packory/{version}/{name}/{+path}");
        var info = await mcp.ReadResourceAsync("packory://catalog/info", cancellationToken: timeout.Token);
        Assert.Contains("Packory", Assert.IsType<TextResourceContents>(Assert.Single(info.Contents)).Text);
        var prompts = await mcp.ListPromptsAsync(cancellationToken: timeout.Token);
        Assert.Contains(prompts, prompt => prompt.Name == "review_skill");
        var prompt = await mcp.GetPromptAsync("review_skill", new Dictionary<string, object?>
        {
            ["name"] = "protocol-fixture", ["version"] = "1.0.0"
        }, cancellationToken: timeout.Token);
        Assert.NotEmpty(prompt.Messages);
    }

    [Fact]
    public async Task SkillsPaginationSurvivesRevocationBeforeAndAtTheMarker()
    {
        using var host = new Host();
        using var http = host.CreateClient();
        var digests = new Dictionary<string, string>();
        for (var i = 0; i < 30; i++)
        {
            var name = "fixture-" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            digests.Add(name, (await host.Publish(name)).Digest);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var mcp = await Connect(http, timeout.Token);
        var first = await Request(mcp, "skills/list", new JsonObject(), timeout.Token);
        AssertFreshPrivate(first);
        Assert.Equal(25, SkillUris(first).Length);
        var cursor = first.GetProperty("nextCursor").GetString()!;
        Assert.False(int.TryParse(cursor, out _));
        host.Revoke("fixture-000", digests["fixture-000"]);
        host.Revoke("fixture-024", digests["fixture-024"]);
        await host.Publish("aaa-added-before-marker");
        await host.Publish("fixture-999");

        var second = await Request(mcp, "skills/list", new JsonObject { ["cursor"] = cursor }, timeout.Token);
        AssertFreshPrivate(second);
        var uris = SkillUris(second);
        Assert.Equal(6, uris.Length);
        Assert.Contains(PackoryMcpCatalog.UriFor("1.0.0", "fixture-025"), uris);
        Assert.Contains(PackoryMcpCatalog.UriFor("1.0.0", "fixture-029"), uris);
        Assert.Contains(PackoryMcpCatalog.UriFor("1.0.0", "fixture-999"), uris);
        Assert.DoesNotContain(uris, uri => uri.Contains("aaa-added", StringComparison.Ordinal));
        Assert.Empty(SkillUris(first).Intersect(uris, StringComparer.Ordinal));
        Assert.False(second.TryGetProperty("nextCursor", out _));

        // Replaying a marker is deterministic for the same current catalog.
        var replay = await Request(mcp, "skills/list", new JsonObject { ["cursor"] = cursor }, timeout.Token);
        Assert.Equal(uris, SkillUris(replay));
    }

    [Fact]
    public async Task CompleteManifestResourcesRoundTripExactBytesAndCanonicalUris()
    {
        using var host = new Host();
        using var http = host.CreateClient();
        var extra = new Dictionary<string, byte[]>
        {
            ["references/space guide.md"] = Encoding.UTF8.GetBytes("# Exact text\r\n"),
            ["assets/ümlaut # 100%.txt"] = Encoding.UTF8.GetBytes("A Unicode fixture.\n"),
            ["assets/binary.txt"] = [0xff, 0xfe, 0x00, 0x81],
            ["assets/empty.txt"] = [],
            ["scripts/percent%2F.txt"] = Encoding.UTF8.GetBytes("The percent sequence is a literal filename.")
        };
        var published = await host.Publish("resource-fixture", extraFiles: extra,
            extraFrontmatter: "metadata:\n  unset: null\n  enabled: true\n  count: 3\n" +
                "  ratio: 1.25\n  tiny: 1e-100\n  large: 1e100\n  explicit: !!str true\n  quoted: 'false'\n" +
                "  positiveTiny: +1e-100\n  positiveLarge: +1e100\n" +
                "  leadingDot: .000000000000000000000000000001\n  trailingDot: 1.\n" +
                "  typedNull: !!null null\n  typedBoolean: !!bool 'false'\n" +
                "  typedInteger: !!int '+0012'\n  typedFloat: !!float '+.50e-100'\n" +
                "  hexadecimal: 0x10000000000000000\n  octal: 0o20\n");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var mcp = await Connect(http, timeout.Token);
        var response = await Request(mcp, "skills/get", new JsonObject
        {
            ["uri"] = PackoryMcpCatalog.UriFor("1.0.0", "resource-fixture")
        }, timeout.Token);
        AssertFreshPrivate(response);
        var skill = response.GetProperty("skill");
        var metadata = skill.GetProperty("frontmatter").GetProperty("metadata");
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("unset").ValueKind);
        Assert.True(metadata.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, metadata.GetProperty("count").GetInt32());
        Assert.Equal(1.25m, metadata.GetProperty("ratio").GetDecimal());
        Assert.Equal("1e-100", metadata.GetProperty("tiny").GetRawText());
        Assert.Equal("1e100", metadata.GetProperty("large").GetRawText());
        Assert.Equal("true", metadata.GetProperty("explicit").GetString());
        Assert.Equal("false", metadata.GetProperty("quoted").GetString());
        Assert.Equal("1e-100", metadata.GetProperty("positiveTiny").GetRawText());
        Assert.Equal("1e100", metadata.GetProperty("positiveLarge").GetRawText());
        Assert.Equal("0.000000000000000000000000000001", metadata.GetProperty("leadingDot").GetRawText());
        Assert.Equal("1.0", metadata.GetProperty("trailingDot").GetRawText());
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("typedNull").ValueKind);
        Assert.False(metadata.GetProperty("typedBoolean").GetBoolean());
        Assert.Equal(12, metadata.GetProperty("typedInteger").GetInt32());
        Assert.Equal("0.50e-100", metadata.GetProperty("typedFloat").GetRawText());
        Assert.Equal("18446744073709551616", metadata.GetProperty("hexadecimal").GetRawText());
        Assert.Equal(16, metadata.GetProperty("octal").GetInt32());
        var files = skill.GetProperty("resources").EnumerateArray().ToArray();
        Assert.Equal(published.Files.Count, files.Length);
        Assert.Equal(files.Length, files.Select(x => x.GetProperty("uri").GetString()).Distinct().Count());
        foreach (var file in files)
        {
            var uri = file.GetProperty("uri").GetString()!;
            Assert.True(PackoryMcpCatalog.TryParseResourceUri(uri, out var version, out var name, out var path));
            Assert.Equal("1.0.0", version);
            Assert.Equal("resource-fixture", name);
            var resource = await mcp.ReadResourceAsync(uri, cancellationToken: timeout.Token);
            Assert.Equal(CacheScope.Private, resource.CacheScope);
            Assert.Equal(TimeSpan.Zero, resource.TimeToLive);
            var content = Assert.Single(resource.Contents);
            Assert.Equal(uri, content.Uri);
            var bytes = content switch
            {
                TextResourceContents text => Encoding.UTF8.GetBytes(text.Text),
                BlobResourceContents blob => blob.DecodedData.ToArray(),
                _ => throw new InvalidOperationException("Unexpected resource representation")
            };
            Assert.Equal(published.Files[path], bytes);
            Assert.Equal(bytes.LongLength, file.GetProperty("size").GetInt64());
            Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)),
                file.GetProperty("digest").GetString());
        }
        var directory = await Request(mcp, "resources/directory/read", new JsonObject
        {
            ["uri"] = "skill://packory/1.0.0/resource-fixture/assets"
        }, timeout.Token);
        AssertFreshPrivate(directory);
        Assert.Contains(directory.GetProperty("resources").EnumerateArray(), child =>
            child.GetProperty("uri").GetString() ==
            PackoryMcpCatalog.UriFor("1.0.0", "resource-fixture", "assets/ümlaut # 100%.txt"));

        host.Revoke("resource-fixture", published.Digest);
        var denied = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await mcp.ReadResourceAsync(skill.GetProperty("uri").GetString()!, cancellationToken: timeout.Token));
        Assert.Equal(McpErrorCode.InvalidParams, denied.ErrorCode);
    }

    [Theory]
    [InlineData("!!int true")]
    [InlineData("!!bool 123")]
    [InlineData("!!null false")]
    [InlineData("!!int 1.2")]
    [InlineData("!!float not-a-number")]
    [InlineData("!!float 0x10")]
    public async Task ExplicitYamlTagsCannotSilentlyChangeTheDeclaredScalarType(string scalar)
    {
        using var host = new Host();
        using var http = host.CreateClient();
        await host.Publish("invalid-tag-fixture", extraFrontmatter: "metadata:\n  invalid: " + scalar + "\n");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var mcp = await Connect(http, timeout.Token);
        var uri = PackoryMcpCatalog.UriFor("1.0.0", "invalid-tag-fixture");
        var rejected = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await Request(mcp, "skills/get", new JsonObject { ["uri"] = uri }, timeout.Token));
        Assert.Equal(McpErrorCode.InvalidParams, rejected.ErrorCode);
        var resourceRejected = await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await mcp.ReadResourceAsync(uri, cancellationToken: timeout.Token));
        Assert.Equal(McpErrorCode.InvalidParams, resourceRejected.ErrorCode);
    }

    [Fact]
    public async Task DirectoryPagingIsBoundToTheDirectoryAndRevokedArchive()
    {
        using var host = new Host();
        using var http = host.CreateClient();
        var extra = Enumerable.Range(0, 205).ToDictionary(
            i => "references/item-" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + ".txt",
            _ => Encoding.UTF8.GetBytes("Inert fixture."));
        var published = await host.Publish("directory-pages", extraFiles: extra);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var mcp = await Connect(http, timeout.Token);
        const string uri = "skill://packory/1.0.0/directory-pages/references";
        var collected = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        string? firstCursor = null;
        var pages = 0;
        do
        {
            var parameters = new JsonObject { ["uri"] = uri };
            if (cursor is not null) parameters["cursor"] = cursor;
            var page = await Request(mcp, "resources/directory/read", parameters, timeout.Token);
            AssertFreshPrivate(page);
            Assert.InRange(page.GetProperty("resources").GetArrayLength(), 1, 100);
            foreach (var child in page.GetProperty("resources").EnumerateArray())
                Assert.True(collected.Add(child.GetProperty("uri").GetString()!));
            cursor = page.TryGetProperty("nextCursor", out var continuation) ? continuation.GetString() : null;
            firstCursor ??= cursor;
            Assert.InRange(++pages, 1, 3);
        } while (cursor is not null);
        Assert.Equal(205, collected.Count);
        Assert.Equal(3, pages);
        var wrongDirectory = await Assert.ThrowsAsync<McpProtocolException>(() =>
            Request(mcp, "resources/directory/read", new JsonObject
            {
                ["uri"] = "skill://packory/1.0.0/directory-pages", ["cursor"] = firstCursor
            }, timeout.Token));
        Assert.Equal(McpErrorCode.InvalidParams, wrongDirectory.ErrorCode);
        var wrongMethod = await Assert.ThrowsAsync<McpProtocolException>(() =>
            Request(mcp, "skills/list", new JsonObject { ["cursor"] = firstCursor }, timeout.Token));
        Assert.Equal(McpErrorCode.InvalidParams, wrongMethod.ErrorCode);
        host.Revoke("directory-pages", published.Digest);
        var revoked = await Assert.ThrowsAsync<McpProtocolException>(() =>
            Request(mcp, "resources/directory/read", new JsonObject
            {
                ["uri"] = uri, ["cursor"] = firstCursor
            }, timeout.Token));
        Assert.Equal(McpErrorCode.InvalidParams, revoked.ErrorCode);
    }

    [Theory]
    [InlineData("skills/list", "{\"cursor\":-1}")]
    [InlineData("skills/list", "{\"cursor\":null}")]
    [InlineData("skills/list", "{\"cursor\":{}}")]
    [InlineData("skills/list", "{\"cursor\":\"-1\"}")]
    [InlineData("skills/list", "{\"cursor\":\"s1.invalid\"}")]
    [InlineData("skills/get", "{}")]
    [InlineData("skills/get", "{\"uri\":42}")]
    [InlineData("skills/get", "{\"uri\":\"skill://packory/1.0.0/missing/SKILL.md\"}")]
    [InlineData("resources/directory/read", "{\"uri\":[]}")]
    [InlineData("resources/directory/read", "{\"uri\":\"skill://packory/1.0.0/missing\"}")]
    public async Task ExtensionParameterErrorsAreInvalidParams(string method, string json)
    {
        using var host = new Host();
        using var http = host.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var mcp = await Connect(http, timeout.Token);
        var error = await Assert.ThrowsAsync<McpProtocolException>(() => Request(mcp,
            method, JsonNode.Parse(json)!.AsObject(), timeout.Token));
        Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
    }

    [Theory]
    [InlineData("references/../SKILL.md")]
    [InlineData("references/%2e%2e/SKILL.md")]
    [InlineData("references%2FSKILL.md")]
    [InlineData("%53KILL.md")]
    [InlineData("SKILL.md?query=value")]
    [InlineData("SKILL.md#fragment")]
    [InlineData("SKILL.md/")]
    [InlineData("references/raw space.md")]
    public async Task ResourceUrisRejectNormalizationAndAmbiguousEscapes(string suffix)
    {
        using var host = new Host();
        using var http = host.CreateClient();
        await host.Publish("strict-uri");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var mcp = await Connect(http, timeout.Token);
        var uri = "skill://packory/1.0.0/strict-uri/" + suffix;
        Assert.False(PackoryMcpCatalog.TryParseResourceUri(uri, out _, out _, out _));
        var error = await Assert.ThrowsAsync<McpProtocolException>(() =>
            Request(mcp, "resources/read", new JsonObject { ["uri"] = uri }, timeout.Token));
        Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
    }

    [Fact]
    public async Task CurrentProtocolRequiresPerRequestMetadata()
    {
        using var host = new Host();
        using var http = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1," +
                "\"method\":\"skills/list\",\"params\":{}}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ReaderToken);
        request.Headers.TryAddWithoutValidation("Mcp-Protocol-Version", CurrentProtocol);
        request.Headers.TryAddWithoutValidation("Mcp-Method", "skills/list");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await http.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var error = JsonDocument.Parse(content);
        Assert.Equal(-32602, error.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.DoesNotContain("\"skills\":", content, StringComparison.Ordinal);
    }
}
