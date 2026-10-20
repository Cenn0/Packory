using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class FeedMavenTests
{
    private const string Admin = "synthetic-maven-feed-admin-credential";
    private const string Reader = "synthetic-maven-global-reader-credential";
    private sealed class Host : WebApplicationFactory<Program>
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(),
            "packory-feed-maven-test-" + Guid.NewGuid().ToString("N"));
        public string Root => _directory;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Packory:DataDirectory"] = _directory,
                    ["Packory:PublicBaseUrl"] = "http://localhost:8080",
                    ["Packory:AdminToken"] = Admin,
                    ["Packory:ReadToken"] = Reader,
                    ["Packory:AnonymousRead"] = "true"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                var settings = new Settings
                {
                    DataDirectory = _directory, PublicBaseUrl = "http://localhost:8080",
                    AdminToken = Admin, ReadToken = Reader,
                    AnonymousRead = true, McpEnabled = false,
                    MaxArtifactBytes = 1024 * 1024
                };
                settings.Validate();
                services.AddSingleton(settings);
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string url,
        string? token = null, byte[]? content = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (content is not null)
        {
            request.Content = new ByteArrayContent(content);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }
        return request;
    }
    private static async Task<HttpStatusCode> Status(HttpClient client, HttpMethod method,
        string url, string? token = null, byte[]? content = null)
    {
        using var response = await client.SendAsync(Request(method, url, token, content));
        return response.StatusCode;
    }
    private static string Credential(IPackageDatabase db, string role, string grant, char digit)
    {
        var token = new string(digit, 64);
        db.CreateManagedToken(new ManagedTokenRecord(Guid.NewGuid().ToString("N"),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            role, grant, DateTimeOffset.UtcNow.AddHours(1).ToString("O")));
        return token;
    }
    private static byte[] Pom(string group, string name, string version) =>
        Encoding.UTF8.GetBytes($"""
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion><groupId>{group}</groupId>
              <artifactId>{name}</artifactId><version>{version}</version>
              <packaging>jar</packaging>
            </project>
            """);

    [Fact]
    public async Task TwoFeedsAllowIdenticalMavenCoordinatesWithIsolatedNativeReads()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        foreach (var feed in new[] { "alpha-maven", "beta-maven" })
        {
            using var request = Request(HttpMethod.Post, "/api/v1/admin/feeds/drafts", Admin);
            request.Content = JsonContent.Create(new
            {
                id = feed, kind = "maven", mode = "hosted", displayName = "Synthetic Maven"
            });
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
                "/api/v1/admin/feeds/" + feed + "/activate", Admin));
        }
        var alphaRead = Credential(db, "read", "feed:alpha-maven:read", 'a');
        var alphaWrite = Credential(db, "write", "feed:alpha-maven:publish", 'b');
        var betaRead = Credential(db, "read", "feed:beta-maven:read", 'c');
        var betaWrite = Credential(db, "write", "feed:beta-maven:publish", 'd');
        const string tail = "com/example/demo/1.0/demo-1.0";
        var alpha = "/feeds/alpha-maven/maven/" + tail;
        var beta = "/feeds/beta-maven/maven/" + tail;
        var pom = Pom("com.example", "demo", "1.0");
        var first = "unique-alpha-maven-jar"u8.ToArray();
        var second = "unique-beta-maven-jar"u8.ToArray();

        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, HttpMethod.Get, alpha + ".jar"));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Get, alpha + ".jar", Reader));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Get, alpha + ".jar", betaRead));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Put, beta + ".jar", alphaWrite, first));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Put, alpha + ".jar", alphaRead, first));

        Assert.Equal(HttpStatusCode.Created,
            await Status(client, HttpMethod.Put, alpha + ".pom", alphaWrite, pom));
        Assert.Equal(HttpStatusCode.Created,
            await Status(client, HttpMethod.Put, beta + ".pom", betaWrite, pom));
        Assert.Equal(HttpStatusCode.Created,
            await Status(client, HttpMethod.Put, alpha + ".jar", alphaWrite, first));
        Assert.Equal(HttpStatusCode.Created,
            await Status(client, HttpMethod.Put, beta + ".jar", betaWrite, second));
        Assert.Equal(HttpStatusCode.Conflict,
            await Status(client, HttpMethod.Put, alpha + ".jar", alphaWrite, second));

        using (var a = await client.SendAsync(Request(HttpMethod.Get, alpha + ".jar", alphaRead)))
        {
            Assert.Equal(HttpStatusCode.OK, a.StatusCode);
            Assert.Equal(first, await a.Content.ReadAsByteArrayAsync());
        }
        using (var b = await client.SendAsync(Request(HttpMethod.Get, beta + ".jar", betaRead)))
        {
            Assert.Equal(HttpStatusCode.OK, b.StatusCode);
            Assert.Equal(second, await b.Content.ReadAsByteArrayAsync());
        }
        var firstSha = Convert.ToHexStringLower(SHA256.HashData(first));
        var firstSha1 = Convert.ToHexStringLower(SHA1.HashData(first));
        Assert.Equal(HttpStatusCode.NoContent, await Status(client, HttpMethod.Put,
            alpha + ".jar.sha1", alphaWrite, Encoding.ASCII.GetBytes(firstSha1 + "\n")));
        Assert.Equal(HttpStatusCode.Conflict, await Status(client, HttpMethod.Put,
            alpha + ".jar.sha1", alphaWrite, Encoding.ASCII.GetBytes(new string('0', 40))));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Put,
            alpha + ".jar.sha1", alphaRead, Encoding.ASCII.GetBytes(firstSha1)));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, HttpMethod.Put,
            "/feeds/alpha-maven/maven/com/example/demo/maven-metadata.xml", alphaWrite,
            "<metadata />"u8.ToArray()));
        Assert.Equal(firstSha, await (await client.SendAsync(Request(HttpMethod.Get,
            alpha + ".jar.sha256", alphaRead))).Content.ReadAsStringAsync());
        Assert.Equal(firstSha1, await (await client.SendAsync(Request(HttpMethod.Get,
            alpha + ".jar.sha1", alphaRead))).Content.ReadAsStringAsync());

        var metadata = "/feeds/alpha-maven/maven/com/example/demo/maven-metadata.xml";
        using (var response = await client.SendAsync(Request(HttpMethod.Get, metadata, alphaRead)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var xml = XDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Contains(xml.Descendants(), x => x.Name.LocalName == "version" && x.Value == "1.0");
        }
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Get, metadata, betaRead));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, HttpMethod.Get,
            "/maven/" + tail + ".jar", Reader));
        Assert.DoesNotContain("com.example:demo", await (await client.GetAsync(
            "/api/packages?kind=maven")).Content.ReadAsStringAsync());
        Assert.Equal(4, db.FeedAssets().Count(x => x.Kind == "maven"));
        Assert.Empty(db.PendingPublications());
        Assert.Equal(0, host.Services.GetRequiredService<ArtifactRecovery>().MissingHostedCount());
        var scan = await host.Services.GetRequiredService<ArtifactIntegrityScanner>()
            .ScanAsync(0, 10, CancellationToken.None);
        Assert.Equal(4, scan.Items.Count(x => x.Kind == "feed-maven" &&
            x.State == "digest_match"));

        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
            "/api/v1/admin/feeds/alpha-maven/deactivate", Admin));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, HttpMethod.Get, metadata, alphaRead));
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Get, beta + ".jar", betaRead));
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
            "/api/v1/admin/feeds/alpha-maven/activate", Admin));
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Get, alpha + ".jar", alphaRead));
    }

    [Fact]
    public async Task PrivateMavenFailsClosedOnWrongPomAndTamperedStoredBytes()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var at = DateTimeOffset.UtcNow.ToString("O");
        Assert.True(db.TryCreateDraftFeed(new FeedDefinition("isolated-maven", "maven",
            "hosted", "private", "Synthetic Maven", false, at, at), "synthetic"));
        Assert.True(db.TrySetHostedFeedEnabled("isolated-maven", "maven", true, "synthetic"));
        var writer = Credential(db, "write", "feed:isolated-maven:publish", 'e');
        var reader = Credential(db, "read", "feed:isolated-maven:read", 'f');
        var asset = "/feeds/isolated-maven/maven/com/example/demo/1.0/demo-1.0";
        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, HttpMethod.Put,
            asset + ".pom", writer, Pom("wrong.example", "demo", "1.0")));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, HttpMethod.Put,
            "/feeds/isolated-maven/maven/com/example/demo/1.0-SNAPSHOT/demo-1.0-SNAPSHOT.pom",
            writer, Pom("com.example", "demo", "1.0-SNAPSHOT")));
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put,
            asset + ".jar", writer, "some bytes"u8.ToArray()));
        var location = Path.Combine(host.Root, "blobs", "feeds", "isolated-maven",
            "maven", "com", "example", "demo", "1.0", "demo-1.0.jar");
        await File.WriteAllTextAsync(location, "tampered");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(client, HttpMethod.Get,
            asset + ".jar", reader));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(client, HttpMethod.Put,
            asset + ".jar.sha256", writer, Encoding.ASCII.GetBytes(new string('a', 64))));
        var page = await host.Services.GetRequiredService<ArtifactIntegrityScanner>()
            .ScanAsync(0, 10, CancellationToken.None);
        Assert.Contains(page.Items, x => x.Kind == "feed-maven" &&
            x.State == "digest_mismatch");
    }
}
