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

public sealed class FeedOciTests
{
    private const string Admin = "synthetic-private-oci-admin-token-long-enough";
    private const string GlobalWriter = "synthetic-private-oci-global-writer-long-enough";
    private const string Repository = "team/image";
    private const string Alpha = "alpha-oci";
    private const string Beta = "beta-oci";

    private sealed class Host(string root, bool anonymous = true) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Settings>();
            var cfg = new Settings
            {
                DataDirectory = root, PublicBaseUrl = "http://localhost:8080", AdminToken = Admin,
                WriteToken = GlobalWriter, AnonymousRead = anonymous, McpEnabled = false,
                MaxArtifactBytes = 65536, MaxMetadataBytes = 16384
            };
            cfg.Validate();
            services.AddSingleton(cfg);
        });
    }

    private static string Route(string feed, string suffix, string repo = Repository) =>
        "/v2/feeds/" + feed + "/" + repo + suffix;
    private static string Digest(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path,
        string? token = Admin, byte[]? body = null, string? media = null, string? range = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(media ?? "application/octet-stream");
            if (range is not null) request.Content.Headers.TryAddWithoutValidation("Content-Range", range);
        }
        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Json(HttpClient client, string path, object value, string token = Admin) =>
        Send(client, HttpMethod.Post, path, token, JsonSerializer.SerializeToUtf8Bytes(value), "application/json");

    private static async Task Create(HttpClient client, string feed)
    {
        using var draft = await Json(client, "/api/v1/admin/feeds/drafts", new
        { id = feed, kind = "oci", mode = "hosted", displayName = "Synthetic OCI" });
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        using var active = await Send(client, HttpMethod.Post, "/api/v1/admin/feeds/" + feed + "/activate");
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
    }

    private static async Task<string> Token(HttpClient client, string feed, bool write)
    {
        using var response = await Json(client, "/api/admin/tokens", new
        { role = write ? "write" : "read", @namespace = "feed:" + feed + (write ? ":publish" : ":read"), expiresInHours = 1 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("token").GetString()!;
    }

    private static async Task Upload(HttpClient client, string feed, byte[] bytes, string token = Admin, string repo = Repository)
    {
        using var response = await Send(client, HttpMethod.Post, Route(feed, "/blobs/uploads/?digest=" + Digest(bytes), repo), token, bytes);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(Digest(bytes), response.Headers.GetValues("Docker-Content-Digest").Single());
        Assert.Equal(Route(feed, "/blobs/" + Digest(bytes), repo), response.Headers.Location!.AbsolutePath);
    }

    private static byte[] Manifest(byte[] config, string label, object? subject = null) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 2, mediaType = FeedOciEndpoints.ManifestMedia,
        config = new { mediaType = "application/vnd.oci.image.config.v1+json", digest = Digest(config), size = config.Length },
        layers = Array.Empty<object>(), annotations = new Dictionary<string, string> { ["org.example.synthetic"] = label },
        subject
    }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

    [Fact]
    public async Task SameRepositoryTagInTwoFeedsHasIndependentBytesAndExactGrants()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-feed-oci-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var host = new Host(root);
            using var client = host.CreateClient();
            await Create(client, Alpha);
            await Create(client, Beta);
            var aWrite = await Token(client, Alpha, true);
            var aRead = await Token(client, Alpha, false);
            var bWrite = await Token(client, Beta, true);
            var bRead = await Token(client, Beta, false);
            var aConfig = Encoding.UTF8.GetBytes("{\"synthetic\":\"alpha\"}");
            var bConfig = Encoding.UTF8.GetBytes("{\"synthetic\":\"beta\"}");
            await Upload(client, Alpha, aConfig, aWrite);
            await Upload(client, Beta, bConfig, bWrite);
            var aManifest = Manifest(aConfig, "alpha");
            var bManifest = Manifest(bConfig, "beta");
            using (var put = await Send(client, HttpMethod.Put, Route(Alpha, "/manifests/1.0.0"), aWrite, aManifest, FeedOciEndpoints.ManifestMedia))
                Assert.Equal(HttpStatusCode.Created, put.StatusCode);
            using (var put = await Send(client, HttpMethod.Put, Route(Beta, "/manifests/1.0.0"), bWrite, bManifest, FeedOciEndpoints.ManifestMedia))
                Assert.Equal(HttpStatusCode.Created, put.StatusCode);
            foreach (var (feed, token, bytes) in new[] { (Alpha, aRead, aManifest), (Beta, bRead, bManifest) })
            {
                using var read = await Send(client, HttpMethod.Get, Route(feed, "/manifests/1.0.0"), token);
                Assert.Equal(HttpStatusCode.OK, read.StatusCode);
                Assert.Equal(bytes, await read.Content.ReadAsByteArrayAsync());
                using var head = await Send(client, HttpMethod.Head, Route(feed, "/manifests/" + Digest(bytes)), token);
                Assert.Equal(HttpStatusCode.OK, head.StatusCode);
                Assert.Equal(bytes.Length, head.Content.Headers.ContentLength);
            }
            foreach (var (token, expected) in new (string?, HttpStatusCode)[]
            { (null, HttpStatusCode.Unauthorized), (bRead, HttpStatusCode.Forbidden), (GlobalWriter, HttpStatusCode.Forbidden) })
            {
                using var denied = await Send(client, HttpMethod.Get, Route(Alpha, "/manifests/1.0.0"), token);
                Assert.Equal(expected, denied.StatusCode);
            }
            using (var denied = await Send(client, HttpMethod.Put, Route(Alpha, "/manifests/new"), aRead, aManifest, FeedOciEndpoints.ManifestMedia))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var global = await Send(client, HttpMethod.Get, "/v2/" + Repository + "/manifests/1.0.0"))
                Assert.Equal(HttpStatusCode.NotFound, global.StatusCode);
            using (var absent = await Send(client, HttpMethod.Get, Route(Alpha, "/blobs/" + Digest(bConfig)), aRead))
                Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
            using (var tags = await Send(client, HttpMethod.Get, Route(Alpha, "/tags/list"), aRead))
            {
                var json = await tags.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("feeds/" + Alpha + "/" + Repository, json.GetProperty("name").GetString());
                Assert.Equal("1.0.0", json.GetProperty("tags")[0].GetString());
            }
            using (var off = await Send(client, HttpMethod.Post, "/api/v1/admin/feeds/" + Alpha + "/deactivate"))
                Assert.Equal(HttpStatusCode.OK, off.StatusCode);
            using (var denied = await Send(client, HttpMethod.Get, Route(Alpha, "/manifests/1.0.0"), aRead))
                Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            using (var other = await Send(client, HttpMethod.Get, Route(Beta, "/manifests/1.0.0"), bRead))
                Assert.Equal(HttpStatusCode.OK, other.StatusCode);
            var db = host.Services.GetRequiredService<IPackageDatabase>();
            Assert.Equal(4, db.FeedAssets().Count(a => a.Kind == "oci"));
            Assert.Empty(db.OciAllBlobDigests());
            Assert.Empty(db.OciAllManifestDigests());
            Assert.Empty(db.PendingPublications());
            Assert.Equal(0, host.Services.GetRequiredService<ArtifactRecovery>().MissingHostedCount());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ChunkSessionSurvivesRestartWithoutCrossFeedOrGlobalSessionAccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-feed-oci-" + Guid.NewGuid().ToString("N"));
        var bytes = Encoding.UTF8.GetBytes("synthetic-chunked-content");
        string location;
        try
        {
            using (var first = new Host(root))
            using (var client = first.CreateClient())
            {
                await Create(client, Alpha);
                await Create(client, Beta);
                using var start = await Send(client, HttpMethod.Post, Route(Alpha, "/blobs/uploads/"), body: []);
                Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
                location = start.Headers.Location!.PathAndQuery;
                using var patch = await Send(client, HttpMethod.Patch, location, body: bytes[..10], range: "0-9");
                Assert.Equal(HttpStatusCode.Accepted, patch.StatusCode);
                Assert.Equal("0-9", patch.Headers.GetValues("Range").Single());
                using var invalid = await Send(client, HttpMethod.Patch, location, body: [1, 2], range: "0-1");
                Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalid.StatusCode);
                using var wrong = await Send(client, HttpMethod.Get, location.Replace(Alpha, Beta, StringComparison.Ordinal));
                Assert.Equal(HttpStatusCode.NotFound, wrong.StatusCode);
                var id = location[(location.LastIndexOf('/') + 1)..];
                var globalSessions = first.Services.GetRequiredService<OciUploadStore>();
                Assert.False(globalSessions.TryGet("feeds/" + Alpha + "/" + Repository, id, out _));
            }
            using (var restarted = new Host(root))
            using (var client = restarted.CreateClient())
            {
                using var status = await Send(client, HttpMethod.Get, location);
                Assert.Equal(HttpStatusCode.NoContent, status.StatusCode);
                Assert.Equal("0-9", status.Headers.GetValues("Range").Single());
                using var finish = await Send(client, HttpMethod.Put, location + "?digest=" + Digest(bytes), body: bytes[10..]);
                Assert.Equal(HttpStatusCode.Created, finish.StatusCode);
                using var download = await Send(client, HttpMethod.Get, Route(Alpha, "/blobs/" + Digest(bytes)));
                Assert.Equal(bytes, await download.Content.ReadAsByteArrayAsync());
                using var complete = await Send(client, HttpMethod.Get, location);
                Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);
                Assert.Empty(restarted.Services.GetRequiredService<IPackageDatabase>().PendingPublications());
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ManifestDescriptorsCannotBorrowForeignBlobsAndCorruptionFailsClosed()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-feed-oci-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var host = new Host(root);
            using var client = host.CreateClient();
            await Create(client, Alpha);
            await Create(client, Beta);
            var config = Encoding.UTF8.GetBytes("{}");
            await Upload(client, Alpha, config);
            var manifest = Manifest(config, "synthetic");
            using (var foreign = await Send(client, HttpMethod.Put, Route(Beta, "/manifests/1.0.0"), body: manifest, media: FeedOciEndpoints.ManifestMedia))
                Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
            using (var repo = await Send(client, HttpMethod.Put, Route(Alpha, "/manifests/1.0.0", "other/image"), body: manifest, media: FeedOciEndpoints.ManifestMedia))
                Assert.Equal(HttpStatusCode.BadRequest, repo.StatusCode);
            using (var wrongSize = await Send(client, HttpMethod.Put, Route(Alpha, "/manifests/1.0.0"),
                body: Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace("\"size\":2", "\"size\":3", StringComparison.Ordinal)), media: FeedOciEndpoints.ManifestMedia))
                Assert.Equal(HttpStatusCode.BadRequest, wrongSize.StatusCode);
            using (var malformed = await Send(client, HttpMethod.Put, Route(Alpha, "/manifests/1.0.0"),
                body: Encoding.UTF8.GetBytes("{\"schemaVersion\":\"2\"}"), media: FeedOciEndpoints.ManifestMedia))
                Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
            using (var mismatch = await Send(client, HttpMethod.Put, Route(Alpha, "/manifests/" + Digest(config)),
                body: manifest, media: FeedOciEndpoints.ManifestMedia))
                Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
            using (var put = await Send(client, HttpMethod.Put, Route(Alpha, "/manifests/1.0.0"), body: manifest, media: FeedOciEndpoints.ManifestMedia))
                Assert.Equal(HttpStatusCode.Created, put.StatusCode);
            var db = host.Services.GetRequiredService<IPackageDatabase>();
            var blobs = host.Services.GetRequiredService<BlobStore>();
            var asset = db.GetFeedAsset(Alpha, "oci", FeedOciEndpoints.AssetPath(Repository, Digest(config), false))!;
            var path = blobs.Existing(FeedOciEndpoints.BlobKey(asset))!;
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("[]"));
            using (var tampered = await Send(client, HttpMethod.Get, Route(Alpha, "/blobs/" + Digest(config))))
                Assert.Equal(HttpStatusCode.ServiceUnavailable, tampered.StatusCode);
            using (var cannotOverwrite = await Send(client, HttpMethod.Post, Route(Alpha, "/blobs/uploads/?digest=" + Digest(config)), body: config))
                Assert.Equal(HttpStatusCode.ServiceUnavailable, cannotOverwrite.StatusCode);
            Assert.Equal("[]", await File.ReadAllTextAsync(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task EmptyBlobsAndMutableTagsRemainDigestBoundAndReferrersAreFeedLocal()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-feed-oci-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var host = new Host(root, anonymous: false);
            using var client = host.CreateClient();
            await Create(client, Alpha);
            await Create(client, Beta);
            var token = await Token(client, Alpha, false);
            using (var ping = await Send(client, HttpMethod.Get, "/v2/", token))
                Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
            await Upload(client, Alpha, []);
            var missingSubject = new { mediaType = FeedOciEndpoints.ManifestMedia, digest = Digest([1, 2, 3]), size = 100 };
            var old = Manifest([], "old", missingSubject);
            var updated = Manifest([], "new", missingSubject);
            foreach (var bytes in new[] { old, updated })
            {
                using var put = await Send(client, HttpMethod.Put, Route(Alpha, "/manifests/latest"), body: bytes, media: FeedOciEndpoints.ManifestMedia);
                Assert.Equal(HttpStatusCode.Created, put.StatusCode);
                Assert.Equal(missingSubject.digest, put.Headers.GetValues("OCI-Subject").Single());
            }
            using (var current = await Send(client, HttpMethod.Get, Route(Alpha, "/manifests/latest"), token))
                Assert.Equal(updated, await current.Content.ReadAsByteArrayAsync());
            using (var immutable = await Send(client, HttpMethod.Get, Route(Alpha, "/manifests/" + Digest(old)), token))
                Assert.Equal(old, await immutable.Content.ReadAsByteArrayAsync());
            using (var refs = await Send(client, HttpMethod.Get, Route(Alpha, "/referrers/" + missingSubject.digest), token))
            {
                var json = await refs.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(2, json.GetProperty("manifests").GetArrayLength());
            }
            using (var refs = await Send(client, HttpMethod.Get, Route(Beta, "/referrers/" + missingSubject.digest)))
            {
                var json = await refs.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(0, json.GetProperty("manifests").GetArrayLength());
            }
            // Mount returns the same upload fallback regardless of source content.
            using var mount = await Send(client, HttpMethod.Post,
                Route(Beta, "/blobs/uploads/?mount=" + Digest([]) + "&from=feeds/" + Alpha + "/" + Repository), body: []);
            Assert.Equal(HttpStatusCode.Accepted, mount.StatusCode);
            using var absent = await Send(client, HttpMethod.Get, Route(Beta, "/blobs/" + Digest([])));
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("config")]
    [InlineData("layers")]
    [InlineData("blobs")]
    [InlineData("manifests")]
    [InlineData("subject")]
    public async Task AlternateDescriptorUrlsCannotBePublishedEvenWhenContentExistsInTheFeed(string field)
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-feed-oci-origin-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var host = new Host(root);
            using var client = host.CreateClient();
            await Create(client, Alpha);
            var token = await Token(client, Alpha, true);
            var config = "{}"u8.ToArray();
            await Upload(client, Alpha, config, token);
            var original = Manifest(config, "synthetic-origin");
            using (var put = await Send(client, HttpMethod.Put, Route(Alpha, "/manifests/original"), token,
                original, FeedOciEndpoints.ManifestMedia)) Assert.Equal(HttpStatusCode.Created, put.StatusCode);
            var manifestReference = field is "manifests" or "subject";
            var referenced = manifestReference ? original : config;
            var descriptor = new JsonObject
            {
                ["mediaType"] = manifestReference ? FeedOciEndpoints.ManifestMedia : "application/octet-stream",
                ["digest"] = Digest(referenced), ["size"] = referenced.Length,
                ["urls"] = new JsonArray("https://outside.example.test/synthetic-content")
            };
            var document = JsonNode.Parse(original)!.AsObject();
            var media = FeedOciEndpoints.ManifestMedia;
            if (field is "manifests" or "blobs")
            {
                media = field == "manifests" ? FeedOciEndpoints.IndexMedia : "application/vnd.oci.artifact.manifest.v1+json";
                document = new JsonObject { ["schemaVersion"] = 2, ["mediaType"] = media, [field] = new JsonArray(descriptor) };
            }
            else document[field] = field == "layers" ? new JsonArray(descriptor) : descriptor;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            using (var rejected = await Send(client, HttpMethod.Put, Route(Alpha, "/manifests/external"), token, bytes, media))
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using (var missing = await Send(client, HttpMethod.Get, Route(Alpha, "/manifests/external"), token))
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var db = host.Services.GetRequiredService<IPackageDatabase>();
            Assert.Null(db.GetFeedAsset(Alpha, "oci", FeedOciEndpoints.AssetPath(Repository, Digest(bytes), true)));
            Assert.Equal(2, db.FeedAssets().Count);
            Assert.Empty(db.PendingPublications());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InlineDescriptorDataMustMatchCanonicalDigestAndSizeWithoutReplacingTheFeedLedger()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-feed-oci-inline-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var host = new Host(root);
            using var client = host.CreateClient();
            await Create(client, Alpha);
            await Create(client, Beta);
            var config = "{}"u8.ToArray();
            await Upload(client, Alpha, config);
            var document = JsonNode.Parse(Manifest(config, "synthetic-inline"))!.AsObject();
            document["config"]!["data"] = Convert.ToBase64String(config);
            var valid = JsonSerializer.SerializeToUtf8Bytes(document);
            using (var put = await Send(client, HttpMethod.Put, Route(Alpha, "/manifests/inline"), body: valid, media: FeedOciEndpoints.ManifestMedia))
                Assert.Equal(HttpStatusCode.Created, put.StatusCode);
            using (var get = await Send(client, HttpMethod.Get, Route(Alpha, "/manifests/inline")))
                Assert.Equal(valid, await get.Content.ReadAsByteArrayAsync());
            using (var foreign = await Send(client, HttpMethod.Put, Route(Beta, "/manifests/inline"), body: valid, media: FeedOciEndpoints.ManifestMedia))
                Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
            using (var otherRepository = await Send(client, HttpMethod.Put, Route(Alpha, "/manifests/inline", "other/image"), body: valid, media: FeedOciEndpoints.ManifestMedia))
                Assert.Equal(HttpStatusCode.BadRequest, otherRepository.StatusCode);
            foreach (var data in new JsonNode?[]
            {
                null, JsonValue.Create(42), JsonValue.Create("not-base64"),
                JsonValue.Create(Convert.ToBase64String("[]"u8)),
                JsonValue.Create(Convert.ToBase64String("longer"u8)),
                JsonValue.Create("e30=\n"), JsonValue.Create("e31=")
            })
            {
                var candidate = document.DeepClone();
                candidate["config"]!["data"] = data;
                using var rejected = await Send(client, HttpMethod.Put, Route(Alpha, "/manifests/invalid-inline"),
                    body: JsonSerializer.SerializeToUtf8Bytes(candidate), media: FeedOciEndpoints.ManifestMedia);
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            }
            var db = host.Services.GetRequiredService<IPackageDatabase>();
            Assert.Null(db.GetFeedOciTag(Alpha, Repository, "invalid-inline"));
            Assert.Equal(2, db.FeedAssets().Count);
            Assert.Empty(db.PendingPublications());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("team/blobs/uploads/image")]
    [InlineData("team/manifests/image")]
    [InlineData("team/referrers/tags/list/image")]
    public async Task ProtocolWordsInsideRepositoryNameDoNotChangeAuthorizationTarget(string repository)
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-feed-oci-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var host = new Host(root);
            using var client = host.CreateClient();
            await Create(client, Alpha);
            var config = Encoding.UTF8.GetBytes("{}");
            await Upload(client, Alpha, config, repo: repository);
            var manifest = Manifest(config, "synthetic");
            using var put = await Send(client, HttpMethod.Put, Route(Alpha, "/manifests/latest", repository),
                body: manifest, media: FeedOciEndpoints.ManifestMedia);
            Assert.Equal(HttpStatusCode.Created, put.StatusCode);
            using var read = await Send(client, HttpMethod.Get, Route(Alpha, "/manifests/latest", repository));
            Assert.Equal(manifest, await read.Content.ReadAsByteArrayAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
