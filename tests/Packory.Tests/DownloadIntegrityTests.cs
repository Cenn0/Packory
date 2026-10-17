using System.Buffers.Binary;
using System.IO.Compression;
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

public sealed class DownloadIntegrityTests
{
    private sealed class Host : WebApplicationFactory<Program>
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "packory-download-integrity-" + Guid.NewGuid().ToString("N"));
        public string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Settings>();
            services.AddSingleton(new Settings { DataDirectory = Root, PublicBaseUrl = "http://localhost:8080",
                AdminToken = Token, AnonymousRead = false });
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

    private static async Task<(string Url, string Key, byte[] Bytes)> Publish(HttpClient client, string kind)
    {
        if (kind == "cargo")
        {
            using var archive = new MemoryStream();
            using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
                await gzip.WriteAsync(Encoding.UTF8.GetBytes("synthetic integrity fixture"));
            var bytes = archive.ToArray();
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new { name = "demo", vers = "1.0.0",
                deps = Array.Empty<object>(), description = "Synthetic integrity fixture" });
            var frame = new byte[metadata.Length + bytes.Length + 8];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)metadata.Length);
            metadata.CopyTo(frame, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(metadata.Length + 4), (uint)bytes.Length);
            bytes.CopyTo(frame, metadata.Length + 8);
            using var upload = await client.PutAsync("/cargo/api/v1/crates/new", new ByteArrayContent(frame));
            Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
            return ("/cargo/api/v1/crates/demo/1.0.0/download", CargoEndpoints.Key("demo", "1.0.0"), bytes);
        }
        var pythonBytes = Encoding.UTF8.GetBytes("synthetic Python artifact integrity fixture");
        const string filename = "demo-1.0.0-py3-none-any.whl";
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("demo"), "name");
        form.Add(new StringContent("1.0.0"), "version");
        form.Add(new ByteArrayContent(pythonBytes), "content", filename);
        using var published = await client.PostAsync("/pypi/legacy/", form);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        return ("/pypi/files/demo/" + filename, PyPiEndpoints.Key("demo", filename), pythonBytes);
    }

    [Theory]
    [InlineData("cargo", "alter")]
    [InlineData("cargo", "truncate")]
    [InlineData("cargo", "extend")]
    [InlineData("pypi", "alter")]
    [InlineData("pypi", "truncate")]
    [InlineData("pypi", "extend")]
    public async Task CorruptedPublishedContentIsNeverReturnedEvenForRangeRequests(string kind, string mutation)
    {
        using var host = new Host();
        using var client = host.Client();
        var artifact = await Publish(client, kind);
        using var original = await client.GetAsync(artifact.Url);
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        Assert.Equal(artifact.Bytes, await original.Content.ReadAsByteArrayAsync());
        var corrupted = artifact.Bytes.ToArray();
        if (mutation == "alter") corrupted[corrupted.Length / 2] ^= 0xff;
        if (mutation == "truncate") corrupted = corrupted[..2];
        if (mutation == "extend") corrupted = corrupted.Concat(new byte[] { 1, 2, 3 }).ToArray();
        var path = Path.Combine(host.Root, "blobs", artifact.Key.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllBytesAsync(path, corrupted);
        using var denied = await client.GetAsync(artifact.Url);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        Assert.Null(denied.Headers.ETag);
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, artifact.Url);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 1);
        using var range = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, range.StatusCode);
        Assert.Null(range.Content.Headers.ContentRange);
        using var anonymous = host.CreateClient();
        using var unauthorized = await anonymous.GetAsync(artifact.Url);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(corrupted, await File.ReadAllBytesAsync(path)); // Evidence is not silently repaired/deleted.
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"name\":\"demo\",\"vers\":\"1.0.0\",\"cksum\":42}")]
    public async Task CargoWithoutAnAuthoritativeDigestCannotBeDownloaded(string index)
    {
        using var host = new Host();
        using var client = host.Client();
        var database = host.Services.GetRequiredService<IPackageDatabase>();
        var blobs = host.Services.GetRequiredService<BlobStore>();
        Assert.True(await blobs.PutAsync(CargoEndpoints.Key("demo", "1.0.0"),
            new MemoryStream(new byte[] { 1, 2, 3 }), 1024, CancellationToken.None));
        Assert.True(database.PublishCargo("demo", "1.0.0", index, "fixture", "admin-token"));
        using var response = await client.GetAsync("/cargo/api/v1/crates/demo/1.0.0/download");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
