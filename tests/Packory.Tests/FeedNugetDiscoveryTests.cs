using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class FeedNugetDiscoveryTests
{
    private const string Admin = "synthetic-nuget-admin-token-for-tests-12345678";
    private const string Package = "example.demo";

    private sealed class Host : WebApplicationFactory<Program>
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(),
            "packory-feed-nuget-discovery-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                services.AddSingleton(new Settings
                {
                    DataDirectory = Root,
                    PublicBaseUrl = "http://localhost:8080",
                    AdminToken = Admin,
                    AnonymousRead = true,
                    MaxArtifactBytes = 2 * 1024 * 1024,
                    MaxMetadataBytes = 64 * 1024,
                    McpEnabled = false
                });
            });

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static HttpRequestMessage Request(string path, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<HttpStatusCode> Status(HttpClient client, string path, string? token)
    {
        using var request = Request(path, token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<JsonElement> Json(HttpClient client, string path, string token)
    {
        using var request = Request(path, token);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string CreateFeed(IPackageDatabase db, string name, char tokenCharacter)
    {
        var at = DateTimeOffset.UtcNow.ToString("O");
        Assert.True(db.TryCreateDraftFeed(new FeedDefinition(name, "nuget", "hosted",
            "private", "Synthetic test feed", false, at, at), "synthetic"));
        Assert.True(db.TryActivateHostedNugetFeed(name, "synthetic"));
        var token = new string(tokenCharacter, 64);
        db.CreateManagedToken(new ManagedTokenRecord(
            Guid.NewGuid().ToString("N"),
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            "read", "feed:" + name + ":read", DateTimeOffset.UtcNow.AddHours(1).ToString("O")));
        return token;
    }

    private static byte[] Archive(string id, string version, string description, string dependency)
    {
        var xml = new XDocument(new XElement("package",
            new XElement("metadata",
                new XElement("id", id),
                new XElement("version", version),
                new XElement("description", description),
                new XElement("dependencies",
                    new XElement("group", new XAttribute("targetFramework", "net10.0"),
                        new XElement("dependency", new XAttribute("id", dependency),
                            new XAttribute("version", "[1.0.0,)")))))));
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(id + ".nuspec");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(xml.ToString());
        }
        return stream.ToArray();
    }

    private static async Task AddRevision(Host host, string feed, string version,
        string description, string dependency, string id = Package)
    {
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var blobs = host.Services.GetRequiredService<BlobStore>();
        var bytes = Archive(id, version, description, dependency);
        var key = FeedNugetEndpoints.BlobKey(feed, id, version);
        using var content = new MemoryStream(bytes, writable: false);
        Assert.True(await blobs.PutAsync(key, content, 2 * 1024 * 1024, CancellationToken.None));
        Assert.True(db.PublishFeedNuget(feed, id, version, description,
            Convert.ToBase64String(SHA512.HashData(bytes)), bytes.LongLength, "synthetic"));
    }

    [Fact]
    public async Task DiscoveryRequiresExactFeedGrantAndDoesNotMixEqualCoordinates()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var alpha = CreateFeed(db, "nuget-alpha", 'a');
        var beta = CreateFeed(db, "nuget-beta", 'b');
        await AddRevision(host, "nuget-alpha", "1.0.0", "Alpha description", "alpha.dependency");
        await AddRevision(host, "nuget-beta", "1.0.0", "Beta description", "beta.dependency");
        await AddRevision(host, "nuget-alpha", "2.0.0", "New Alpha", "alpha.extra");

        const string a = "/feeds/nuget-alpha/nuget/v3/";
        const string b = "/feeds/nuget-beta/nuget/v3/";
        var service = await Json(client, a + "index.json", alpha);
        var resources = service.GetProperty("resources").EnumerateArray()
            .Select(x => x.GetProperty("@type").GetString()).ToArray();
        Assert.Contains("SearchQueryService/3.5.0", resources);
        Assert.Contains("RegistrationsBaseUrl/3.6.0", resources);
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, a + "search?q=example", null));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, a + "search?q=example", beta));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client,
            a + "registration/example.demo/index.json", beta));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client,
            a + "registration/example.demo/1.0.0.json", beta));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client,
            a + "registration/example.demo/page/0.json", beta));

        var searchA = await Json(client, a + "search?q=example", alpha);
        Assert.Equal(1, searchA.GetProperty("totalHits").GetInt32());
        var itemA = searchA.GetProperty("data")[0];
        Assert.Equal(Package, itemA.GetProperty("id").GetString());
        Assert.Equal(2, itemA.GetProperty("versions").GetArrayLength());
        Assert.DoesNotContain("nuget-beta", searchA.ToString());
        var searchB = await Json(client, b + "search?q=example", beta);
        Assert.Equal(1, searchB.GetProperty("data")[0].GetProperty("versions").GetArrayLength());
        var index = await Json(client, a + "registration/example.demo/index.json", alpha);
        Assert.Equal(1, index.GetProperty("count").GetInt32());
        Assert.Equal(2, index.GetProperty("items")[0].GetProperty("count").GetInt32());
        var page = await Json(client, a + "registration/example.demo/page/0.json", alpha);
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        var leafA = await Json(client, a + "registration/example.demo/1.0.0.json", alpha);
        var leafB = await Json(client, b + "registration/example.demo/1.0.0.json", beta);
        Assert.Equal("Alpha description", leafA.GetProperty("catalogEntry").GetProperty("description").GetString());
        Assert.Equal("Beta description", leafB.GetProperty("catalogEntry").GetProperty("description").GetString());
        Assert.Equal("alpha.dependency", leafA.GetProperty("catalogEntry")
            .GetProperty("dependencyGroups")[0].GetProperty("dependencies")[0].GetProperty("id").GetString());
        Assert.DoesNotContain("nuget-beta", leafA.ToString());
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, a + "registration/example.demo/9.0.0.json", alpha));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, a + "registration/example.demo/page/1.json", alpha));
        Assert.Equal(HttpStatusCode.BadRequest, await Status(client, a + "search?skip=-1", alpha));

        Assert.True(db.TryDeactivateHostedNugetFeed("nuget-alpha", "synthetic"));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, a + "search?q=example", alpha));
        Assert.Equal(HttpStatusCode.OK, await Status(client, b + "search?q=example", beta));
    }

    [Fact]
    public async Task CorruptedCommittedPackageCannotProduceRegistrationLeaf()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var token = CreateFeed(db, "nuget-corrupt", 'c');
        await AddRevision(host, "nuget-corrupt", "1.0.0", "Stored", "some.dependency");
        var path = host.Services.GetRequiredService<BlobStore>()
            .Existing(FeedNugetEndpoints.BlobKey("nuget-corrupt", Package, "1.0.0"));
        Assert.NotNull(path);
        await File.WriteAllBytesAsync(path!, "corrupted bytes"u8.ToArray());

        const string root = "/feeds/nuget-corrupt/nuget/v3/registration/example.demo/";
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            await Status(client, root + "1.0.0.json", token));
        Assert.Equal(HttpStatusCode.ServiceUnavailable,
            await Status(client, root + "page/0.json", token));
    }
}
