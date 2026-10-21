using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class FeedPyPiTests
{
    private const string Name = "e0-python-demo";
    private const string Version = "1.2.3";
    private const string Filename = "e0_python_demo-1.2.3-py3-none-any.whl";

    private static byte[] Wheel(string body, string metadataVersion = Version, string? unsafePath = null, string? dependency = null)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            static void Add(ZipArchive zip, string path, string value)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(value);
            }
            Add(archive, "e0_python_demo-1.2.3.dist-info/METADATA",
                "Metadata-Version: 2.1\nName: e0_python_demo\nVersion: " + metadataVersion + "\nRequires-Python: >=3.10\n" +
                (dependency is null ? "" : "Requires-Dist: " + dependency + "\n"));
            Add(archive, unsafePath ?? "e0_python_demo/__init__.py", body);
            Add(archive, "e0_python_demo-1.2.3.dist-info/WHEEL", "Wheel-Version: 1.0\nRoot-Is-Purelib: true\nTag: py3-none-any\n");
        }
        return output.ToArray();
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string filename = Filename, string version = Version, string? digest = null)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("e0_python_demo"), "name" }, { new StringContent(version), "version" },
            { new StringContent("file_upload"), ":action" }, { new StringContent("1"), "protocol_version" },
            { new StringContent(digest ?? Convert.ToHexStringLower(SHA256.HashData(bytes))), "sha256_digest" },
            { new StringContent(">=3.10"), "requires_python" },
            { new ByteArrayContent(bytes), "content", filename }
        };
        return form;
    }

    [Fact]
    public async Task NativeSimpleMetadataAndDistributionsStayWithinExactFeed()
    {
        using var host = new FeedEcosystemHost();
        using var client = host.CreateClient();
        foreach (var feed in new[] { "python-alpha", "python-beta" }) await FeedEcosystemHost.Create(client, feed, "pypi");
        var alphaRead = await FeedEcosystemHost.Grant(client, "python-alpha", false);
        var alphaWrite = await FeedEcosystemHost.Grant(client, "python-alpha", true);
        var betaRead = await FeedEcosystemHost.Grant(client, "python-beta", false);
        var betaWrite = await FeedEcosystemHost.Grant(client, "python-beta", true);
        var a = "/feeds/python-alpha/pypi";
        var b = "/feeds/python-beta/pypi";
        var alpha = Wheel("def answer(): return 11");
        var beta = Wheel("def answer(): return 22");
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Post, a + "/legacy/", alphaWrite.Token, Upload(alpha)));
        // Twine sends Basic auth with an arbitrary username and token as password.
        using (var request = new HttpRequestMessage(HttpMethod.Post, b + "/legacy/") { Content = Upload(beta) })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("__token__:" + betaWrite.Token)));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(HttpStatusCode.Conflict, await FeedEcosystemHost.Status(client, HttpMethod.Post, a + "/legacy/", alphaWrite.Token, Upload(beta)));
        Assert.Equal(HttpStatusCode.Forbidden, await FeedEcosystemHost.Status(client, HttpMethod.Post, a + "/legacy/", alphaRead.Token, Upload(alpha)));
        Assert.Equal(HttpStatusCode.Forbidden, await FeedEcosystemHost.Status(client, HttpMethod.Post, b + "/legacy/", alphaWrite.Token, Upload(alpha)));
        foreach (var suffix in new[] { "/simple/", "/simple/" + Name + "/", "/files/" + Name + "/" + Filename })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await FeedEcosystemHost.Status(client, HttpMethod.Get, a + suffix));
            Assert.Equal(HttpStatusCode.Forbidden, await FeedEcosystemHost.Status(client, HttpMethod.Get, a + suffix, betaRead.Token));
            Assert.Equal(HttpStatusCode.Forbidden, await FeedEcosystemHost.Status(client, HttpMethod.Get, a + suffix, FeedEcosystemHost.Reader));
            Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Head, a + suffix, alphaRead.Token));
        }
        using (var request = FeedEcosystemHost.Request(HttpMethod.Get, a + "/simple/E0_Python.Demo/", alphaRead.Token))
        {
            request.Headers.Accept.ParseAdd("application/vnd.pypi.simple.v1+json");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var file = Assert.Single(document.RootElement.GetProperty("files").EnumerateArray());
            Assert.Contains(a, file.GetProperty("url").GetString());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(alpha)), file.GetProperty("hashes").GetProperty("sha256").GetString());
            Assert.Equal(">=3.10", file.GetProperty("requires-python").GetString());
        }
        using (var response = await client.SendAsync(FeedEcosystemHost.Request(HttpMethod.Get, a + "/simple/" + Name + "/", alphaRead.Token)))
        {
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains(a + "/files/", html);
            Assert.Contains("data-requires-python=\"&gt;=3.10\"", html);
            Assert.DoesNotContain(b, html);
        }
        foreach (var (url, token, expected) in new[] { (a, alphaRead.Token, alpha), (b, betaRead.Token, beta) })
        {
            using var response = await client.SendAsync(FeedEcosystemHost.Request(HttpMethod.Get, url + "/files/" + Name + "/" + Filename, token));
            Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
        }
        Assert.Equal(HttpStatusCode.NotFound, await FeedEcosystemHost.Status(client, HttpMethod.Get, "/pypi/simple/" + Name + "/"));
        Assert.Equal(HttpStatusCode.NotFound, await FeedEcosystemHost.Status(client, HttpMethod.Get, a + "/simple/missing/", alphaRead.Token));
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Post, "/api/v1/admin/feeds/python-alpha/deactivate", FeedEcosystemHost.Admin));
        Assert.Equal(HttpStatusCode.NotFound, await FeedEcosystemHost.Status(client, HttpMethod.Get, a + "/simple/", alphaRead.Token));
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Get, b + "/simple/", betaRead.Token));
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Post, "/api/v1/admin/feeds/python-alpha/activate", FeedEcosystemHost.Admin));
        Assert.Equal(HttpStatusCode.NoContent, await FeedEcosystemHost.Status(client, HttpMethod.Delete, "/api/admin/tokens/" + alphaRead.Id, FeedEcosystemHost.Admin));
        Assert.Equal(HttpStatusCode.Unauthorized, await FeedEcosystemHost.Status(client, HttpMethod.Get, a + "/simple/", alphaRead.Token));
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        Assert.Equal(2, db.FeedAssets().Count);
        Assert.Empty(db.PendingPublications());
    }

    [Fact]
    public async Task InvalidArchiveCoordinatesDigestAndTamperFailClosed()
    {
        using var host = new FeedEcosystemHost();
        using var client = host.CreateClient();
        await FeedEcosystemHost.Create(client, "python-safe", "pypi");
        var write = await FeedEcosystemHost.Grant(client, "python-safe", true);
        var url = "/feeds/python-safe/pypi/legacy/";
        var bytes = Wheel("synthetic");
        Assert.Equal(HttpStatusCode.BadRequest, await FeedEcosystemHost.Status(client, HttpMethod.Post, url, write.Token, Upload(bytes, digest: new string('0', 64))));
        Assert.Equal(HttpStatusCode.BadRequest, await FeedEcosystemHost.Status(client, HttpMethod.Post, url, write.Token, Upload(bytes, version: "9.9.9")));
        Assert.Equal(HttpStatusCode.BadRequest, await FeedEcosystemHost.Status(client, HttpMethod.Post, url, write.Token, Upload(Wheel("synthetic", metadataVersion: "9.9.9"))));
        Assert.Equal(HttpStatusCode.BadRequest, await FeedEcosystemHost.Status(client, HttpMethod.Post, url, write.Token, Upload(Wheel("synthetic", unsafePath: "../escape"))));
        Assert.Equal(HttpStatusCode.BadRequest, await FeedEcosystemHost.Status(client, HttpMethod.Post, url, write.Token, Upload("not a zip"u8.ToArray())));
        Assert.Empty(host.Services.GetRequiredService<IPackageDatabase>().FeedAssets());
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Post, url, write.Token, Upload(bytes)));
        var entry = Assert.Single(host.Services.GetRequiredService<IPackageDatabase>().FeedAssets());
        var blobs = host.Services.GetRequiredService<BlobStore>();
        await File.WriteAllTextAsync(blobs.Existing(FeedAssetEntry.BlobKey(entry))!, "corrupt");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await FeedEcosystemHost.Status(client, HttpMethod.Get,
            "/feeds/python-safe/pypi/simple/" + Name + "/", write.Token));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, await FeedEcosystemHost.Status(client, HttpMethod.Get,
            "/feeds/python-safe/pypi/files/" + Name + "/" + Filename, write.Token));
    }

    [Fact]
    public async Task MultipleDistributionFilesForOneVersionRemainImmutable()
    {
        using var host = new FeedEcosystemHost();
        using var client = host.CreateClient();
        await FeedEcosystemHost.Create(client, "python-multiple", "pypi");
        var write = await FeedEcosystemHost.Grant(client, "python-multiple", true);
        var url = "/feeds/python-multiple/pypi/legacy/";
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Post, url, write.Token, Upload(Wheel("first"))));
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Post, url, write.Token,
            Upload(Wheel("second"), filename: "e0_python_demo-1.2.3-1-py3-none-any.whl")));
        Assert.Equal(2, host.Services.GetRequiredService<IPackageDatabase>().FeedPackageAssets("python-multiple", "pypi", Name).Count);
    }

    [Theory]
    [InlineData("other @ https://registry.example/other.whl")]
    [InlineData("other\n @ https://registry.example/other.whl")]
    [InlineData("other @ file:///tmp/other.whl")]
    [InlineData("other @ git+https://registry.example/repository")]
    public async Task DirectReferenceDependenciesCannotBypassPrivateIndex(string dependency)
    {
        using var host = new FeedEcosystemHost();
        using var client = host.CreateClient();
        await FeedEcosystemHost.Create(client, "python-origin", "pypi");
        var writer = await FeedEcosystemHost.Grant(client, "python-origin", true);
        Assert.Equal(HttpStatusCode.BadRequest, await FeedEcosystemHost.Status(client, HttpMethod.Post,
            "/feeds/python-origin/pypi/legacy/", writer.Token, Upload(Wheel("synthetic", dependency: dependency))));
        Assert.Empty(host.Services.GetRequiredService<IPackageDatabase>().FeedAssets());
        Assert.Empty(host.Services.GetRequiredService<IPackageDatabase>().PendingPublications());
    }

    [Fact]
    public async Task OrdinaryFeedDependencyWithEnvironmentMarkerIsPreservedInVerifiedBytes()
    {
        using var host = new FeedEcosystemHost();
        using var client = host.CreateClient();
        await FeedEcosystemHost.Create(client, "python-deps", "pypi");
        var writer = await FeedEcosystemHost.Grant(client, "python-deps", true);
        var bytes = Wheel("synthetic", dependency: "other (>=1.0,<2.0); python_version >= '3.10'");
        Assert.Equal(HttpStatusCode.OK, await FeedEcosystemHost.Status(client, HttpMethod.Post,
            "/feeds/python-deps/pypi/legacy/", writer.Token, Upload(bytes)));
        using var response = await client.SendAsync(FeedEcosystemHost.Request(HttpMethod.Get,
            "/feeds/python-deps/pypi/files/" + Name + "/" + Filename, writer.Token));
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3rc1.post2.dev3+local.1", true)]
    [InlineData("1!1.2.3", false)]
    [InlineData("1.2.3\n", false)]
    [InlineData("../bad", false)]
    public void PythonVersionProfileIsExplicit(string version, bool valid) => Assert.Equal(valid, FeedPyPiEndpoints.IsVersion(version));
}
