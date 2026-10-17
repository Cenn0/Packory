using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class OciProtocolHardeningTests
{
    private sealed class Host : WebApplicationFactory<Program>
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "packory-oci-http-" + Guid.NewGuid().ToString("N"));
        public string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Settings>();
            var cfg = new Settings { DataDirectory = Root, PublicBaseUrl = "http://localhost:8080",
                AdminToken = Token, AnonymousRead = false };
            cfg.Validate();
            services.AddSingleton(cfg);
        });
        public HttpClient Client()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            return client;
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static async Task<string> Start(HttpClient client, string repository = "team/demo")
    {
        using var result = await client.PostAsync("/v2/" + repository + "/blobs/uploads/", null);
        Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);
        return result.Headers.Location!.AbsolutePath;
    }
    private static Task<HttpResponseMessage> Patch(HttpClient client, string path, string text, string range)
    {
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(text));
        body.Headers.TryAddWithoutValidation("Content-Range", range);
        return client.PatchAsync(path, body);
    }

    [Fact]
    public async Task ChunkReplayReturns416WithCommittedRangeAndCanContinue()
    {
        using var host = new Host();
        using var client = host.Client();
        var upload = await Start(client);
        using var first = await Patch(client, upload, "abc", "0-2");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        using var replay = await Patch(client, upload, "abc", "0-2");
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, replay.StatusCode);
        Assert.Equal("0-2", replay.Headers.GetValues("Range").Single());
        Assert.Equal(upload, replay.Headers.Location!.AbsolutePath);
        using var status = await client.GetAsync(upload);
        Assert.Equal(HttpStatusCode.NoContent, status.StatusCode);
        Assert.Equal("0-2", status.Headers.GetValues("Range").Single());
        using var next = await Patch(client, upload, "def", "3-5");
        Assert.Equal(HttpStatusCode.Accepted, next.StatusCode);
        var bytes = Encoding.UTF8.GetBytes("abcdef");
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var finish = await client.PutAsync(upload + "?digest=" + digest, new ByteArrayContent([]));
        Assert.Equal(HttpStatusCode.Created, finish.StatusCode);
        using var read = await client.GetAsync(finish.Headers.Location!.AbsolutePath);
        Assert.Equal(bytes, await read.Content.ReadAsByteArrayAsync());
        using var gone = await client.GetAsync(upload);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task InvalidFinalChunkDoesNotCommitOrPublishAnything()
    {
        using var host = new Host();
        using var client = host.Client();
        var upload = await Start(client);
        using var first = await Patch(client, upload, "abc", "0-2");
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("abcdef")));
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes("def"));
        body.Headers.TryAddWithoutValidation("Content-Range", "2-4");
        using var invalid = await client.PutAsync(upload + "?digest=" + digest, body);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalid.StatusCode);
        using var missing = await client.GetAsync("/v2/team/demo/blobs/" + digest);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var status = await client.GetAsync(upload);
        Assert.Equal("0-2", status.Headers.GetValues("Range").Single());
    }

    [Fact]
    public async Task CancelAndCrossRepositorySessionAccessAreFailClosed()
    {
        using var host = new Host();
        using var client = host.Client();
        var upload = await Start(client);
        using var foreign = await Patch(client, upload.Replace("team/demo", "team/other"), "abc", "0-2");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var cancelled = await client.DeleteAsync(upload);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        using var late = await Patch(client, upload, "abc", "0-2");
        Assert.Equal(HttpStatusCode.NotFound, late.StatusCode);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(host.Root, "oci-staging")));
    }

    [Fact]
    public async Task CorruptFilesystemBlobIsNotServedOrMounted()
    {
        using var host = new Host();
        using var client = host.Client();
        var bytes = Encoding.UTF8.GetBytes("abc");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var digest = "sha256:" + hash;
        using var published = await client.PostAsync("/v2/team/demo/blobs/uploads/?digest=" + digest, new ByteArrayContent(bytes));
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        var path = Path.Combine(host.Root, "blobs", "oci", "blobs", "sha256", hash + ".blob");
        File.WriteAllText(path, "bad");
        using var read = await client.GetAsync("/v2/team/demo/blobs/" + digest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, read.StatusCode);
        Assert.DoesNotContain("bad", await read.Content.ReadAsStringAsync());
        using var mount = await client.PostAsync("/v2/team/other/blobs/uploads/?mount=" + digest + "&from=team/demo", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, mount.StatusCode);
        using var absent = await client.GetAsync("/v2/team/other/blobs/" + digest);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }

    [Fact]
    public async Task ReferrerFilterAndSubjectAcknowledgementMatchActualBehavior()
    {
        using var host = new Host();
        using var client = host.Client();
        var subject = "sha256:" + new string('a', 64);
        foreach (var kind in new[] { "one", "two" })
        {
            var document = JsonSerializer.Serialize(new { schemaVersion = 2,
                mediaType = "application/vnd.oci.image.index.v1+json", manifests = Array.Empty<object>(),
                artifactType = "application/vnd.test." + kind,
                subject = new { digest = subject, size = 1, mediaType = "application/vnd.oci.image.index.v1+json" } });
            using var created = await client.PutAsync("/v2/team/demo/manifests/" + kind,
                new StringContent(document, Encoding.UTF8, "application/vnd.oci.image.index.v1+json"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            Assert.Equal(subject, created.Headers.GetValues("OCI-Subject").Single());
        }
        using var all = await client.GetAsync("/v2/team/demo/referrers/" + subject);
        Assert.False(all.Headers.Contains("OCI-Filters-Applied"));
        using var selected = await client.GetAsync("/v2/team/demo/referrers/" + subject + "?artifactType=application%2Fvnd.test.one");
        Assert.Equal("artifactType", selected.Headers.GetValues("OCI-Filters-Applied").Single());
        using var json = JsonDocument.Parse(await selected.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("manifests").GetArrayLength());
        Assert.Equal("application/vnd.test.one", json.RootElement.GetProperty("manifests")[0].GetProperty("artifactType").GetString());
        using var zero = await client.GetAsync("/v2/team/demo/tags/list?n=0");
        Assert.Equal(HttpStatusCode.OK, zero.StatusCode);
        Assert.False(zero.Headers.Contains("Link"));
        using var page = JsonDocument.Parse(await zero.Content.ReadAsStringAsync());
        Assert.Equal(0, page.RootElement.GetProperty("tags").GetArrayLength());
    }

    [Theory]
    [InlineData("{\"schemaVersion\":\"2\",\"layers\":[]}")]
    [InlineData("{\"schemaVersion\":9999999999999999999,\"layers\":[]}")]
    [InlineData("{\"schemaVersion\":2,\"layers\":5}")]
    [InlineData("{\"schemaVersion\":2,\"subject\":{\"digest\":5}}")]
    public async Task MalformedManifestValuesReturn400InsteadOfUnhandledExceptions(string json)
    {
        using var host = new Host();
        using var client = host.Client();
        using var response = await client.PutAsync("/v2/team/demo/manifests/latest",
            new StringContent(json, Encoding.UTF8, "application/vnd.oci.image.manifest.v1+json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
