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

public sealed class RegistryReviewRegressionTests
{
    private const string Token = "synthetic-review-administrator-token";
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(reply(request));
    }
    private sealed class Host(Func<HttpRequestMessage, HttpResponseMessage> reply,
        int metadataLimit = 1024, TimeSpan? timeout = null) : WebApplicationFactory<Program>
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "packory-review-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Settings>();
            services.AddSingleton(new Settings { DataDirectory = Root, AdminToken = Token,
                AnonymousRead = false, MaxMetadataBytes = metadataLimit });
            services.DisableBackgroundSkillScanning();
            services.AddHttpClient("upstream", client => client.Timeout = timeout ?? TimeSpan.FromSeconds(90))
                .ConfigurePrimaryHttpMessageHandler(() => new Handler(reply));
        });
        public HttpClient Authenticated()
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

    private sealed class CountedStream(int size, bool stall = false) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, size - BytesRead);
            Array.Fill(buffer, (byte)' ', offset, read);
            BytesRead += read;
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (stall) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            var read = Math.Min(buffer.Length, size - BytesRead);
            buffer.Span[..read].Fill((byte)' ');
            BytesRead += read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task NugetVersionIndexStopsReadingAtMetadataBudget()
    {
        using var source = new CountedStream(2 * 1024 * 1024);
        using var host = new Host(_ => new(HttpStatusCode.OK) { Content = new StreamContent(source) });
        using var client = host.Authenticated();
        using var response = await client.GetAsync("/nuget/v3/flatcontainer/demo/index.json");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.InRange(source.BytesRead, 1025, 81920);
    }

    [Theory]
    [InlineData("sha256-unsupported")]
    [InlineData("broken")]
    [InlineData("")]
    public async Task NpmColdDownloadCannotDowngradeIntegrityToShasum(string integrity)
    {
        byte[] bytes = [0x1f, 0x8b, 8, 0, 0, 0, 0, 0];
        var metadata = JsonSerializer.Serialize(new { name = "demo", versions = new Dictionary<string, object>
        {
            ["1.0.0"] = new { name = "demo", version = "1.0.0", dist = new {
                tarball = "https://registry.npmjs.org/demo/-/demo-1.0.0.tgz", integrity,
                shasum = Convert.ToHexStringLower(SHA1.HashData(bytes)) } }
        }});
        var downloads = 0;
        using var host = new Host(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal))
            { downloads++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; }
            return new(HttpStatusCode.OK) { Content = new StringContent(metadata) };
        });
        using var client = host.Authenticated();
        using var response = await client.GetAsync("/npm/demo/-/1.0.0.tgz");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(0, downloads);
        Assert.False(host.Services.GetRequiredService<BlobStore>().Contains(NpmEndpoints.NpmKey("demo", "1.0.0")));
    }

    [Theory]
    [InlineData("/nuget/v3/flatcontainer/demo/index.json")]
    [InlineData("/nuget/v3/registration/demo/index.json")]
    [InlineData("/nuget/v3/flatcontainer/demo/1.0.0/demo.1.0.0.nupkg")]
    [InlineData("/npm/demo")]
    public async Task UpstreamDeadlineIncludesBodyAfterHeaders(string path)
    {
        using var source = new CountedStream(1024, stall: true);
        using var host = new Host(_ => new(HttpStatusCode.OK) { Content = new StreamContent(source) },
            timeout: TimeSpan.FromMilliseconds(200));
        using var client = host.Authenticated();
        using var response = await client.GetAsync(path).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task NullApprovalDigestIsBadRequest()
    {
        using var host = new Host(_ => new(HttpStatusCode.NotFound));
        using var client = host.Authenticated();
        using var response = await client.PostAsync("/api/security/skills/demo/1.0.0/approve",
            new StringContent("{\"sha256\":null,\"reason\":\"synthetic review\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AdmittedSeventeenMiBSkillRemainsReadableThroughMcp()
    {
        using var host = new Host(_ => new(HttpStatusCode.NotFound));
        using var client = host.Authenticated();
        const string name = "large-skill";
        using var zipBytes = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(zipBytes, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("SKILL.md").Open()))
                writer.Write("---\nname: large-skill\ndescription: Synthetic large skill\n---\n# Test\n");
            using var data = zip.CreateEntry("assets/example.bin").Open();
            data.Write(new byte[17 * 1024 * 1024]);
        }
        var bytes = zipBytes.ToArray();
        Assert.Equal(name, SkillEndpoints.InspectZip(bytes).Name);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var blobs = host.Services.GetRequiredService<BlobStore>();
        var security = host.Services.GetRequiredService<SkillSecurityStore>();
        Assert.True(await blobs.PutAsync(SkillEndpoints.SkillKey(name, "1.0.0"), new MemoryStream(bytes),
            20 * 1024 * 1024, CancellationToken.None));
        Assert.True(security.TryStage(name, "1.0.0", "Synthetic", digest, "skill-staging/" + digest + ".zip"));
        Assert.True(security.SaveResult(name, "1.0.0", digest, "NEEDS_REVIEW", 0, "SAFE", "{}"));
        Assert.True(security.TryTransition(name, "1.0.0", digest, "NEEDS_REVIEW", "APPROVED", "synthetic-reviewer", "Synthetic fixture"));
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        Assert.True(db.AddSkill(name, "1.0.0", "Synthetic", "synthetic-reviewer"));
        var snapshot = PackoryMcpCatalog.Get(name, "1.0.0", db, blobs, security, host.Services.GetRequiredService<Settings>());
        Assert.NotNull(snapshot);
        Assert.Equal(17 * 1024 * 1024, Assert.Single(snapshot.Files, f => f.Path == "assets/example.bin").Bytes.Length);
    }

    [Theory]
    [InlineData("/nuget/v3/flatcontainer/demo/index.json")]
    [InlineData("/npm/demo")]
    public async Task MirrorGetsFailClosedWhenWriterLeaseIsUnavailable(string path)
    {
        var upstreamCalls = 0;
        using var host = new Host(_ => { upstreamCalls++; return new(HttpStatusCode.NotFound); });
        using var client = host.Authenticated();
        var settings = host.Services.GetRequiredService<Settings>();
        // Select an unacquired PostgreSQL lease after isolated SQLite startup.
        // This exercises the real HTTP gate without pretending to partition a DB.
        settings.MetadataProvider = "postgresql";
        try
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(0, upstreamCalls);
        }
        finally { settings.MetadataProvider = "sqlite"; }
    }
}
