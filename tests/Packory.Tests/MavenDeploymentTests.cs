using System.Net;
using System.Net.Http.Headers;
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

public sealed class MavenDeploymentTests
{
    private const string Admin = "synthetic-maven-deploy-admin-token-for-tests";
    private const string Writer = "synthetic-maven-deploy-write-token-for-tests";
    private const string Reader = "synthetic-maven-deploy-read-token-for-tests";
    private const string Ga = "org/example/demo";
    private const string MetadataPath = Ga + "/maven-metadata.xml";

    private sealed class Host(int metadataLimit = MavenMetadata.MaxBytes) : WebApplicationFactory<Program>
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "packory-maven-deploy-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Packory:DataDirectory"] = Root,
                    ["Packory:AdminToken"] = Admin, ["Packory:AnonymousRead"] = "false" }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                var settings = new Settings
                {
                    DataDirectory = Root, PublicBaseUrl = "http://localhost:8080",
                    AdminToken = Admin, WriteToken = Writer, ReadToken = Reader,
                    AnonymousRead = false, McpEnabled = false, MaxMetadataBytes = metadataLimit,
                    MaxArtifactBytes = 1024 * 1024, WriteNamespaces = ["maven:org.example/*"]
                };
                settings.Validate();
                services.AddSingleton(settings);
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path,
        string? token, byte[]? bytes = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (bytes is not null) request.Content = new ByteArrayContent(bytes);
        return request;
    }

    private static async Task<HttpStatusCode> Status(HttpClient client, HttpMethod method,
        string path, string? token, byte[]? bytes = null)
    {
        using var request = Request(method, path, token, bytes);
        using var result = await client.SendAsync(request);
        return result.StatusCode;
    }

    private static byte[] Pom(string version) => Encoding.UTF8.GetBytes(
        $"<project><groupId>org.example</groupId><artifactId>demo</artifactId><version>{version}</version></project>");

    private static byte[] ReleaseMetadata(string latest, string versions,
        string at = "20261022100000") => Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="UTF-8"?>
        <metadata>
          <groupId>org.example</groupId><artifactId>demo</artifactId>
          <versioning><latest>{latest}</latest><release>{latest}</release>
            <versions>{versions}</versions><lastUpdated>{at}</lastUpdated>
          </versioning>
        </metadata>
        """);

    private static byte[] SnapshotMetadata(int build, string timestamp = "20261022.101500",
        string? jarValue = null)
    {
        var value = "2.0-" + timestamp + "-" + build;
        var updated = timestamp.Replace(".", "");
        return Encoding.UTF8.GetBytes($"""
        <metadata modelVersion="1.1.0">
          <groupId>org.example</groupId><artifactId>demo</artifactId><version>2.0-SNAPSHOT</version>
          <versioning><snapshot><timestamp>{timestamp}</timestamp><buildNumber>{build}</buildNumber></snapshot>
            <lastUpdated>{updated}</lastUpdated><snapshotVersions>
              <snapshotVersion><extension>pom</extension><value>{value}</value><updated>{updated}</updated></snapshotVersion>
              <snapshotVersion><extension>jar</extension><value>{jarValue ?? value}</value><updated>{updated}</updated></snapshotVersion>
            </snapshotVersions>
          </versioning>
        </metadata>
        """);
    }

    private static async Task PublishRelease(HttpClient client, string version, string prefix = "/maven/",
        string token = Writer)
    {
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put,
            prefix + Ga + "/" + version + "/demo-" + version + ".pom", token, Pom(version)));
    }

    private static async Task PublishSnapshot(HttpClient client, int build,
        string prefix = "/maven/", string token = Writer, string timestamp = "20261022.101500",
        string payload = "snapshot archive")
    {
        var root = prefix + Ga + "/2.0-SNAPSHOT/demo-2.0-" + timestamp + "-" + build;
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put,
            root + ".pom", token, Pom("2.0-SNAPSHOT")));
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put,
            root + ".jar", token, Encoding.UTF8.GetBytes(payload)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParentInheritedPomPublishesOnlyMatchingEffectiveCoordinates(bool privateFeed)
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var prefix = "/maven/";
        if (privateFeed)
        {
            var db = host.Services.GetRequiredService<IPackageDatabase>();
            var at = DateTimeOffset.UtcNow.ToString("O");
            Assert.True(db.TryCreateDraftFeed(new FeedDefinition("inherited-pom", "maven",
                "hosted", "private", "Synthetic parent POM test", false, at, at), "synthetic"));
            Assert.True(db.TrySetHostedFeedEnabled("inherited-pom", "maven", true, "synthetic"));
            prefix = "/feeds/inherited-pom/maven/";
        }
        const string pom = """
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <parent><groupId>org.example</groupId><artifactId>parent</artifactId><version>1.0</version></parent>
              <artifactId>child</artifactId>
            </project>
            """;
        var path = prefix + "org/example/child/1.0/child-1.0.pom";
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put,
            path, privateFeed ? Admin : Writer, Encoding.UTF8.GetBytes(pom)));
        using var read = await client.SendAsync(Request(HttpMethod.Get,
            path, privateFeed ? Admin : Reader));
        Assert.Equal(Encoding.UTF8.GetBytes(pom), await read.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, HttpMethod.Put,
            prefix + "org/example/child/1.1/child-1.1.pom",
            privateFeed ? Admin : Writer, Encoding.UTF8.GetBytes(pom)));
    }

    [Theory]
    [InlineData("<project><parent><groupId>org.example</groupId><artifactId>root</artifactId><version>1.0</version></parent><artifactId>child</artifactId></project>", true)]
    [InlineData("<project><parent><groupId>org.other</groupId><artifactId>root</artifactId><version>1.0</version></parent><artifactId>child</artifactId></project>", false)]
    [InlineData("<project><parent><groupId>org.example</groupId><artifactId>root</artifactId><version>2.0</version></parent><artifactId>child</artifactId></project>", false)]
    [InlineData("<project><parent><groupId>org.example</groupId><artifactId>root</artifactId><version>1.0</version></parent><artifactId>child</artifactId><groupId>org.other</groupId></project>", false)]
    [InlineData("<project><parent><groupId>org.example</groupId><artifactId>root</artifactId><version>1.0</version></parent><parent><groupId>org.example</groupId><artifactId>root</artifactId><version>1.0</version></parent><artifactId>child</artifactId></project>", false)]
    [InlineData("<project><parent><groupId>org.example</groupId><groupId>org.example</groupId><artifactId>root</artifactId><version>1.0</version></parent><artifactId>child</artifactId></project>", false)]
    [InlineData("<!DOCTYPE project [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><project>&x;</project>", false)]
    public void EffectiveParentPomCoordinatesAreValidated(string xml, bool expected)
    {
        var coordinates = new MavenCoordinates.Artifact("org.example", "child", "1.0", null, "pom");
        Assert.Equal(expected, MavenEndpoints.PomIdentityMatches(Encoding.UTF8.GetBytes(xml), coordinates));
    }

    [Fact]
    public async Task ReleaseMetadataKeepsExactBytesChecksumsAndConditionalUpdates()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var url = "/maven/" + MetadataPath;
        await PublishRelease(client, "1.0");
        var first = ReleaseMetadata("1.0", "<version>1.0</version>");
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, HttpMethod.Put, url, null, first));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Put, url, Reader, first));
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put, url, Writer, first));
        using var get = await client.SendAsync(Request(HttpMethod.Get, url, Reader));
        Assert.Equal(first, await get.Content.ReadAsByteArrayAsync());
        var firstTag = "\"" + Convert.ToHexStringLower(SHA256.HashData(first)) + "\"";
        Assert.Equal(firstTag, get.Headers.ETag?.ToString());
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Head, url, Reader));
        foreach (var (algorithm, hash) in new[]
        {
            ("sha512", SHA512.HashData(first)), ("sha256", SHA256.HashData(first)),
            ("sha1", SHA1.HashData(first)), ("md5", MD5.HashData(first))
        })
        {
            var digest = Convert.ToHexStringLower(hash);
            Assert.Equal(HttpStatusCode.NoContent, await Status(client, HttpMethod.Put,
                url + "." + algorithm, Writer, Encoding.ASCII.GetBytes(digest + "\n")));
            using var sidecar = await client.SendAsync(Request(HttpMethod.Get, url + "." + algorithm, Reader));
            Assert.Equal(digest, await sidecar.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.Conflict, await Status(client, HttpMethod.Put,
            url + ".sha256", Writer, Encoding.ASCII.GetBytes(new string('0', 64))));
        using var conditional = Request(HttpMethod.Get, url, Reader);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", firstTag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(conditional)).StatusCode);

        await PublishRelease(client, "1.1");
        var second = ReleaseMetadata("1.1", "<version>1.0</version><version>1.1</version>", "20261022110000");
        using var update = Request(HttpMethod.Put, url, Writer, second);
        update.Headers.TryAddWithoutValidation("If-Match", firstTag);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(update)).StatusCode);
        using var stale = Request(HttpMethod.Put, url, Writer, first);
        stale.Headers.TryAddWithoutValidation("If-Match", firstTag);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await client.SendAsync(stale)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, await Status(client, HttpMethod.Put, url, Writer, first));
        using var latest = await client.SendAsync(Request(HttpMethod.Get, url, Reader));
        Assert.Equal(second, await latest.Content.ReadAsByteArrayAsync());
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        Assert.Equal(Convert.ToBase64String(second), db.GetMavenMetadata("", MetadataPath)?.ContentBase64);
        Assert.Single(db.MavenMetadata());
        Assert.Empty(db.PendingPublications());
    }

    [Fact]
    public async Task SnapshotGenerationsMoveMetadataWithoutOverwritingImmutableFiles()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var metadata = "/maven/" + Ga + "/2.0-SNAPSHOT/maven-metadata.xml";
        await PublishSnapshot(client, 1, payload: "first generation");
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, HttpMethod.Get, metadata, Reader));
        var first = SnapshotMetadata(1);
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put, metadata, Writer, first));
        await PublishSnapshot(client, 2, payload: "second generation");
        var second = SnapshotMetadata(2);
        Assert.Equal(HttpStatusCode.NoContent, await Status(client, HttpMethod.Put, metadata, Writer, second));
        Assert.Equal(HttpStatusCode.Conflict, await Status(client, HttpMethod.Put, metadata, Writer, first));
        var firstJar = "/maven/" + Ga + "/2.0-SNAPSHOT/demo-2.0-20261022.101500-1.jar";
        Assert.Equal(HttpStatusCode.Conflict, await Status(client, HttpMethod.Put,
            firstJar, Writer, "overwritten"u8.ToArray()));
        using var unchanged = await client.SendAsync(Request(HttpMethod.Get, firstJar, Reader));
        Assert.Equal("first generation", await unchanged.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, HttpMethod.Put,
            "/maven/" + Ga + "/2.0-SNAPSHOT/demo-2.0-SNAPSHOT.jar", Writer, "mutable"u8.ToArray()));
        using var actual = await client.SendAsync(Request(HttpMethod.Get, metadata, Reader));
        Assert.Equal(second, await actual.Content.ReadAsByteArrayAsync());
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        Assert.Equal(4, db.MavenAssets().Count);
        Assert.Single(db.MavenVersions("org.example", "demo"));
        Assert.True(MavenMetadata.ReferencesValid(Assert.Single(db.MavenMetadata()), db));
    }

    [Fact]
    public async Task MetadataRejectsMissingReferencesAndCorruptContentBeforePublication()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var release = "/maven/" + MetadataPath;
        Assert.Equal(HttpStatusCode.Conflict, await Status(client, HttpMethod.Put, release, Writer,
            ReleaseMetadata("1.0", "<version>1.0</version>")));
        var snapshot = "/maven/" + Ga + "/2.0-SNAPSHOT/maven-metadata.xml";
        Assert.Equal(HttpStatusCode.Conflict, await Status(client, HttpMethod.Put, snapshot, Writer, SnapshotMetadata(1)));
        await PublishSnapshot(client, 1);
        var blob = Path.Combine(host.Root, "blobs", "maven", "org", "example", "demo",
            "2.0-SNAPSHOT", "demo-2.0-20261022.101500-1.jar");
        await File.WriteAllTextAsync(blob, "corruption");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(client, HttpMethod.Put,
            snapshot, Writer, SnapshotMetadata(1)));
        Assert.Empty(host.Services.GetRequiredService<IPackageDatabase>().MavenMetadata());
    }

    [Fact]
    public async Task ConcurrentMetadataWritersCannotRemoveEachOthersVersions()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        await PublishRelease(client, "1.0");
        var url = "/maven/" + MetadataPath;
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put, url,
            Writer, ReleaseMetadata("1.0", "<version>1.0</version>")));
        foreach (var version in new[] { "1.1", "1.2" }) await PublishRelease(client, version);
        var attempts = await Task.WhenAll(new[] { "1.1", "1.2" }.Select(version => Status(
            client, HttpMethod.Put, url, Writer, ReleaseMetadata(version,
                "<version>1.0</version><version>" + version + "</version>", "20261022110000"))));
        Assert.Single(attempts, x => x == HttpStatusCode.NoContent);
        Assert.Single(attempts, x => x == HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Conflict, await Status(client, HttpMethod.Put, url, Writer,
            ReleaseMetadata("1.3", "<version>1.0</version><version>1.1</version><version>1.2</version><version>1.3</version>",
                "20261022120000")));
        await PublishRelease(client, "1.3");
        Assert.Equal(HttpStatusCode.NoContent, await Status(client, HttpMethod.Put, url, Writer,
            ReleaseMetadata("1.3", "<version>1.0</version><version>1.1</version><version>1.2</version><version>1.3</version>",
                "20261022120000")));
    }

    [Fact]
    public async Task FirstNativeMetadataWritePreservesExistingGeneratedVersionInventory()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        await PublishRelease(client, "1.0");
        await PublishRelease(client, "1.1");
        var url = "/maven/" + MetadataPath;
        Assert.Equal(HttpStatusCode.Conflict, await Status(client, HttpMethod.Put, url, Writer,
            ReleaseMetadata("1.1", "<version>1.1</version>")));
        Assert.Null(host.Services.GetRequiredService<IPackageDatabase>().GetMavenMetadata("", MetadataPath));
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put, url, Writer,
            ReleaseMetadata("1.1", "<version>1.0</version><version>1.1</version>")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArtifactIdEndingSnapshotKeepsItsReleaseMetadataIdentity(bool privateFeed)
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var scope = privateFeed ? "suffix-maven" : "";
        if (privateFeed)
        {
            var at = DateTimeOffset.UtcNow.ToString("O");
            Assert.True(db.TryCreateDraftFeed(new FeedDefinition(scope, "maven", "hosted", "private",
                "Synthetic suffix fixture", false, at, at), "synthetic"));
            Assert.True(db.TrySetHostedFeedEnabled(scope, "maven", true, "synthetic"));
        }
        var prefix = privateFeed ? "/feeds/" + scope + "/maven/" : "/maven/";
        var path = "org/example/demo-SNAPSHOT/maven-metadata.xml";
        var artifact = "org/example/demo-SNAPSHOT/1.0/demo-SNAPSHOT-1.0.pom";
        var pom = "<project><groupId>org.example</groupId><artifactId>demo-SNAPSHOT</artifactId><version>1.0</version></project>"u8.ToArray();
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put, prefix + artifact, Admin, pom));
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Get, prefix + path, Admin));
        var metadata = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(
            ReleaseMetadata("1.0", "<version>1.0</version>")).Replace("<artifactId>demo</artifactId>",
            "<artifactId>demo-SNAPSHOT</artifactId>"));
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put,
            prefix + path, privateFeed ? Admin : Writer, metadata));
        using var exact = await client.SendAsync(Request(HttpMethod.Get, prefix + path, Admin));
        Assert.Equal(metadata, await exact.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NoContent, await Status(client, HttpMethod.Put,
            prefix + path + ".sha256", privateFeed ? Admin : Writer,
            Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(metadata)))));
        Assert.True(MavenMetadata.ReferencesValid(db.GetMavenMetadata(scope, path)!, db));
        // The same physical metadata path must not also become the snapshot
        // metadata for org:example:demo-SNAPSHOT.
        var conflicting = "org/example/demo-SNAPSHOT/example-demo-20261022.101500-1.pom";
        var otherPom = "<project><groupId>org</groupId><artifactId>example</artifactId><version>demo-SNAPSHOT</version></project>"u8.ToArray();
        Assert.Equal(HttpStatusCode.Conflict, await Status(client, HttpMethod.Put,
            prefix + conflicting, Admin, otherPom));
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Get, prefix + artifact, Admin));
    }

    [Fact]
    public async Task ConcurrentFirstArtifactsCannotCreateAnAmbiguousMetadataPath()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var release = Status(client, HttpMethod.Put,
            "/maven/org/example/demo-SNAPSHOT/1.0/demo-SNAPSHOT-1.0.pom", Admin,
            "<project><groupId>org.example</groupId><artifactId>demo-SNAPSHOT</artifactId><version>1.0</version></project>"u8.ToArray());
        var snapshot = Status(client, HttpMethod.Put,
            "/maven/org/example/demo-SNAPSHOT/example-demo-20261022.101500-1.pom", Admin,
            "<project><groupId>org</groupId><artifactId>example</artifactId><version>demo-SNAPSHOT</version></project>"u8.ToArray());
        var results = await Task.WhenAll(release, snapshot);
        Assert.Single(results, x => x == HttpStatusCode.Created);
        Assert.Single(results, x => x == HttpStatusCode.Conflict);
        Assert.Single(host.Services.GetRequiredService<IPackageDatabase>().MavenAssets());
    }

    [Fact]
    public async Task PrivateMetadataUsesExactFeedGrantsAndNeverFallsBackToGlobal()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var at = DateTimeOffset.UtcNow.ToString("O");
        foreach (var feed in new[] { "maven-alpha", "maven-beta" })
        {
            Assert.True(db.TryCreateDraftFeed(new FeedDefinition(feed, "maven", "hosted", "private",
                "Synthetic Maven", false, at, at), "synthetic"));
            Assert.True(db.TrySetHostedFeedEnabled(feed, "maven", true, "synthetic"));
        }
        string Token(string feed, string role, string grant)
        {
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            db.CreateManagedToken(new ManagedTokenRecord(Guid.NewGuid().ToString("N"),
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))), role,
                "feed:" + feed + ":" + grant, DateTimeOffset.UtcNow.AddHours(1).ToString("O")));
            return token;
        }
        var alphaWrite = Token("maven-alpha", "write", "publish");
        var alphaRead = Token("maven-alpha", "read", "read");
        var betaWrite = Token("maven-beta", "write", "publish");
        var betaRead = Token("maven-beta", "read", "read");
        await PublishSnapshot(client, 1, "/feeds/maven-alpha/maven/", alphaWrite, payload: "alpha");
        await PublishSnapshot(client, 1, "/feeds/maven-beta/maven/", betaWrite, payload: "beta");
        var relative = Ga + "/2.0-SNAPSHOT/maven-metadata.xml";
        var alpha = "/feeds/maven-alpha/maven/" + relative;
        var beta = "/feeds/maven-beta/maven/" + relative;
        var bytes = SnapshotMetadata(1);
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, HttpMethod.Get, alpha, null));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Put, alpha, betaWrite, bytes));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Put, alpha, alphaRead, bytes));
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put, alpha, alphaWrite, bytes));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, HttpMethod.Get, beta, betaRead));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, HttpMethod.Get, "/maven/" + relative, Reader));
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put, beta, betaWrite, bytes));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Get, alpha, betaRead));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Get, alpha, Reader));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Get, "/maven/" + relative, alphaRead));
        Assert.Equal(2, db.MavenMetadata().Count);
        Assert.All(db.MavenMetadata(), x => Assert.True(MavenMetadata.ReferencesValid(x, db)));
        Assert.True(db.TrySetHostedFeedEnabled("maven-alpha", "maven", false, "synthetic"));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, HttpMethod.Get, alpha, alphaRead));
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Get, beta, betaRead));
    }

    [Fact]
    public async Task MetadataSizeAndNamespaceAreEnforcedBeforePersistence()
    {
        using var host = new Host(1024);
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Status(client, HttpMethod.Put,
            "/maven/" + MetadataPath, Writer, new byte[1025]));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Put,
            "/maven/net/other/demo/maven-metadata.xml", Writer, "<metadata/>"u8.ToArray()));
        Assert.Empty(host.Services.GetRequiredService<IPackageDatabase>().MavenMetadata());
    }

    [Theory]
    [InlineData("<!DOCTYPE metadata [<!ENTITY x SYSTEM 'https://example.invalid/entity'>]><metadata>&x;</metadata>")]
    [InlineData("<metadata><groupId>org.example</groupId><groupId>org.example</groupId><artifactId>demo</artifactId><versioning><versions><version>1.0</version></versions><lastUpdated>20261022100000</lastUpdated></versioning></metadata>")]
    [InlineData("<metadata xmlns:x='urn:foreign'><x:groupId>org.example</x:groupId><artifactId>demo</artifactId><versioning><versions><version>1.0</version></versions><lastUpdated>20261022100000</lastUpdated></versioning></metadata>")]
    [InlineData("<metadata><groupId>org.example</groupId><artifactId>demo</artifactId><versioning><latest><value>1.0</value></latest><versions><version>1.0</version></versions><lastUpdated>20261022100000</lastUpdated></versioning></metadata>")]
    [InlineData("<metadata><groupId>org.example</groupId><artifactId>demo</artifactId><versioning><versions><version>1.0</version><version>1.0</version></versions><lastUpdated>20261022100000</lastUpdated></versioning></metadata>")]
    [InlineData("<metadata><groupId>org.example</groupId><artifactId>demo</artifactId><versioning><versions><version>1.0</version></versions><lastUpdated>20260230100000</lastUpdated></versioning></metadata>")]
    [InlineData("<metadata><groupId>org.example</groupId><artifactId>demo</artifactId><plugins/><versioning><versions><version>1.0</version></versions><lastUpdated>20261022100000</lastUpdated></versioning></metadata>")]
    public void AmbiguousOrUnsafeMetadataIsRejected(string xml)
    {
        Assert.True(MavenMetadata.TryParsePath(MetadataPath, out var coordinates));
        Assert.False(MavenMetadata.TryParse(Encoding.UTF8.GetBytes(xml), coordinates!, out _));
    }

    [Fact]
    public void SnapshotMetadataRejectsMismatchedVersionsDuplicateVariantsAndFutureReferences()
    {
        Assert.True(MavenMetadata.TryParsePath(Ga + "/2.0-SNAPSHOT/maven-metadata.xml", out var coordinates));
        var valid = SnapshotMetadata(2);
        Assert.True(MavenMetadata.TryParse(valid, coordinates!, out _));
        foreach (var invalid in new[]
        {
            Encoding.UTF8.GetString(valid).Replace("<version>2.0-SNAPSHOT</version>", "<version>3.0-SNAPSHOT</version>"),
            Encoding.UTF8.GetString(valid).Replace("<extension>jar</extension>", "<extension>pom</extension>"),
            Encoding.UTF8.GetString(valid).Replace("<snapshot>", "<snapshot><localCopy>true</localCopy>"),
            Encoding.UTF8.GetString(valid).Replace("<extension>pom</extension>", "<extension>module</extension>"),
            Encoding.UTF8.GetString(SnapshotMetadata(2, jarValue: "2.0-20261022.101500-3"))
        }) Assert.False(MavenMetadata.TryParse(Encoding.UTF8.GetBytes(invalid), coordinates!, out _));
    }
}
