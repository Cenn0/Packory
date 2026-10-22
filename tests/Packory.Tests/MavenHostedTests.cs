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

public sealed class MavenHostedTests
{
    private const string Admin = "maven-test-admin-token-of-sufficient-length";
    private const string Reader = "maven-test-reader-token-of-sufficient-length";
    private const string Writer = "maven-test-writer-token-of-sufficient-length";

    private sealed class Host(Action<Settings>? configure = null) : WebApplicationFactory<Program>
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(),
            "packory-maven-tests-" + Guid.NewGuid().ToString("N"));
        public string Root => _data;

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
                    AdminToken = Admin, ReadToken = Reader,
                    WriteToken = Writer, AnonymousRead = false,
                    McpEnabled = false, MaxArtifactBytes = 1024 * 1024
                };
                configure?.Invoke(cfg);
                cfg.Validate();
                services.AddSingleton(cfg);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_data))
                Directory.Delete(_data, recursive: true);
        }
    }

    private static HttpRequestMessage Auth(HttpMethod method, string path,
        string token, byte[]? bytes = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (bytes is not null)
        {
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(
                "application/octet-stream");
        }
        return request;
    }

    private static byte[] Pom(string group, string name, string version) =>
        Encoding.UTF8.GetBytes($"""
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <groupId>{group}</groupId><artifactId>{name}</artifactId>
              <version>{version}</version><packaging>jar</packaging>
            </project>
            """);

    [Fact]
    public async Task MavenRelease_PomAndJar_ImmutableChecksumMetadataAndTampering()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var basePath = "/maven/com/example/demo/1.2.3/demo-1.2.3";
        var pom = Pom("com.example", "demo", "1.2.3");
        var jar = Encoding.UTF8.GetBytes("synthetic, non-executable Java archive fixture");
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync(basePath + ".pom")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Auth(HttpMethod.Put, basePath + ".pom", Reader, pom))).StatusCode);

        using var p = await client.SendAsync(Auth(HttpMethod.Put, basePath + ".pom", Writer, pom));
        Assert.Equal(HttpStatusCode.Created, p.StatusCode);
        using var a = await client.SendAsync(Auth(HttpMethod.Put, basePath + ".jar", Writer, jar));
        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        using var duplicate = await client.SendAsync(Auth(HttpMethod.Put, basePath + ".jar", Writer, jar));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        using var result = await client.SendAsync(Auth(HttpMethod.Get, basePath + ".jar", Reader));
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(jar, await result.Content.ReadAsByteArrayAsync());
        var expectedSha = Convert.ToHexStringLower(SHA256.HashData(jar));
        Assert.Equal("\"" + expectedSha + "\"", result.Headers.ETag?.ToString());
        using var sha = await client.SendAsync(Auth(HttpMethod.Get, basePath + ".jar.sha256", Reader));
        Assert.Equal(HttpStatusCode.OK, sha.StatusCode);
        Assert.Equal(expectedSha, await sha.Content.ReadAsStringAsync());

        using var sha1 = await client.SendAsync(Auth(HttpMethod.Get, basePath + ".jar.sha1", Reader));
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(jar)),
            await sha1.Content.ReadAsStringAsync());
        using var head = await client.SendAsync(Auth(HttpMethod.Head, basePath + ".jar", Reader));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        using var metadata = await client.SendAsync(Auth(HttpMethod.Get,
            "/maven/com/example/demo/maven-metadata.xml", Reader));
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        var document = XDocument.Parse(await metadata.Content.ReadAsStringAsync());
        Assert.Contains(document.Descendants(), x => x.Name.LocalName == "version" && x.Value == "1.2.3");

        var db = host.Services.GetRequiredService<IPackageDatabase>();
        Assert.Equal(expectedSha, db.GetMavenAsset("com/example/demo/1.2.3/demo-1.2.3.jar")?.Sha256);
        Assert.Contains(db.MavenAssets(), x => x.ArtifactId == "demo");
        Assert.Equal("hosted", db.PackageOrigin("maven", "com.example:demo", "1.2.3"));
        using var catalog = await client.SendAsync(Auth(HttpMethod.Get,
            "/api/packages?kind=maven", Reader));
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        Assert.Contains("com.example:demo", await catalog.Content.ReadAsStringAsync());
        using var rawCatalog = await client.SendAsync(Auth(HttpMethod.Get,
            "/api/packages?kind=raw", Reader));
        Assert.Equal(HttpStatusCode.OK, rawCatalog.StatusCode);

        await File.WriteAllTextAsync(Path.Combine(host.Root, "blobs", "maven",
            "com", "example", "demo", "1.2.3", "demo-1.2.3.jar"), "tampered");
        using var corrupted = await client.SendAsync(Auth(HttpMethod.Get, basePath + ".jar", Reader));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, corrupted.StatusCode);
        using var checksumCorrupted = await client.SendAsync(
            Auth(HttpMethod.Get, basePath + ".jar.sha256", Reader));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, checksumCorrupted.StatusCode);
    }

    [Fact]
    public async Task MavenChecksumPutsValidateOnlyCommittedImmutableArtifactBytes()
    {
        using var host = new Host(cfg => cfg.WriteNamespaces = ["maven:com.example/demo"]);
        using var client = host.CreateClient();
        var artifact = "/maven/com/example/demo/1.0/demo-1.0.jar";
        var bytes = "synthetic immutable Maven sidecar fixture"u8.ToArray();
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes));
        var md5 = Convert.ToHexStringLower(MD5.HashData(bytes));

        async Task<HttpStatusCode> Put(string url, string token, byte[] data)
        {
            using var result = await client.SendAsync(Auth(HttpMethod.Put, url, token, data));
            return result.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Created, await Put(artifact, Writer, bytes));
        Assert.Equal(HttpStatusCode.NoContent, await Put(
            artifact + ".sha256", Writer, Encoding.ASCII.GetBytes(sha256.ToUpperInvariant() + "\r\n")));
        Assert.Equal(HttpStatusCode.NoContent, await Put(
            artifact + ".sha1", Writer, Encoding.ASCII.GetBytes(sha1)));
        Assert.Equal(HttpStatusCode.NoContent, await Put(
            artifact + ".md5", Writer, Encoding.ASCII.GetBytes(md5 + "\n")));
        // Idempotence: uploading a correct checksum is only a validation,
        // not a second immutable package publication.
        Assert.Equal(HttpStatusCode.NoContent, await Put(
            artifact + ".sha1", Writer, Encoding.ASCII.GetBytes(sha1)));
        Assert.Equal(sha256, await (await client.SendAsync(Auth(HttpMethod.Get,
            artifact + ".sha256", Reader))).Content.ReadAsStringAsync());
        Assert.Equal(sha1, await (await client.SendAsync(Auth(HttpMethod.Get,
            artifact + ".sha1", Reader))).Content.ReadAsStringAsync());
        Assert.Equal(md5, await (await client.SendAsync(Auth(HttpMethod.Get,
            artifact + ".md5", Reader))).Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, await Put(
            artifact + ".sha1", Writer, Encoding.ASCII.GetBytes(new string('0', 40))));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(
            artifact + ".sha256", Writer, Encoding.ASCII.GetBytes("  " + sha256)));
        Assert.Equal(HttpStatusCode.BadRequest, await Put(
            artifact + ".sha1", Writer, Encoding.ASCII.GetBytes(sha1 + "\n" + sha1)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, await Put(
            artifact + ".sha1", Writer, new byte[131]));
        Assert.Equal(HttpStatusCode.NotFound, await Put(
            "/maven/com/example/demo/2.0/demo-2.0.jar.sha1", Writer,
            Encoding.ASCII.GetBytes(sha1)));
        Assert.Equal(HttpStatusCode.NotFound, await Put(
            "/maven/com/example/demo/maven-metadata.xml.sha1", Writer,
            Encoding.ASCII.GetBytes(sha1)));
        Assert.Equal(HttpStatusCode.Forbidden, await Put(
            "/maven/org/other/demo/1.0/demo-1.0.jar.sha1", Writer,
            Encoding.ASCII.GetBytes(sha1)));
        Assert.Equal(HttpStatusCode.Forbidden, await Put(
            artifact + ".sha1", Reader, Encoding.ASCII.GetBytes(sha1)));
        using var unauthenticated = await client.PutAsync(artifact + ".sha1",
            new ByteArrayContent(Encoding.ASCII.GetBytes(sha1)));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Single(host.Services.GetRequiredService<IPackageDatabase>().MavenAssets());
        Assert.Empty(host.Services.GetRequiredService<IPackageDatabase>().PendingPublications());

        await File.WriteAllTextAsync(Path.Combine(host.Root, "blobs", "maven",
            "com", "example", "demo", "1.0", "demo-1.0.jar"), "tampered");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await Put(
            artifact + ".sha1", Writer, Encoding.ASCII.GetBytes(sha1)));
    }

    [Fact]
    public async Task MavenRejectsMismatchedPomAndDisallowedSnapshotsAndNamespaces()
    {
        using var host = new Host(cfg => cfg.WriteNamespaces = ["maven:com.example/*"]);
        using var client = host.CreateClient();
        var good = "/maven/com/example/test/1.0/test-1.0.pom";
        using var scopeDenied = await client.SendAsync(Auth(HttpMethod.Put,
            "/maven/net/another/test/1.0/test-1.0.pom", Writer, Pom("net.another", "test", "1.0")));
        Assert.Equal(HttpStatusCode.Forbidden, scopeDenied.StatusCode);
        using var mismatch = await client.SendAsync(Auth(HttpMethod.Put, good,
            Writer, Pom("com.example", "wrong", "1.0")));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Null(host.Services.GetRequiredService<IPackageDatabase>().GetMavenAsset(
            "com/example/test/1.0/test-1.0.pom"));

        using var rejectedDtd = await client.SendAsync(Auth(HttpMethod.Put, good, Writer,
            Encoding.UTF8.GetBytes("<!DOCTYPE project [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><project>&e;</project>")));
        Assert.Equal(HttpStatusCode.BadRequest, rejectedDtd.StatusCode);

        var foreignNamespace = """
            <project xmlns="http://maven.apache.org/POM/4.0.0" xmlns:other="urn:untrusted">
              <modelVersion>4.0.0</modelVersion>
              <other:groupId>com.example</other:groupId>
              <artifactId>test</artifactId><version>1.0</version>
            </project>
            """;
        using var spoofed = await client.SendAsync(Auth(HttpMethod.Put, good, Writer,
            Encoding.UTF8.GetBytes(foreignNamespace)));
        Assert.Equal(HttpStatusCode.BadRequest, spoofed.StatusCode);

        using var snapshot = await client.SendAsync(Auth(HttpMethod.Put,
            "/maven/com/example/test/1.1-SNAPSHOT/test-1.1-SNAPSHOT.pom",
            Writer, Pom("com.example", "test", "1.1-SNAPSHOT")));
        Assert.Equal(HttpStatusCode.BadRequest, snapshot.StatusCode);

        using var allowed = await client.SendAsync(Auth(HttpMethod.Put, good,
            Writer, Pom("com.example", "test", "1.0")));
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
        using var invalid = await client.SendAsync(Auth(HttpMethod.Put,
            "/maven/com..example/test/1.0/test-1.0.jar",
            Admin, "invalid"u8.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task ScopedManagedCredentialCannotReadGlobalMavenArtifacts()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var path = "/maven/org/sample/demo/1.0/demo-1.0.pom";
        using var publish = await client.SendAsync(Auth(HttpMethod.Put, path, Admin,
            Pom("org.sample", "demo", "1.0")));
        Assert.Equal(HttpStatusCode.Created, publish.StatusCode);

        var token = new string('d', 64);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        host.Services.GetRequiredService<IPackageDatabase>().CreateManagedToken(
            new ManagedTokenRecord(Guid.NewGuid().ToString("N"), hash, "read",
                "feed:private-nuget:read", DateTimeOffset.UtcNow.AddHours(1).ToString("O")));
        using var denied = await client.SendAsync(Auth(HttpMethod.Get, path, token));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var deniedMetadata = await client.SendAsync(Auth(HttpMethod.Get,
            "/maven/org/sample/demo/maven-metadata.xml", token));
        Assert.Equal(HttpStatusCode.Forbidden, deniedMetadata.StatusCode);
        using var deniedCatalog = await client.SendAsync(Auth(HttpMethod.Get,
            "/api/packages?kind=maven", token));
        Assert.Equal(HttpStatusCode.Forbidden, deniedCatalog.StatusCode);
        using var permitted = await client.SendAsync(Auth(HttpMethod.Get, path, Reader));
        Assert.Equal(HttpStatusCode.OK, permitted.StatusCode);
    }

    [Fact]
    public async Task MavenPublicationJournalAndRecoveryDistinguishCommittedAndMissingBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-maven-recovery-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = new Settings { DataDirectory = root };
            var db = new Database(cfg);
            db.Initialize();
            var blobs = new BlobStore(cfg);
            var path = "net/fixture/lib/1.0/lib-1.0.jar";
            var key = MavenEndpoints.Key(path);
            var bytes = "synthetic immutable recovery bytes"u8.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            db.BeginPublication(key, "maven", path, "1.0");
            await using (var source = new MemoryStream(bytes, writable: false))
                Assert.True(await blobs.PutAsync(key, source, 1024 * 1024, CancellationToken.None));
            var recovery = new ArtifactRecovery(cfg, db, blobs,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtifactRecovery>.Instance);
            var pending = Assert.Single(db.PendingPublications());
            Assert.Equal("uncommitted-object", recovery.State(pending));
            Assert.True(db.PublishMavenAsset(new MavenAssetEntry(path, "net.fixture", "lib",
                "1.0", hash, bytes.Length, "application/java-archive",
                DateTimeOffset.UtcNow.ToString("O")), "synthetic"));
            Assert.True(recovery.MetadataCommitted(pending));
            Assert.Equal("committed", recovery.State(pending));
            recovery.Reconcile();
            Assert.Empty(db.PendingPublications());
            Assert.Equal(0, recovery.MissingHostedCount());

            // Absent authoritative bytes fail readiness and are never re-created.
            File.Delete(Path.Combine(root, "blobs", "maven",
                "net", "fixture", "lib", "1.0", "lib-1.0.jar"));
            Assert.Equal(1, recovery.MissingHostedCount());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MavenOfflineIntegrityScannerUsesStoredPublisherDigest()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-maven-integrity-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = new Settings { DataDirectory = root };
            var db = new Database(cfg);
            db.Initialize();
            var security = new SkillSecurityStore(cfg);
            security.Initialize();
            var blobs = new BlobStore(cfg);
            var relative = "org/sample/example/2.0/example-2.0.jar";
            var bytes = "synthetic integrity fixture"u8.ToArray();
            var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var record = new MavenAssetEntry(relative, "org.sample", "example", "2.0",
                digest, bytes.Length, "application/java-archive",
                DateTimeOffset.UtcNow.ToString("O"));
            Assert.True(db.PublishMavenAsset(record, "synthetic"));
            await using (var source = new MemoryStream(bytes, writable: false))
                Assert.True(await blobs.PutAsync(MavenEndpoints.Key(relative),
                    source, 1024 * 1024, CancellationToken.None));
            var scan = new ArtifactIntegrityScanner(db, blobs, security, cfg);
            var positive = await scan.ScanAsync(0, 10, CancellationToken.None);
            Assert.Single(positive.Items, item =>
                item.Kind == "maven" && item.State == "digest_match" &&
                item.DigestBasis == "sha256");

            var path = Path.Combine(root, "blobs", "maven",
                "org", "sample", "example", "2.0", "example-2.0.jar");
            await File.WriteAllTextAsync(path, "tampered");
            var negative = await scan.ScanAsync(0, 10, CancellationToken.None);
            Assert.Single(negative.Items, item =>
                item.Kind == "maven" && item.State == "digest_mismatch");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MavenV6_SqliteMigrationTracksIndependentArtifactDigests()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-maven-schema-" +
            Guid.NewGuid().ToString("N"));
        try
        {
            var db = new Database(new Settings { DataDirectory = root });
            db.Initialize();
            var record = new MavenAssetEntry("org/example/fixture/1.0/fixture-1.0.pom",
                "org.example", "fixture", "1.0", new string('a', 64),
                128, "application/xml", DateTimeOffset.UtcNow.ToString("O"));
            Assert.True(db.PublishMavenAsset(record, "synthetic"));
            Assert.False(db.PublishMavenAsset(record, "synthetic"));
            Assert.Single(db.MavenVersions("org.example", "fixture"));
            db.Initialize();
            Assert.Equal(record, db.GetMavenAsset(record.Path));
            Assert.Equal(15, SchemaVersionGuard.Current);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
