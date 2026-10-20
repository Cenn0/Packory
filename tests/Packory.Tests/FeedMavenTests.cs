using System.Net;
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
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class FeedMavenTests
{
    private const string Admin = "synthetic-e0-maven-admin-token-long-enough-for-tests";
    private const string Reader = "synthetic-e0-maven-global-reader-token-long-enough";
    private const string Writer = "synthetic-e0-maven-global-writer-token-long-enough";
    private const string Artifact = "com/example/demo/1.0.0/demo-1.0.0.jar";
    private const string Pom = "com/example/demo/1.0.0/demo-1.0.0.pom";
    private const string Metadata = "com/example/demo/maven-metadata.xml";

    private sealed class Host : WebApplicationFactory<Program>
    {
        private readonly string data = Path.Combine(Path.GetTempPath(),
            "packory-feed-maven-" + Guid.NewGuid().ToString("N"));
        public string Root => data;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Packory:DataDirectory"] = data,
                    ["Packory:PublicBaseUrl"] = "http://localhost:8080",
                    ["Packory:AdminToken"] = Admin,
                    ["Packory:ReadToken"] = Reader,
                    ["Packory:WriteToken"] = Writer,
                    ["Packory:AnonymousRead"] = "true"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                var cfg = new Settings
                {
                    DataDirectory = data, PublicBaseUrl = "http://localhost:8080",
                    AdminToken = Admin, ReadToken = Reader, WriteToken = Writer,
                    AnonymousRead = true, McpEnabled = false,
                    MaxArtifactBytes = 1024 * 1024
                };
                cfg.Validate();
                services.AddSingleton(cfg);
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(data))
                Directory.Delete(data, recursive: true);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path,
        string? token = null, byte[]? content = null)
    {
        var message = new HttpRequestMessage(method, path);
        if (token is not null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (content is not null)
        {
            message.Content = new ByteArrayContent(content);
            message.Content.Headers.ContentType =
                new MediaTypeHeaderValue("application/octet-stream");
        }
        return message;
    }

    private static async Task<HttpStatusCode> Status(HttpClient client,
        HttpMethod method, string path, string? token = null, byte[]? body = null)
    {
        using var response = await client.SendAsync(Request(method, path, token, body));
        return response.StatusCode;
    }

    private static async Task Create(HttpClient client, string feedId,
        string mode = "hosted")
    {
        using var request = Request(HttpMethod.Post, "/api/v1/admin/feeds/drafts", Admin);
        request.Content = JsonContent.Create(new
        {
            id = feedId, kind = "maven", mode, displayName = "Synthetic E0 Maven"
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<string> Token(HttpClient client, string role, string grant)
    {
        using var request = Request(HttpMethod.Post, "/api/admin/tokens", Admin);
        request.Content = JsonContent.Create(new
        {
            role, @namespace = grant, expiresInHours = 1
        });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("token").GetString()!;
    }

    [Fact]
    public async Task SameMavenCoordinatesAreImmutableAndIndependentAcrossFeeds()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        await Create(client, "alpha-maven");
        await Create(client, "beta-maven");
        foreach (var feed in new[] { "alpha-maven", "beta-maven" })
            Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
                "/api/v1/admin/feeds/" + feed + "/activate", Admin));
        var alphaRead = await Token(client, "read", "feed:alpha-maven:read");
        var betaRead = await Token(client, "read", "feed:beta-maven:read");
        var alphaWrite = await Token(client, "write", "feed:alpha-maven:publish");
        var betaWrite = await Token(client, "write", "feed:beta-maven:publish");
        string alpha = "/feeds/alpha-maven/maven/" + Artifact;
        string beta = "/feeds/beta-maven/maven/" + Artifact;
        byte[] first = "alpha-immutable-maven"u8.ToArray();
        byte[] second = "beta-immutable-maven"u8.ToArray();

        Assert.Equal(HttpStatusCode.Unauthorized,
            await Status(client, HttpMethod.Get, alpha));
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Get, alpha, Reader));
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Get, alpha, betaRead));
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Put, alpha, betaWrite, first));
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Put, alpha, alphaRead, first));
        Assert.Equal(HttpStatusCode.NotFound,
            await Status(client, HttpMethod.Get, alpha, alphaRead));
        Assert.Equal(HttpStatusCode.Created,
            await Status(client, HttpMethod.Put, alpha, alphaWrite, first));
        Assert.Equal(HttpStatusCode.Created,
            await Status(client, HttpMethod.Put, beta, betaWrite, second));
        Assert.Equal(HttpStatusCode.Conflict,
            await Status(client, HttpMethod.Put, alpha, alphaWrite, second));
        using (var response = await client.SendAsync(Request(HttpMethod.Get, alpha, alphaRead)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(first, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("\"" + Convert.ToHexStringLower(SHA256.HashData(first)) + "\"",
                response.Headers.ETag?.ToString());
        }
        using (var response = await client.SendAsync(Request(HttpMethod.Get, beta, betaRead)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(second, await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(HttpStatusCode.OK,
            await Status(client, HttpMethod.Head, alpha, alphaRead));
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Get,
                "/feeds/alpha-maven/maven/" + Metadata, betaRead));
        Assert.Equal(HttpStatusCode.NotFound,
            await Status(client, HttpMethod.Get, "/maven/" + Artifact, Reader));
        using (var response = await client.SendAsync(Request(HttpMethod.Get,
            "/feeds/alpha-maven/maven/" + Metadata, alphaRead)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.Contains("<version>1.0.0</version>", text);
            Assert.DoesNotContain("beta-maven", text);
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(first));
        using (var response = await client.SendAsync(Request(HttpMethod.Get,
            alpha + ".sha256", alphaRead)))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(sha, await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.NoContent,
            await Status(client, HttpMethod.Put, alpha + ".sha256",
                alphaWrite, Encoding.ASCII.GetBytes(sha + "\n")));
        Assert.Equal(HttpStatusCode.Conflict,
            await Status(client, HttpMethod.Put, alpha + ".sha256",
                alphaWrite, Encoding.ASCII.GetBytes(new string('0', 64))));

        var db = host.Services.GetRequiredService<IPackageDatabase>();
        Assert.Equal(2, db.FeedAssets().Count(x => x.Kind == "maven"));
        Assert.Equal(new[] { "1.0.0" },
            db.FeedMavenVersions("alpha-maven", "com.example", "demo"));
        Assert.Empty(db.PendingPublications());
        Assert.Equal(0, host.Services.GetRequiredService<ArtifactRecovery>().MissingHostedCount());
        var result = await host.Services.GetRequiredService<ArtifactIntegrityScanner>()
            .ScanAsync(0, 10, CancellationToken.None);
        Assert.Equal(2, result.Items.Count(x => x.Kind == "feed-maven" &&
            x.State == "digest_match"));

        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
            "/api/v1/admin/feeds/alpha-maven/deactivate", Admin));
        Assert.Equal(HttpStatusCode.NotFound,
            await Status(client, HttpMethod.Get, alpha, alphaRead));
        Assert.Equal(HttpStatusCode.OK,
            await Status(client, HttpMethod.Get, beta, betaRead));
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
            "/api/v1/admin/feeds/alpha-maven/activate", Admin));
        Assert.Equal(HttpStatusCode.OK,
            await Status(client, HttpMethod.Get, alpha, alphaRead));
    }

    [Fact]
    public async Task MalformedPathsPomIdentityProxyAndTamperingFailClosed()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        await Create(client, "private-maven");
        await Create(client, "proxy-maven", "proxy");
        Assert.Equal(HttpStatusCode.Conflict, await Status(client, HttpMethod.Post,
            "/api/v1/admin/feeds/proxy-maven/activate", Admin));
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
            "/api/v1/admin/feeds/private-maven/activate", Admin));
        var read = await Token(client, "read", "feed:private-maven:read");
        var write = await Token(client, "write", "feed:private-maven:publish");
        var root = "/feeds/private-maven/maven/";

        foreach (var path in new[] {
            "com/example/demo/1.0.0-SNAPSHOT/demo-1.0.0-SNAPSHOT.jar",
            "com/example/demo/1.0.0/demo-1.0.0.exe",
            "com/example/demo/1.0.0/demo-1.0.0.jar.sha512",
            "com/example/demo/1.0.0/demo-1.0.0..jar" })
            Assert.Equal(HttpStatusCode.BadRequest, await Status(client,
                HttpMethod.Put, root + path, write, "bad"u8.ToArray()));

        var invalidPom = "<project><groupId>wrong</groupId><artifactId>demo</artifactId><version>1.0.0</version></project>";
        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, HttpMethod.Put,
            root + Pom, write, Encoding.UTF8.GetBytes(invalidPom)));
        var correctPom = "<project><groupId>com.example</groupId><artifactId>demo</artifactId><version>1.0.0</version></project>";
        Assert.Equal(HttpStatusCode.Created, await Status(client, HttpMethod.Put,
            root + Pom, write, Encoding.UTF8.GetBytes(correctPom)));
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Get, "/feeds/proxy-maven/maven/" + Pom, read));
        Assert.Equal(HttpStatusCode.NotFound,
            await Status(client, HttpMethod.Get, root + Artifact, read));

        var location = Path.Combine(host.Root, "blobs", "feeds", "private-maven",
            "maven", "com", "example", "demo", "1.0.0", "demo-1.0.0.pom");
        await File.WriteAllTextAsync(location, "tampered");
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            await Status(client, HttpMethod.Get, root + Pom, read));
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            await Status(client, HttpMethod.Put, root + Pom + ".sha256",
                write, Encoding.ASCII.GetBytes(new string('0', 64))));
    }
}
