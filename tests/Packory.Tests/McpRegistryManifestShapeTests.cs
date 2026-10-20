using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class McpRegistryManifestShapeTests
{
    private const string Full = """
        {
          "$schema":"https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json",
          "name":"org.example/inert-multi-distribution",
          "version":"1.2.3",
          "title":"Inert server metadata",
          "description":"Public synthetic test metadata",
          "websiteUrl":"https://example.org/docs",
          "repository":{
            "source":"github",
            "url":"https://github.com/example/synthetic-server",
            "id":"12345",
            "subfolder":"servers/weather"
          },
          "icons":[
            {"src":"https://cdn.example.org/logo.png","mimeType":"image/png","theme":"light","sizes":["48x48","any"]},
            {"src":"https://cdn.example.org/logo.svg","mimeType":"image/svg+xml","theme":"dark"}
          ],
          "remotes":[
            {
              "type":"streamable-http",
              "url":"https://mcp.example.org/{tenant}/mcp",
              "variables":{"tenant":{"description":"Tenant selector","format":"string","isRequired":true}},
              "headers":[
                {"name":"Authorization","value":"Bearer {access_key}",
                 "variables":{"access_key":{"isSecret":true,"isRequired":true,"format":"string"}}}
              ]
            },
            {
              "type":"sse",
              "url":"https://mcp.example.org/sse?tenant={tenant}",
              "variables":{"tenant":{"description":"Tenant selector","isRequired":true}}
            }
          ],
          "packages":[
            {
              "registryType":"npm",
              "identifier":"@example/inert-server",
              "registryBaseUrl":"https://registry.npmjs.org",
              "version":"1.2.3",
              "runtimeHint":"npx",
              "runtimeArguments":[{"type":"named","name":"--yes","value":"true"}],
              "packageArguments":[{"type":"positional","valueHint":"path","isRequired":true}],
              "environmentVariables":[{"name":"ACCESS_TOKEN","value":"{access_key}",
                "variables":{"access_key":{"isSecret":true}}}],
              "transport":{"type":"stdio"}
            },
            {
              "registryType":"pypi",
              "identifier":"example-server",
              "version":"1.2.3",
              "transport":{"type":"streamable-http","url":"https://runtime.example.org/mcp"}
            },
            {
              "registryType":"mcpb",
              "identifier":"https://downloads.example.org/inert.mcpb",
              "version":"1.2.3",
              "fileSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "transport":{"type":"stdio"}
            }
          ],
          "_meta":{
            "io.modelcontextprotocol.registry/publisher-provided":{
              "buildInfo":{"commit":"abc123","timestamp":"2026-01-01T00:00:00Z"},
              "notes":["inert","untrusted"]
            },
            "org.example/build":{"flags":{"reproducible":false}}
          }
        }
        """;

    private static JsonObject FullObject() => JsonNode.Parse(Full)!.AsObject();
    private static byte[] Bytes(JsonNode obj) => Encoding.UTF8.GetBytes(obj.ToJsonString());

    [Fact]
    public void PinnedOptionalShapesAreInertAndPreserveExactOriginalDocument()
    {
        var original = Encoding.UTF8.GetBytes(Full);
        var manifest = McpRegistryManifest.Parse(original);
        Assert.Equal("org.example/inert-multi-distribution", manifest.Name);
        Assert.Equal("https://mcp.example.org/{tenant}/mcp", manifest.RemoteUrl);
        Assert.Equal(Full, manifest.Json);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(original)), manifest.Sha256);
        var doc = JsonDocument.Parse(manifest.Json);
        Assert.Equal(3, doc.RootElement.GetProperty("packages").GetArrayLength());
        Assert.Equal(2, doc.RootElement.GetProperty("remotes").GetArrayLength());
        Assert.Equal(2, doc.RootElement.GetProperty("icons").GetArrayLength());
        Assert.Equal("servers/weather",
            doc.RootElement.GetProperty("repository").GetProperty("subfolder").GetString());
        Assert.Equal("Bearer {access_key}",
            doc.RootElement.GetProperty("remotes")[0].GetProperty("headers")[0]
                .GetProperty("value").GetString());
        // The original string remains inert: no variable interpolation, HTTP call,
        // package download, executable invocation or credential resolution occurs.
    }

    [Fact]
    public void PackageOnlyDescriptorsWorkWithDurablePublishDiscoverAndRevoke()
    {
        var full = FullObject();
        full.Remove("remotes");
        var bytes = Bytes(full);
        var manifest = McpRegistryManifest.Parse(bytes);
        Assert.Equal("", manifest.RemoteUrl);
        var root = Path.Combine(Path.GetTempPath(), "packory-mcp-r2-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new McpRegistryStore(new Settings { DataDirectory = root });
            store.Initialize();
            Assert.True(store.Stage(manifest));
            Assert.Null(store.Find(manifest.Name, manifest.Version));
            Assert.True(store.Decide(manifest.Name, manifest.Version, manifest.Sha256, "publish",
                "Reviewed inert package-only fixture", "synthetic-reviewer"));
            var found = Assert.IsType<McpRegistryEntry>(store.Find(manifest.Name, manifest.Version));
            Assert.Equal(manifest.Json, found.Json);
            var summary = JsonSerializer.SerializeToElement(found.Summary);
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("remoteType").ValueKind);
            Assert.Equal(JsonValueKind.Null, summary.GetProperty("remoteUrl").ValueKind);
            var response = JsonSerializer.SerializeToElement(McpRegistryDiscovery.Response(found));
            Assert.Equal(3, response.GetProperty("server").GetProperty("packages").GetArrayLength());
            Assert.False(response.GetProperty("_meta")
                .GetProperty(McpRegistryDiscovery.MetadataNamespace)
                .GetProperty("publisherVerified").GetBoolean());
            Assert.False(response.GetProperty("_meta")
                .GetProperty(McpRegistryDiscovery.MetadataNamespace)
                .GetProperty("safeToConnect").GetBoolean());
            store.Initialize();
            Assert.Single(store.ListPublishedPage(search: "inert-multi-distribution").Items);
            Assert.True(store.Decide(manifest.Name, manifest.Version, manifest.Sha256, "revoke",
                "Synthetic revoke after package-only discovery", "synthetic-reviewer"));
            Assert.Null(store.Find(manifest.Name, manifest.Version));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AlternativeRepositoryAndPackageTransportShapesAreAcceptedAsMetadata()
    {
        var root = FullObject();
        root["repository"]!["source"] = "gitlab";
        root["repository"]!["url"] = "https://gitlab.com/example/synthetic-server";
        root["packages"]!.AsArray()[1]!["transport"]!["type"] = "sse";
        root["packages"]!.AsArray()[1]!["transport"]!["url"] = "https://runtime.example.org/sse";
        root["packages"]!.AsArray()[0]!["registryType"] = "cargo";
        var parsed = McpRegistryManifest.Parse(Bytes(root));
        Assert.Equal("1.2.3", parsed.Version);
    }

    [Fact]
    public void RejectsUnknownShapesCredentialLiteralsTraversalAndUncheckedLinks()
    {
        var mutations = new (string Name, Action<JsonObject> Apply)[]
        {
            ("unknown root", root => root["headers"] = new JsonObject()),
            ("untrusted schema", root => root["$schema"] = "https://example.org/not-the-pinned-schema"),
            ("invalid remote protocol", root => root["remotes"]!.AsArray()[0]!["type"] = "stdio"),
            ("invalid remote scheme", root => root["remotes"]!.AsArray()[0]!["url"] = "http://mcp.example.org/mcp"),
            ("private remote", root => root["remotes"]!.AsArray()[0]!["url"] = "https://127.0.0.1/mcp"),
            ("undeclared variable", root => root["remotes"]!.AsArray()[0]!["url"] = "https://mcp.example.org/{missing}/mcp"),
            ("hardcoded query token", root => root["remotes"]!.AsArray()[0]!["url"] = "https://mcp.example.org/mcp?token=unsafe"),
            ("inline auth header", root => root["remotes"]!.AsArray()[0]!["headers"]!.AsArray()[0]!["value"] = "Bearer synthetic-credential"),
            ("no remote or package", root => {root.Remove("remotes");root.Remove("packages");}),
            ("missing package transport", root => root["packages"]!.AsArray()[0]!.AsObject().Remove("transport")),
            ("unsafe package base", root => root["packages"]!.AsArray()[0]!["registryBaseUrl"] = "http://localhost:80"),
            ("unsafe direct download", root => root["packages"]!.AsArray()[2]!["identifier"] = "https://127.0.0.1/executable.mcpb"),
            ("package range", root => root["packages"]!.AsArray()[0]!["version"] = "^1.2.3"),
            ("package latest", root => root["packages"]!.AsArray()[0]!["version"] = "latest"),
            ("invalid mcpb checksum", root => root["packages"]!.AsArray()[2]!["fileSha256"] = "not-a-sha"),
            ("missing mcpb checksum", root => root["packages"]!.AsArray()[2]!.AsObject().Remove("fileSha256")),
            ("invalid package command", root => root["packages"]!.AsArray()[0]!["runtimeArguments"]!.AsArray()[0]!["type"] = "shell"),
            ("invalid repo subfolder", root => root["repository"]!["subfolder"] = "../private"),
            ("wrong github origin", root => root["repository"]!["url"] = "https://not-github.example.org/claimed/repo"),
            ("unsafe icon source", root => root["icons"]!.AsArray()[0]!["src"] = "data:image/svg+xml,unsafe"),
            ("unsupported icon MIME", root => root["icons"]!.AsArray()[0]!["mimeType"] = "text/html"),
            ("invalid icon size", root => root["icons"]!.AsArray()[0]!["sizes"] = new JsonArray("0x0")),
            ("metadata secret key", root => root["_meta"]!["org.example/build"]!["secret"] = "not-a-real-secret"),
            ("non-namespaced extension", root => root["_meta"]!["extension"] = new JsonObject()),
            ("unsafe fixed secret", root => root["packages"]!.AsArray()[0]!["environmentVariables"]!.AsArray()[0]!["value"] = "inline-credential"),
            ("wrong input type", root => root["packages"]!.AsArray()[0]!["environmentVariables"]!.AsArray()[0]!["isRequired"] = "true")
        };
        foreach (var (name, apply) in mutations)
        {
            var root = FullObject();
            apply(root);
            var invalid = Bytes(root);
            var error = Record.Exception(() => McpRegistryManifest.Parse(invalid));
            Assert.True(error is InvalidDataException, "Missing fail-closed rejection for " + name +
                ": " + error?.ToString());
        }
    }

    [Fact]
    public void RejectsExcessDepthDuplicatePropertiesOversizedListsAndMalformedUtf8()
    {
        var original = FullObject();
        var malformed = original.ToJsonString().Replace(
            "\"version\":\"1.2.3\"", "\"version\":\"1.2.3\",\"version\":\"1.2.3\"",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            McpRegistryManifest.Parse(Encoding.UTF8.GetBytes(malformed)));
        var invalidUtf8 = Encoding.UTF8.GetBytes(Full);
        invalidUtf8[0] = 0xff;
        Assert.Throws<InvalidDataException>(() => McpRegistryManifest.Parse(invalidUtf8));
        var tooManyIcons = FullObject();
        var icons = tooManyIcons["icons"]!.AsArray();
        for (var i = 0; i < 17; i++)
            icons.Add(new JsonObject { ["src"] = "https://cdn.example.org/" + i + ".png" });
        Assert.Throws<InvalidDataException>(() => McpRegistryManifest.Parse(Bytes(tooManyIcons)));
        var metadata = FullObject();
        var current = metadata["_meta"]!["org.example/build"]!.AsObject();
        for (var i = 0; i < 11; i++)
        {
            var nested = new JsonObject();
            current["nested"] = nested;
            current = nested;
        }
        Assert.Throws<InvalidDataException>(() => McpRegistryManifest.Parse(Bytes(metadata)));
        var tooLong = FullObject();
        tooLong["description"] = new string('x', 101);
        Assert.Throws<InvalidDataException>(() => McpRegistryManifest.Parse(Bytes(tooLong)));
    }
}
