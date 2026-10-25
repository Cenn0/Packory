using System.Net;
using System.Net.Http.Headers;
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

public sealed class ModelCatalogTests
{
    private const string Admin = "synthetic-model-admin-token-at-least-32-chars";
    private const string Reader = "synthetic-model-reader-token-at-least-32-chars";
    private const string Writer = "synthetic-model-writer-token-at-least-32-chars";
    private const string Descriptor = """
        {"name":"example/demo","version":"1.0.0","format":"gguf",
         "sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
         "sizeBytes":1024,"license":"Apache-2.0","publisher":"Synthetic publisher",
         "sourceUrl":"https://models.example.org/download/demo.gguf",
         "provenanceUrl":"https://models.example.org/provenance/commit",
         "description":"Inert metadata-only model fixture"}
        """;

    private sealed class Host : WebApplicationFactory<Program>
    {
        private readonly string data = Path.Combine(Path.GetTempPath(), "packory-model-tests-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Packory:DataDirectory"] = data,
                ["Packory:PublicBaseUrl"] = "http://localhost:8080",
                ["Packory:AdminToken"] = Admin,
                ["Packory:ReadToken"] = Reader,
                ["Packory:WriteToken"] = Writer,
                ["Packory:AnonymousRead"] = "false",
                ["Packory:McpEnabled"] = "false"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                var settings = new Settings
                {
                    DataDirectory = data, PublicBaseUrl = "http://localhost:8080",
                    AdminToken = Admin, ReadToken = Reader, WriteToken = Writer,
                    AnonymousRead = false, McpEnabled = false
                };
                settings.Validate(); services.AddSingleton(settings);
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(data)) Directory.Delete(data, true);
        }
    }

    private static HttpRequestMessage Call(HttpMethod method, string path,
        string? token = null, string? json = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    [Fact]
    public void DescriptorRejectsUnsafeOrAmbiguousReferences()
    {
        var valid = ModelDescriptor.Parse(Encoding.UTF8.GetBytes(Descriptor));
        Assert.Equal("example/demo", valid.Name);
        foreach (var unsafeUrl in new[]
        {
            "http://models.example.org/download/demo.gguf",
            "https://127.0.0.1/download/demo.gguf",
            "https://localhost/download/demo.gguf",
            "https://host.internal/download/demo.gguf",
            "https://:@models.example.org/download/demo.gguf",
            "https://models.example.org/download/demo.gguf?token=synthetic"
        })
            Assert.Throws<InvalidDataException>(() => ModelDescriptor.Parse(Encoding.UTF8.GetBytes(
                Descriptor.Replace("https://models.example.org/download/demo.gguf", unsafeUrl, StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => ModelDescriptor.Parse(Encoding.UTF8.GetBytes(
            Descriptor.Replace("\"publisher\":\"Synthetic publisher\",",
                "\"publisher\":\"Synthetic publisher\",\"publisher\":\"shadow\",", StringComparison.Ordinal))));
        Assert.Throws<InvalidDataException>(() => ModelDescriptor.Parse(Encoding.UTF8.GetBytes(
            Descriptor.Replace("\"format\":\"gguf\"", "\"format\":\"unsupported\"", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task PendingModelMetadataRequiresAdminReviewAndNeverClaimsByteVerification()
    {
        using var host = new Host(); using var client = host.CreateClient();
        const string catalog = "/api/v1/models";
        const string stage = "/api/v1/models/stage";
        const string adminDetail = "/api/v1/admin/model?name=example%2Fdemo&version=1.0.0";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(catalog)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Call(HttpMethod.Post, stage, Reader, Descriptor))).StatusCode);
        using var accepted = await client.SendAsync(Call(HttpMethod.Post, stage, Writer, Descriptor));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var acceptedBody = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var digest = acceptedBody.RootElement.GetProperty("descriptorSha256").GetString()!;
        Assert.False(acceptedBody.RootElement.GetProperty("bytesVerified").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict,
            (await client.SendAsync(Call(HttpMethod.Post, stage, Writer, Descriptor))).StatusCode);
        using var pendingList = JsonDocument.Parse(await (await client.SendAsync(Call(
            HttpMethod.Get, catalog, Reader))).Content.ReadAsStringAsync());
        Assert.Empty(pendingList.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Call(HttpMethod.Get, adminDetail, Reader))).StatusCode);
        using var pending = JsonDocument.Parse(await (await client.SendAsync(Call(
            HttpMethod.Get, adminDetail, Admin))).Content.ReadAsStringAsync());
        Assert.Equal("PENDING", pending.RootElement.GetProperty("entry").GetProperty("reviewState").GetString());
        Assert.False(pending.RootElement.GetProperty("entry").GetProperty("bytesVerified").GetBoolean());
        var decision = JsonSerializer.Serialize(new { name = "example/demo", version = "1.0.0",
            descriptorSha256 = digest, action = "publish", reason = "Synthetic metadata reviewed" });
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Call(HttpMethod.Post, "/api/v1/admin/models/decide", Reader, decision))).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(Call(HttpMethod.Post, "/api/v1/admin/models/decide", Admin, decision))).StatusCode);
        using var published = JsonDocument.Parse(await (await client.SendAsync(Call(
            HttpMethod.Get, catalog, Reader))).Content.ReadAsStringAsync());
        var entry = published.RootElement.GetProperty("items")[0];
        Assert.Equal("example/demo", entry.GetProperty("name").GetString());
        Assert.False(entry.GetProperty("bytesVerified").GetBoolean());
        Assert.False(entry.GetProperty("publisherVerified").GetBoolean());
        var revoke = decision.Replace("\"publish\"", "\"revoke\"", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK,
            (await client.SendAsync(Call(HttpMethod.Post, "/api/v1/admin/models/decide", Admin, revoke))).StatusCode);
        using var empty = JsonDocument.Parse(await (await client.SendAsync(Call(
            HttpMethod.Get, catalog, Reader))).Content.ReadAsStringAsync());
        Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
    }
}
