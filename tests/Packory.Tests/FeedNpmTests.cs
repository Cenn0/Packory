using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class FeedNpmTests
{
    private const string Admin = "private-npm-admin-synthetic-token-long-enough";
    private const string GlobalReader = "private-npm-global-reader-synthetic-token-long-enough";
    private const string Name = "@private-example/demo";

    private sealed class Host : WebApplicationFactory<Program>
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "packory-feed-npm-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Settings>();
            services.AddSingleton(new Settings
            {
                DataDirectory = Root, PublicBaseUrl = "http://localhost:8080", AdminToken = Admin,
                ReadToken = GlobalReader, AnonymousRead = true, McpEnabled = false,
                ReservedNpmScopes = ["@private-example"], MaxArtifactBytes = 1024 * 1024,
                MaxMetadataBytes = 64 * 1024
            });
        });
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<HttpStatusCode> Status(HttpClient client, HttpMethod method, string path, string? token)
    {
        using var request = Request(method, path, token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private static async Task<(string Read, string Write)> Create(HttpClient client, string feed, string mode = "hosted")
    {
        using (var request = Request(HttpMethod.Post, "/api/v1/admin/feeds/drafts", Admin))
        {
            request.Content = JsonContent.Create(new { id = feed, kind = "npm", mode, displayName = "Synthetic npm" });
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        if (mode == "hosted")
            Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post,
                "/api/v1/admin/feeds/" + feed + "/activate", Admin));
        async Task<string> Grant(string role, string permission)
        {
            using var request = Request(HttpMethod.Post, "/api/admin/tokens", Admin);
            request.Content = JsonContent.Create(new { role, @namespace = "feed:" + feed + ":" + permission, expiresInHours = 1 });
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        }
        return (await Grant("read", "read"), await Grant("write", "publish"));
    }

    private static string Url(string feed, string name = Name) => "/feeds/" + feed + "/npm/" + Uri.EscapeDataString(name);

    private static byte[] Archive(string name = Name, string version = "1.0.0", string flavor = "alpha", string? badPath = null)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            void Add(string path, byte[] data)
            {
                using var stream = new MemoryStream(data, writable: false);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    DataStream = stream,
                    ModificationTime = DateTimeOffset.UnixEpoch
                };
                tar.WriteEntry(entry);
            }
            Add("package/package.json", JsonSerializer.SerializeToUtf8Bytes(new { name, version, main = "index.js" }));
            Add(badPath ?? "package/index.js", Encoding.UTF8.GetBytes("module.exports = '" + flavor + "';\n"));
        }
        return output.ToArray();
    }

    private static JsonObject Document(byte[] tarball, string name = Name, string version = "1.0.0", string tag = "latest") =>
        new()
        {
            ["name"] = name,
            ["versions"] = new JsonObject
            {
                [version] = new JsonObject
                {
                    ["name"] = name, ["version"] = version, ["description"] = "Synthetic private package",
                    ["dist"] = new JsonObject
                    {
                        ["tarball"] = "https://example.invalid/must-never-be-fetched.tgz",
                        ["integrity"] = "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball)),
                        ["shasum"] = Convert.ToHexStringLower(SHA1.HashData(tarball))
                    }
                }
            },
            ["dist-tags"] = new JsonObject { [tag] = version },
            ["_attachments"] = new JsonObject
            {
                ["demo-" + version + ".tgz"] = new JsonObject
                {
                    ["content_type"] = "application/octet-stream", ["length"] = tarball.Length,
                    ["data"] = Convert.ToBase64String(tarball)
                }
            }
        };

    private static async Task<HttpStatusCode> Publish(HttpClient client, string feed, string? token,
        JsonObject document, string name = Name)
    {
        using var request = Request(HttpMethod.Put, Url(feed, name), token);
        request.Content = JsonContent.Create(document);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Theory]
    [InlineData(Name)]
    [InlineData("private-example-demo")]
    public async Task SameNameAndVersionHaveIndependentBytesAndCredentials(string name)
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var alpha = await Create(client, "npm-alpha");
        var beta = await Create(client, "npm-beta");
        var aBytes = Archive(name, flavor: "alpha");
        var bBytes = Archive(name, flavor: "beta");
        Assert.Equal(HttpStatusCode.Unauthorized, await Publish(client, "npm-alpha", null, Document(aBytes, name), name));
        Assert.Equal(HttpStatusCode.Forbidden, await Publish(client, "npm-alpha", alpha.Read, Document(aBytes, name), name));
        Assert.Equal(HttpStatusCode.Forbidden, await Publish(client, "npm-beta", alpha.Write, Document(aBytes, name), name));
        Assert.Equal(HttpStatusCode.Created, await Publish(client, "npm-alpha", alpha.Write, Document(aBytes, name), name));
        Assert.Equal(HttpStatusCode.Created, await Publish(client, "npm-beta", beta.Write, Document(bBytes, name), name));
        Assert.Equal(HttpStatusCode.Conflict, await Publish(client, "npm-alpha", alpha.Write, Document(bBytes, name), name));

        foreach (var (feed, token, expected) in new[]
        {
            ("npm-alpha", alpha.Read, aBytes), ("npm-beta", beta.Read, bBytes)
        })
        {
            using var request = Request(HttpMethod.Get, Url(feed, name), token);
            request.Headers.Host = "untrusted.invalid";
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
            var packument = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("1.0.0", packument.GetProperty("dist-tags").GetProperty("latest").GetString());
            var manifest = packument.GetProperty("versions").GetProperty("1.0.0");
            var dist = manifest.GetProperty("dist");
            Assert.Equal("sha512-" + Convert.ToBase64String(SHA512.HashData(expected)), dist.GetProperty("integrity").GetString());
            var tarball = dist.GetProperty("tarball").GetString()!;
            Assert.StartsWith("http://localhost:8080/feeds/" + feed + "/npm/", tarball);
            using var tarRequest = Request(HttpMethod.Get, new Uri(tarball).PathAndQuery, token);
            using var tarResponse = await client.SendAsync(tarRequest);
            Assert.Equal(HttpStatusCode.OK, tarResponse.StatusCode);
            Assert.Equal(expected, await tarResponse.Content.ReadAsByteArrayAsync());
            Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Head, new Uri(tarball).PathAndQuery, token));
            Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Get, Url(feed, name) + "/latest", token));
            Assert.Equal(HttpStatusCode.NotFound, await Status(client, HttpMethod.Get, Url(feed, name) + "/9.9.9", token));
            Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, HttpMethod.Get, new Uri(tarball).PathAndQuery, null));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, await Status(client, HttpMethod.Get, Url("npm-alpha", name), null));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Get, Url("npm-alpha", name), beta.Read));
        Assert.Equal(HttpStatusCode.Forbidden, await Status(client, HttpMethod.Get, Url("npm-alpha", name), GlobalReader));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, HttpMethod.Get, Url("npm-alpha", "not-present-in-this-feed"), alpha.Read));
        const string globalOnly = "@private-example/global-only";
        using (var global = Request(HttpMethod.Put, "/npm/" + Uri.EscapeDataString(globalOnly), Admin))
        {
            global.Content = JsonContent.Create(Document(Archive(globalOnly), globalOnly));
            using var published = await client.SendAsync(global);
            Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, HttpMethod.Get,
            Url("npm-alpha", globalOnly), alpha.Read));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, HttpMethod.Get,
            Url("npm-alpha", globalOnly) + "/-/1.0.0.tgz", alpha.Read));
        if (name == Name)
            Assert.Equal(HttpStatusCode.NotFound, await Status(client, HttpMethod.Get,
                "/npm/" + Uri.EscapeDataString(name), null));
        var database = host.Services.GetRequiredService<IPackageDatabase>();
        Assert.Empty(database.NpmHosted(name).Versions);
        Assert.Equal(2, database.FeedPackageAssets("npm-alpha", "npm").Count + database.FeedPackageAssets("npm-beta", "npm").Count);
        Assert.Empty(database.PendingPublications());
        Assert.Equal(0, host.Services.GetRequiredService<ArtifactRecovery>().MissingHostedCount());
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Post, "/api/v1/admin/feeds/npm-alpha/deactivate", Admin));
        Assert.Equal(HttpStatusCode.NotFound, await Status(client, HttpMethod.Get, Url("npm-alpha", name), alpha.Read));
        Assert.Equal(HttpStatusCode.OK, await Status(client, HttpMethod.Get, Url("npm-beta", name), beta.Read));
    }

    [Fact]
    public async Task InvalidArchiveClaimsAndAmbiguousJsonCannotCommitAnything()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var grant = await Create(client, "npm-validation");
        var tarball = Archive();
        var checksum = Document(tarball);
        checksum["versions"]!["1.0.0"]!["dist"]!["integrity"] = "sha512-" + Convert.ToBase64String(new byte[64]);
        Assert.Equal(HttpStatusCode.BadRequest, await Publish(client, "npm-validation", grant.Write, checksum));
        var length = Document(tarball);
        length["_attachments"]!["demo-1.0.0.tgz"]!["length"] = tarball.Length + 1;
        Assert.Equal(HttpStatusCode.BadRequest, await Publish(client, "npm-validation", grant.Write, length));
        Assert.Equal(HttpStatusCode.BadRequest, await Publish(client, "npm-validation", grant.Write,
            Document(Archive(name: "different-package"))));
        Assert.Equal(HttpStatusCode.BadRequest, await Publish(client, "npm-validation", grant.Write,
            Document(Archive(badPath: "package/../../outside"))));
        Assert.Equal(HttpStatusCode.BadRequest, await Publish(client, "npm-validation", grant.Write,
            Document(Encoding.UTF8.GetBytes("not a gzip tarball"))));
        var invalidTag = Document(tarball);
        invalidTag["dist-tags"]!["latest"] = "2.0.0";
        Assert.Equal(HttpStatusCode.BadRequest, await Publish(client, "npm-validation", grant.Write, invalidTag));
        var external = Document(tarball);
        external["versions"]!["1.0.0"]!["dependencies"] = new JsonObject
        {
            ["outside"] = "https://example.invalid/must-not-be-installed.tgz"
        };
        Assert.Equal(HttpStatusCode.BadRequest, await Publish(client, "npm-validation", grant.Write, external));
        using (var duplicate = Request(HttpMethod.Put, Url("npm-validation"), grant.Write))
        {
            duplicate.Content = new StringContent("{\"name\":\"a\",\"name\":\"b\"}", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(duplicate);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Empty(host.Services.GetRequiredService<IPackageDatabase>().FeedPackageAssets("npm-validation", "npm"));
        Assert.Empty(host.Services.GetRequiredService<IPackageDatabase>().PendingPublications());
    }

    [Fact]
    public async Task MetadataAndTarballBothFailClosedAfterBundleTampering()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var grant = await Create(client, "npm-corrupted");
        Assert.Equal(HttpStatusCode.Created, await Publish(client, "npm-corrupted", grant.Write, Document(Archive())));
        var asset = Assert.Single(host.Services.GetRequiredService<IPackageDatabase>().FeedPackageAssets("npm-corrupted", "npm"));
        var path = Path.Combine(host.Root, "blobs", FeedAssetEntry.BlobKey(asset));
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[bytes.Length / 2] ^= 0xff;
        await File.WriteAllBytesAsync(path, bytes);
        foreach (var suffix in new[] { "", "/1.0.0", "/-/1.0.0.tgz" })
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await Status(client, HttpMethod.Get,
                Url("npm-corrupted") + suffix, grant.Read));
    }

    [Fact]
    public async Task VersionMergingRetainsIndependentTagsAndConcurrentPublishHasOneWinner()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var grant = await Create(client, "npm-multiple");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Publish(client,
            "npm-multiple", grant.Write, Document(Archive()))));
        Assert.Single(results, x => x == HttpStatusCode.Created);
        Assert.Equal(7, results.Count(x => x == HttpStatusCode.Conflict));
        Assert.Equal(HttpStatusCode.Created, await Publish(client, "npm-multiple", grant.Write,
            Document(Archive(version: "2.0.0-beta.1"), version: "2.0.0-beta.1", tag: "next")));
        using var request = Request(HttpMethod.Get, Url("npm-multiple"), grant.Read);
        using var response = await client.SendAsync(request);
        var packument = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, packument.GetProperty("versions").EnumerateObject().Count());
        Assert.Equal("1.0.0", packument.GetProperty("dist-tags").GetProperty("latest").GetString());
        Assert.Equal("2.0.0-beta.1", packument.GetProperty("dist-tags").GetProperty("next").GetString());
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("1.0.0-beta.1", true)]
    [InlineData("1.0.0-beta.1+build.123", false)]
    [InlineData("latest", false)]
    [InlineData("v1.0.0", false)]
    [InlineData("01.0.0", false)]
    [InlineData("1.0.0-01", false)]
    [InlineData("1.0.0/../outside", false)]
    [InlineData("1.0.0\n", false)]
    [InlineData("9007199254740992.0.0", false)]
    public void NpmVersionsHaveCanonicalNativeSemver(string version, bool expected) =>
        Assert.Equal(expected, FeedNpmEndpoints.ValidVersion(version));

    [Fact]
    public async Task NativeNpmCleanVersionPreservesOriginalArchiveBytesWithoutAliasingBuildRevisions()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var grant = await Create(client, "npm-canonical");
        var bytes = Archive(version: "1.0.0+build.1");
        Assert.Equal(HttpStatusCode.Created, await Publish(client, "npm-canonical", grant.Write, Document(bytes)));
        Assert.Equal(HttpStatusCode.Conflict, await Publish(client, "npm-canonical", grant.Write,
            Document(Archive(version: "1.0.0+build.2"))));
        using var request = Request(HttpMethod.Get, Url("npm-canonical") + "/-/1.0.0.tgz", grant.Read);
        using var response = await client.SendAsync(request);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task PrivateDistTagsCanMoveAndDeleteWithoutChangingImmutableVersions()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var alpha = await Create(client, "npm-tags-alpha");
        var beta = await Create(client, "npm-tags-beta");
        Assert.Equal(HttpStatusCode.Created, await Publish(client, "npm-tags-alpha",
            alpha.Write, Document(Archive())));
        Assert.Equal(HttpStatusCode.Created, await Publish(client, "npm-tags-alpha",
            alpha.Write, Document(Archive(version: "2.0.0"), version: "2.0.0", tag: "next")));
        Assert.Equal(HttpStatusCode.Created, await Publish(client, "npm-tags-beta",
            beta.Write, Document(Archive(flavor: "beta"))));

        var path = "/feeds/npm-tags-alpha/npm/-/package/" +
            Uri.EscapeDataString(Name) + "/dist-tags";
        using (var r = Request(HttpMethod.Put, path + "/latest", beta.Write))
        {
            r.Content = JsonContent.Create("2.0.0");
            using var response = await client.SendAsync(r);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using (var r = Request(HttpMethod.Put, path + "/latest", alpha.Read))
        {
            r.Content = JsonContent.Create("2.0.0");
            using var response = await client.SendAsync(r);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using (var r = Request(HttpMethod.Put, path + "/latest", alpha.Write))
        {
            r.Content = JsonContent.Create("8.0.0");
            using var response = await client.SendAsync(r);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using (var r = Request(HttpMethod.Put, path + "/latest", alpha.Write))
        {
            r.Content = JsonContent.Create("2.0.0");
            using var response = await client.SendAsync(r);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        using (var r = Request(HttpMethod.Get, path, alpha.Read))
        using (var response = await client.SendAsync(r))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var tags = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("2.0.0", tags.GetProperty("latest").GetString());
            Assert.Equal("2.0.0", tags.GetProperty("next").GetString());
        }
        using (var r = Request(HttpMethod.Get, Url("npm-tags-alpha"), alpha.Read))
        using (var response = await client.SendAsync(r))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var packument = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("2.0.0", packument.GetProperty("dist-tags").GetProperty("latest").GetString());
            Assert.Equal(2, packument.GetProperty("versions").EnumerateObject().Count());
        }
        Assert.Equal(HttpStatusCode.OK,
            await Status(client, HttpMethod.Get, Url("npm-tags-alpha") + "/latest", alpha.Read));
        Assert.Equal(HttpStatusCode.Forbidden,
            await Status(client, HttpMethod.Get, path, beta.Read));
        Assert.Equal(HttpStatusCode.Unauthorized,
            await Status(client, HttpMethod.Get, path, null));

        Assert.Equal(HttpStatusCode.NoContent,
            await Status(client, HttpMethod.Delete, path + "/latest", alpha.Write));
        using (var r = Request(HttpMethod.Get, Url("npm-tags-alpha"), alpha.Read))
        using (var response = await client.SendAsync(r))
        {
            var packument = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.False(packument.GetProperty("dist-tags").TryGetProperty("latest", out _));
            Assert.Equal(2, packument.GetProperty("versions").EnumerateObject().Count());
        }
        Assert.Equal(HttpStatusCode.NotFound,
            await Status(client, HttpMethod.Get, Url("npm-tags-alpha") + "/latest", alpha.Read));
        Assert.Equal(HttpStatusCode.OK,
            await Status(client, HttpMethod.Get, Url("npm-tags-alpha") + "/1.0.0", alpha.Read));

        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var pointer = Assert.Single(db.FeedNpmTags("npm-tags-alpha", Name));
        Assert.Equal("latest", pointer.Tag);
        Assert.True(pointer.Deleted);
        Assert.Empty(db.FeedNpmTags("npm-tags-beta", Name));
        Assert.Equal(2, db.AuditEvents(null, 100).Count(e =>
            e.Kind == "feed-npm" && (e.Action == "tag-set" || e.Action == "tag-delete")));
    }
}
