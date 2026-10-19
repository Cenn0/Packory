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

public sealed class MavenArtifactTests
{
    private const string Admin = "test-maven-admin-token-long-enough-to-be-safe";
    private const string Writer = "test-maven-publisher-token-long-enough-to-be-safe";
    private const string Reader = "test-maven-reader-token-long-enough-to-be-safe";

    private sealed class Host(Action<Settings>? configure = null) : WebApplicationFactory<Program>
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(), "packory-maven-tests-" + Guid.NewGuid().ToString("N"));
        public string DataRoot => _data;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Packory:DataDirectory"] = _data,
                    ["Packory:PublicBaseUrl"] = "http://localhost:8080",
                    ["Packory:AdminToken"] = Admin,
                    ["Packory:ReadToken"] = Reader,
                    ["Packory:WriteToken"] = Writer,
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
                    ReadToken = Reader,
                    WriteToken = Writer,
                    McpEnabled = false,
                    AnonymousRead = false,
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
            if (disposing && Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string uri, string token, byte[]? content = null)
    {
        var req = new HttpRequestMessage(method, uri);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (content is not null)
        {
            req.Content = new ByteArrayContent(content);
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        return req;
    }

    private static byte[] Pom(string group = "com.example", string artifact = "widget",
        string version = "1.0.0") => Encoding.UTF8.GetBytes(
        $"""<project xmlns="http://maven.apache.org/POM/4.0.0"><modelVersion>4.0.0</modelVersion><groupId>{group}</groupId><artifactId>{artifact}</artifactId><version>{version}</version><packaging>jar</packaging></project>""");

    [Fact]
    public async Task HostedReleases_PomJarClassifierChecksumsAndMetadata()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string root = "/maven/com/example/widget/1.0.0/widget-1.0.0";
        var jar = Encoding.UTF8.GetBytes("synthetic jar bytes are never executed");
        var pom = Pom();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync(root + ".pom")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Request(HttpMethod.Put, root + ".pom", Reader, pom))).StatusCode);
        using var uploadedPom = await client.SendAsync(Request(HttpMethod.Put, root + ".pom", Writer, pom));
        Assert.Equal(HttpStatusCode.Created, uploadedPom.StatusCode);
        using var uploadedJar = await client.SendAsync(Request(HttpMethod.Put, root + ".jar", Writer, jar));
        Assert.Equal(HttpStatusCode.Created, uploadedJar.StatusCode);
        using var classifier = await client.SendAsync(Request(HttpMethod.Put, root + "-sources.jar", Writer, jar));
        Assert.Equal(HttpStatusCode.Created, classifier.StatusCode);
        using var duplicate = await client.SendAsync(Request(HttpMethod.Put, root + ".jar", Writer, jar));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var actual = await client.SendAsync(Request(HttpMethod.Get, root + ".jar", Reader));
        Assert.Equal(HttpStatusCode.OK, actual.StatusCode);
        Assert.Equal(jar, await actual.Content.ReadAsByteArrayAsync());
        Assert.Equal("\"" + Convert.ToHexStringLower(SHA256.HashData(jar)) + "\"",
            actual.Headers.ETag?.ToString());
        using var pomResponse = await client.SendAsync(Request(HttpMethod.Get, root + ".pom", Reader));
        Assert.Equal(pom, await pomResponse.Content.ReadAsByteArrayAsync());

        using var sha256 = await client.SendAsync(Request(HttpMethod.Get, root + ".jar.sha256", Reader));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(jar)),
            (await sha256.Content.ReadAsStringAsync()).Trim());
        using var sha1 = await client.SendAsync(Request(HttpMethod.Get, root + ".jar.sha1", Reader));
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(jar)),
            (await sha1.Content.ReadAsStringAsync()).Trim());

        using var metadata = await client.SendAsync(Request(HttpMethod.Get,
            "/maven/com/example/widget/maven-metadata.xml", Reader));
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        var xml = await metadata.Content.ReadAsStringAsync();
        Assert.Contains("<groupId>com.example</groupId>", xml);
        Assert.Contains("<artifactId>widget</artifactId>", xml);
        Assert.Contains("<version>1.0.0</version>", xml);

        var file = Path.Combine(host.DataRoot, "blobs", "maven", "com", "example",
            "widget", "1.0.0", "widget-1.0.0.jar");
        await File.WriteAllTextAsync(file, "corrupted content");
        using var corrupt = await client.SendAsync(Request(HttpMethod.Get, root + ".jar", Reader));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, corrupt.StatusCode);
        using var corruptSidecar = await client.SendAsync(Request(HttpMethod.Get, root + ".jar.sha256", Reader));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, corruptSidecar.StatusCode);
    }

    [Fact]
    public async Task MavenRejectsSnapshotSpoofingAndUnsafePomsBeforePublication()
    {
        using var host = new Host(cfg => cfg.WriteNamespaces = ["maven:com.example/*"]);
        using var client = host.CreateClient();
        var allowed = "/maven/com/example/widget/1.0.0/widget-1.0.0.pom";
        using var blocked = await client.SendAsync(Request(HttpMethod.Put,
            "/maven/org/other/widget/1.0.0/widget-1.0.0.pom", Writer, Pom("org.other")));
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);

        using var wrong = await client.SendAsync(Request(HttpMethod.Put, allowed, Writer, Pom("wrong.group")));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(
            Request(HttpMethod.Get, allowed, Reader))).StatusCode);

        var dtd = Encoding.UTF8.GetBytes("""<!DOCTYPE project [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><project><groupId>com.example</groupId><artifactId>widget</artifactId><version>1.0.0</version><description>&xxe;</description></project>""");
        using var hostile = await client.SendAsync(Request(HttpMethod.Put, allowed, Writer, dtd));
        Assert.Equal(HttpStatusCode.BadRequest, hostile.StatusCode);

        using var snapshot = await client.SendAsync(Request(HttpMethod.Put,
            "/maven/com/example/widget/1.0.0-SNAPSHOT/widget-1.0.0-SNAPSHOT.pom",
            Admin, Pom(version: "1.0.0-SNAPSHOT")));
        Assert.Equal(HttpStatusCode.BadRequest, snapshot.StatusCode);
        using var clientMetadata = await client.SendAsync(Request(HttpMethod.Put,
            "/maven/com/example/widget/maven-metadata.xml", Admin, Pom()));
        Assert.Equal(HttpStatusCode.BadRequest, clientMetadata.StatusCode);

        using var good = await client.SendAsync(Request(HttpMethod.Put, allowed, Writer, Pom()));
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
    }

    [Fact]
    public async Task ScopedManagedTokenCannotReadGlobalMavenCoordinates()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        const string path = "/maven/com/example/widget/1.0.0/widget-1.0.0.pom";
        using var publish = await client.SendAsync(Request(HttpMethod.Put, path, Admin, Pom()));
        Assert.Equal(HttpStatusCode.Created, publish.StatusCode);
        var token = new string('e', 64);
        var hashed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        db.CreateManagedToken(new ManagedTokenRecord(Guid.NewGuid().ToString("N"), hashed,
            "read", "feed:private-feed:read", DateTimeOffset.UtcNow.AddDays(1).ToString("O")));
        using var restricted = await client.SendAsync(Request(HttpMethod.Get, path, token));
        Assert.Equal(HttpStatusCode.Forbidden, restricted.StatusCode);
        using var listRestricted = await client.SendAsync(Request(HttpMethod.Get,
            "/maven/com/example/widget/maven-metadata.xml", token));
        Assert.Equal(HttpStatusCode.Forbidden, listRestricted.StatusCode);
        using var admin = await client.SendAsync(Request(HttpMethod.Get, path, Reader));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public void MavenMetadataAndSqliteSchemaUpgradeAreDurable()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-maven-db-" + Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Database(new Settings { DataDirectory = root });
            db.Initialize();
            const string path = "com/example/widget/1.0.0/widget-1.0.0.pom";
            var entry = new MavenAssetEntry(path, "com.example", "widget", "1.0.0",
                new string('a', 64), 3, DateTimeOffset.UtcNow.ToString("O"));
            Assert.True(db.PublishMaven(entry, "synthetic"));
            Assert.False(db.PublishMaven(entry, "synthetic"));
            Assert.Single(db.MavenAssets());
            Assert.Equal("1.0.0", Assert.Single(db.MavenVersions("com.example", "widget")));
            Assert.Equal("hosted", db.PackageOrigin("maven", "com.example/widget", "1.0.0"));
            db.Initialize();
            Assert.Equal(6, SchemaVersionGuard.Current);
            Assert.NotNull(db.MavenAsset(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("com/example/widget/1.0.0/widget-1.0.0.jar", true)]
    [InlineData("com/example/widget/1.0.0/widget-1.0.0-sources.jar", true)]
    [InlineData("com/example/widget/1.0.0/widget-1.0.0.pom", true)]
    [InlineData("com/EXAMPLE/widget/1.0.0/widget-1.0.0.jar", false)]
    [InlineData("com/example/widget/1.0.0-SNAPSHOT/widget-1.0.0-SNAPSHOT.jar", false)]
    [InlineData("com/example/widget/1.0.0/widget-1.0.0.zip", false)]
    [InlineData("com/example/widget/1.0.0/../widget-1.0.0.jar", false)]
    [InlineData("com/example/widget/1.0.0/widget-1.0.0.module", false)]
    public void MavenNativeAssetPathValidationIsFailClosed(string path, bool accepted) =>
        Assert.Equal(accepted, MavenEndpoints.TryParseAsset(path, out _));
}
