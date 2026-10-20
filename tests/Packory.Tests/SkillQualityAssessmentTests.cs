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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class SkillQualityAssessmentTests
{
    private const string Name = "quality-fixture";
    private const string Version = "1.0.0";
    private const string Admin = "quality-fixture-admin-token-sufficient-length";
    private const string Reader = "quality-fixture-reader-token-sufficient-length";
    private const string Header = "---\nname: quality-fixture\ndescription: Inspect synthetic files when testing the quality profile.\nlicense: Apache-2.0\n---\n";

    private static PackoryMcpCatalog.SkillSnapshot Snapshot(string body,
        params (string Path, byte[] Bytes)[] resources)
    {
        var files = new List<PackoryMcpCatalog.SkillFile>
        {
            new("SKILL.md", Encoding.UTF8.GetBytes(Header + body))
        };
        files.AddRange(resources.Select(x => new PackoryMcpCatalog.SkillFile(x.Path, x.Bytes)));
        var directories = files.SelectMany(x => x.Path.Split('/').SkipLast(1)
            .Select((_, i) => string.Join('/', x.Path.Split('/').Take(i + 1))))
            .Distinct(StringComparer.Ordinal).ToArray();
        return new PackoryMcpCatalog.SkillSnapshot(Name, Version, new JsonObject
        {
            ["name"] = Name, ["description"] = "Inspect synthetic files when testing the quality profile.",
            ["license"] = "Apache-2.0"
        }, files, directories) { ArchiveSha256 = new string('a', 64) };
    }

    [Fact]
    public void DeterministicProfileBindsBytesWithoutClaimingExecutionSafetyOrQualityScore()
    {
        var snapshot = Snapshot("# Instructions\nRead [details](references/guide.md).\n",
            ("references/guide.md", Encoding.UTF8.GetBytes("Read [root](../SKILL.md#instructions).\n")));
        var first = SkillQualityAssessment.Assess(snapshot, true);
        var second = SkillQualityAssessment.Assess(snapshot, true);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        Assert.Equal(snapshot.ArchiveSha256, first.ArchiveSha256);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(snapshot.Files[0].Bytes)), first.ManifestSha256);
        Assert.Equal("completed", first.AssessmentStatus);
        Assert.Empty(first.Findings);
        Assert.Equal(2, first.Coverage.LocalLinksChecked);
        Assert.Equal(2, first.Coverage.InspectedMarkdownFiles);
        Assert.Contains("behavioral_correctness", first.Coverage.NotAssessed);
        Assert.Contains("malware_safety", first.Coverage.NotAssessed);
        Assert.Contains("publisher_identity", first.Coverage.NotAssessed);
        Assert.DoesNotContain("Score", JsonSerializer.Serialize(first));
    }

    [Fact]
    public void FormatErrorsAndOptionalRecommendationsRemainSeparate()
    {
        var snapshot = Snapshot("\n", ("scripts/example.py", Encoding.UTF8.GetBytes("# Inert script fixture\n")));
        snapshot.Frontmatter["description"] = new string('x', 1025);
        snapshot.Frontmatter["compatibility"] = new JsonArray("python");
        snapshot.Frontmatter["metadata"] = new JsonObject { ["version"] = 1 };
        snapshot.Frontmatter["allowed-tools"] = new JsonArray("Read");
        snapshot.Frontmatter.Remove("license");
        var report = SkillQualityAssessment.Assess(snapshot, true);
        Assert.Contains(report.Findings, x => x.Code == "QUALITY_DESCRIPTION_FORMAT" && x.Severity == "error");
        Assert.Contains(report.Findings, x => x.Code == "QUALITY_COMPATIBILITY_FORMAT" && x.Basis == "specification");
        Assert.Contains(report.Findings, x => x.Code == "QUALITY_METADATA_FORMAT");
        Assert.Contains(report.Findings, x => x.Code == "QUALITY_ALLOWED_TOOLS_FORMAT");
        Assert.Contains(report.Findings, x => x.Code == "QUALITY_LICENSE_UNDECLARED" && x.Severity == "info");
        Assert.Contains(report.Findings, x => x.Code == "QUALITY_EMPTY_INSTRUCTIONS" && x.Basis == "recommendation");
        Assert.Equal("completed", report.AssessmentStatus); // The assessment ran; the findings still contain errors.
        Assert.Contains(report.Dimensions, x => x.Id == "frontmatter" && x.Status == "issues_found");
    }

    [Fact]
    public void OptionalEnvironmentAndIdentityFieldsAreNotMandatory()
    {
        var snapshot = Snapshot("Follow the documented task steps.\n");
        var report = SkillQualityAssessment.Assess(snapshot, false);
        Assert.Empty(report.Findings);
        Assert.False(report.ApprovalEnforced);
        Assert.DoesNotContain(report.Findings, x => x.Code.Contains("AUTHOR", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Findings, x => x.Code.Contains("CONTACT", StringComparison.Ordinal));
    }

    [Fact]
    public void BrokenReferencesReturnControlledGuidanceAndNoEmbeddedSecretOrUrl()
    {
        const string sentinel = "EMBEDDED_PRIVATE_SENTINEL";
        var snapshot = Snapshot("# References\n[missing](references/" + sentinel + ".md?token=" + sentinel + ")\n" +
            "[remote](https://example.invalid/" + sentinel + ")\n" +
            "`[inline-example](missing.md)`\n```md\n[fenced-example](missing.md)\n```\n" +
            "<!-- [comment](missing.md) -->\n");
        var report = SkillQualityAssessment.Assess(snapshot, true);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("QUALITY_MISSING_LOCAL_REFERENCE", finding.Code);
        Assert.Equal(7, finding.Line);
        Assert.Equal(report.ManifestSha256, finding.FileSha256);
        Assert.Equal(1, report.Coverage.LocalLinksChecked);
        Assert.Equal(1, report.Coverage.ExternalLinksNotChecked);
        Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(report));
        Assert.DoesNotContain("https://example.invalid", JsonSerializer.Serialize(report));
        Assert.NotEmpty(finding.Suggestion);
    }

    [Fact]
    public void LocalReferenceResolutionSupportsEscapingTitlesCaseAndNestedPaths()
    {
        var snapshot = Snapshot("[space](<references/guide one.md> \"title\")\n" +
            "[encoded](references/guide%20one.md#heading)\n[case](references/GUIDE.md)\n" +
            "[escape](../outside.md)\n[encoded escape](%2e%2e/outside.md)\n" +
            "[parentheses](references/guide(2).md)\n",
            ("references/guide one.md", Encoding.UTF8.GetBytes("Guide body.\n")),
            ("references/guide.md", Encoding.UTF8.GetBytes("Guide body.\n")),
            ("references/guide(2).md", Encoding.UTF8.GetBytes("Guide body.\n")));
        var report = SkillQualityAssessment.Assess(snapshot, true);
        Assert.Equal(6, report.Coverage.LocalLinksChecked);
        Assert.Single(report.Findings, x => x.Code == "QUALITY_MISSING_LOCAL_REFERENCE");
        Assert.Equal(2, report.Findings.Count(x => x.Code == "QUALITY_NONPORTABLE_LOCAL_REFERENCE"));
    }

    [Fact]
    public void ReferenceStyleLinksUseOnlyReferencedDefinitionsAndSkipCodeExamples()
    {
        var snapshot = Snapshot("[full][guide]\n[guide][]\n[guide]\n[unused]: missing.md\n" +
            "[GUIDE]: references/guide.md\n~~~md\n[bad](missing.md)\n~~~\n" +
            "    [indented](missing.md)\n",
            ("references/guide.md", Encoding.UTF8.GetBytes("Reference.\n")));
        var report = SkillQualityAssessment.Assess(snapshot, true);
        Assert.Empty(report.Findings);
        Assert.Equal(3, report.Coverage.LocalLinksChecked);
    }

    [Fact]
    public void InvalidUtf8AndResourceCapsAreExplicitlyIncomplete()
    {
        var snapshot = Snapshot("Instructions.\n",
            ("references/invalid.md", new byte[] { 0xff, 0xfe, 0xff }),
            ("references/large.md", new byte[SkillQualityAssessment.MaxMarkdownFileBytes + 1]));
        var report = SkillQualityAssessment.Assess(snapshot, true);
        Assert.Equal("incomplete", report.AssessmentStatus);
        Assert.Equal(2, report.Coverage.SkippedMarkdownFiles);
        Assert.Contains(report.Findings, x => x.Code == "QUALITY_INVALID_UTF8");
        Assert.Contains(report.Findings, x => x.Code == "QUALITY_MARKDOWN_LIMIT");
        Assert.Contains(report.Dimensions, x => x.Id == "coverage" && x.Status == "incomplete");
    }

    [Fact]
    public void FindingsAreBoundedWhileDimensionCountsRemainAccurate()
    {
        var snapshot = Snapshot(string.Concat(Enumerable.Repeat("[missing](missing.md)\n", 150)));
        var report = SkillQualityAssessment.Assess(snapshot, true);
        Assert.Equal(SkillQualityAssessment.MaxFindings, report.Findings.Count);
        Assert.True(report.FindingsTruncated);
        Assert.Equal(150, report.Dimensions.Single(x => x.Id == "local_references").ErrorCount);
        Assert.Equal(150, report.Coverage.LocalLinksChecked);
    }

    [Fact]
    public void LinkBudgetAndCancellationDoNotMasqueradeAsCompletedCoverage()
    {
        var snapshot = Snapshot(string.Concat(Enumerable.Repeat("[missing](missing.md)\n", SkillQualityAssessment.MaxLinks + 1)));
        var report = SkillQualityAssessment.Assess(snapshot, true);
        Assert.Equal("incomplete", report.AssessmentStatus);
        Assert.Equal(SkillQualityAssessment.MaxLinks, report.Coverage.LocalLinksChecked);
        Assert.Equal(1, report.Dimensions.Single(x => x.Id == "coverage").WarningCount);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SkillQualityAssessment.Assess(snapshot, true, cancellation.Token));
    }

    [Fact]
    public void LongManifestIsGuidanceNotAFormatFailure()
    {
        var report = SkillQualityAssessment.Assess(Snapshot(string.Concat(Enumerable.Repeat("instruction\n", 500))), true);
        var finding = Assert.Single(report.Findings);
        Assert.Equal("QUALITY_LONG_MANIFEST", finding.Code);
        Assert.Equal("info", finding.Severity);
        Assert.Equal("recommendation", finding.Basis);
    }

    private sealed class Host(bool anonymousRead = false) : WebApplicationFactory<Program>
    {
        private readonly string _data = Path.Combine(Path.GetTempPath(), "packory-quality-" + Guid.NewGuid().ToString("N"));
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Packory:DataDirectory"] = _data, ["Packory:PublicBaseUrl"] = "http://localhost:8080",
                ["Packory:AdminToken"] = Admin, ["Packory:ReadToken"] = Reader,
                ["Packory:AnonymousRead"] = anonymousRead ? "true" : "false"
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Settings>();
                services.AddSingleton(new Settings
                {
                    DataDirectory = _data, PublicBaseUrl = "http://localhost:8080",
                    AdminToken = Admin, ReadToken = Reader, AnonymousRead = anonymousRead
                });
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_data)) Directory.Delete(_data, true);
        }
    }

    private static byte[] Archive()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry(Name + "/SKILL.md").Open());
            writer.Write(Header + "# Steps\nRead the bundled reference.\n");
        }
        return buffer.ToArray();
    }

    private static async Task<string> Stage(Host host, bool approve)
    {
        var bytes = Archive();
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var security = host.Services.GetRequiredService<SkillSecurityStore>();
        Assert.True(security.TryStage(Name, Version, "Synthetic fixture", digest, "skill-staging/" + digest + ".zip"));
        Assert.True(security.SaveResult(Name, Version, digest, "NEEDS_REVIEW", 0, "SAFE", "{}"));
        Assert.True(host.Services.GetRequiredService<IPackageDatabase>().AddSkill(Name, Version, "Synthetic fixture", "synthetic-test"));
        Assert.True(await host.Services.GetRequiredService<BlobStore>().PutAsync(SkillEndpoints.SkillKey(Name, Version),
            new MemoryStream(bytes, false), bytes.Length + 1, CancellationToken.None));
        if (approve) Assert.True(security.TryTransition(Name, Version, digest, "NEEDS_REVIEW", "APPROVED",
            "synthetic-reviewer", "Synthetic quality fixture"));
        return digest;
    }

    private sealed class CallbackBlobStore(Settings settings, Action onRead) : BlobStore(settings)
    {
        private int _called;
        public override string? Existing(string key)
        {
            var path = base.Existing(key);
            if (Interlocked.Exchange(ref _called, 1) == 0) onRead();
            return path;
        }
    }

    private static HttpRequestMessage Authenticated(HttpMethod method, string uri, string token,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task QualityRejectsManagedFeedAndNamespaceGrantsBeforeTheirRateBudget()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        await Stage(host, true);
        const string feed = "quality-other-feed";
        using (var request = Authenticated(HttpMethod.Post, "/api/v1/admin/feeds/drafts", Admin,
            JsonContent.Create(new { id = feed, kind = "nuget", mode = "hosted", displayName = "Synthetic isolated feed" })))
        using (var response = await client.SendAsync(request)) Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using (var request = Authenticated(HttpMethod.Post, "/api/v1/admin/feeds/" + feed + "/activate", Admin))
        using (var response = await client.SendAsync(request)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        async Task<string> CreateToken(string role, string? grant)
        {
            using var request = Authenticated(HttpMethod.Post, "/api/admin/tokens", Admin,
                JsonContent.Create(new { role, @namespace = grant, expiresInHours = 1 }));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            return json.GetProperty("token").GetString()!;
        }
        var globalReader = await CreateToken("read", null);
        var feedReader = await CreateToken("read", "feed:" + feed + ":read");
        var feedPublisher = await CreateToken("write", "feed:" + feed + ":publish");
        var otherSkillPublisher = await CreateToken("write", "skill:other-*");
        var uri = $"/api/v1/skills/{Name}/{Version}/quality";
        foreach (var token in new[] { feedReader, feedPublisher, otherSkillPublisher })
        {
            using var request = Authenticated(HttpMethod.Get, uri, token);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        // Exceed the managed-read partition's 60-request budget using denied
        // feed reads: every denial must still happen before the limiter.
        for (var i = 0; i < 65; i++)
        {
            using var request = Authenticated(HttpMethod.Get, uri, feedReader);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        using (var request = Authenticated(HttpMethod.Get, uri, globalReader))
        using (var response = await client.SendAsync(request)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // A rejected global quality read must not revoke the legitimate feed grant.
        using (var request = Authenticated(HttpMethod.Get, "/feeds/" + feed + "/nuget/v3/index.json", feedReader))
        using (var response = await client.SendAsync(request)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnauthorizedQualityRequestsNeverConsumeAnAuthorizedReadersBudget()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        await Stage(host, true);
        var uri = $"/api/v1/skills/{Name}/{Version}/quality";
        for (var i = 0; i < 65; i++)
        {
            using var response = await client.GetAsync(uri);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        }
        for (var i = 0; i < 65; i++)
        {
            using var request = Authenticated(HttpMethod.Get, uri, "invalid-synthetic-credential");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using var authorized = Authenticated(HttpMethod.Get, uri, Reader);
        using var accepted = await client.SendAsync(authorized);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(SkillQualityAssessment.ProfileVersion,
            (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profileVersion").GetString());
    }

    [Fact]
    public async Task ExhaustedAnonymousQualityBudgetLeavesAuthenticatedQualityAndMcpAvailable()
    {
        using var host = new Host(anonymousRead: true);
        using var client = host.CreateClient();
        var digest = await Stage(host, true);
        var uri = $"/api/v1/skills/{Name}/{Version}/quality";
        // The anonymous quality partition has a concrete 60/minute allowance;
        // exhaust it without consuming the separate authenticated MCP quota.
        for (var i = 0; i < 60; i++)
        {
            using var response = await client.GetAsync(uri);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        using (var response = await client.GetAsync(uri)) Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        using (var request = Authenticated(HttpMethod.Get, uri, Reader))
        using (var response = await client.SendAsync(request)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(client.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Reader }
        }, client);
        await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        Assert.Contains(await mcp.ListToolsAsync(cancellationToken: timeout.Token), x => x.Name == "assess_skill_quality");
        var assessment = await mcp.CallToolAsync("assess_skill_quality",
            new Dictionary<string, object?> { ["name"] = Name, ["version"] = Version }, cancellationToken: timeout.Token);
        Assert.Contains(digest, JsonSerializer.Serialize(assessment));
        Assert.DoesNotContain("unavailable", JsonSerializer.Serialize(assessment));
    }

    [Fact]
    public async Task PublishedAssessmentRequiresApprovalRechecksRevocationAndRejectsTamperedBytes()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var digest = await Stage(host, false);
        var settings = host.Services.GetRequiredService<Settings>();
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var blobs = host.Services.GetRequiredService<BlobStore>();
        var security = host.Services.GetRequiredService<SkillSecurityStore>();
        SkillQualityAssessment.Report? Read() => SkillQualityAssessment.AssessPublished(Name, Version, db, blobs, security, settings);
        Assert.Null(Read());
        Assert.Null(SkillQualityAssessment.AssessPublished("../invalid", Version, db, blobs, security, settings));
        Assert.True(security.TryTransition(Name, Version, digest, "NEEDS_REVIEW", "APPROVED", "synthetic-reviewer", "Fixture approval"));
        Assert.Equal(digest, Assert.IsType<SkillQualityAssessment.Report>(Read()).ArchiveSha256);
        var file = blobs.Existing(SkillEndpoints.SkillKey(Name, Version))!;
        var approved = File.ReadAllBytes(file);
        File.WriteAllBytes(file, Encoding.UTF8.GetBytes("tampered synthetic archive"));
        Assert.Null(Read());
        File.WriteAllBytes(file, approved);
        Assert.NotNull(Read());
        using var duringRead = new CancellationTokenSource();
        var cancellingBlobs = new CallbackBlobStore(settings, duringRead.Cancel);
        Assert.Throws<OperationCanceledException>(() => SkillQualityAssessment.AssessPublished(Name, Version, db,
            cancellingBlobs, security, settings, duringRead.Token));
        var revokingBlobs = new CallbackBlobStore(settings, () => Assert.True(security.TryTransition(Name, Version, digest,
            "APPROVED", "REVOKED", "synthetic-reviewer", "Revoke during materialization")));
        Assert.Null(SkillQualityAssessment.AssessPublished(Name, Version, db, revokingBlobs, security, settings));
        Assert.Null(Read());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SkillQualityAssessment.AssessPublished(Name, Version, db, blobs, security, settings,
            cancellation.Token));
    }

    [Fact]
    public async Task OfficialMcpClientAndRestExposeSameDigestBoundAssessmentAndHideRevokedSkill()
    {
        using var host = new Host();
        using var client = host.CreateClient();
        var uri = $"/api/v1/skills/{Name}/{Version}/quality";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(uri)).StatusCode);
        var digest = await Stage(host, true);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Reader);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(digest, document.GetProperty("archiveSha256").GetString());
        Assert.Equal(SkillQualityAssessment.ProfileVersion, document.GetProperty("profileVersion").GetString());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(client.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Reader }
        }, client);
        await using var mcp = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        Assert.Contains(await mcp.ListToolsAsync(cancellationToken: timeout.Token), x => x.Name == "assess_skill_quality");
        var args = new Dictionary<string, object?> { ["name"] = Name, ["version"] = Version };
        var assessment = await mcp.CallToolAsync("assess_skill_quality", args, cancellationToken: timeout.Token);
        Assert.Contains(digest, JsonSerializer.Serialize(assessment));
        Assert.Contains("behavioral_correctness", JsonSerializer.Serialize(assessment));
        var artifactPath = host.Services.GetRequiredService<BlobStore>().Existing(SkillEndpoints.SkillKey(Name, Version))!;
        var approvedBytes = File.ReadAllBytes(artifactPath);
        File.WriteAllBytes(artifactPath, Encoding.UTF8.GetBytes("tampered fixture"));
        using var readTampered = new HttpRequestMessage(HttpMethod.Get, uri);
        readTampered.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Reader);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(readTampered)).StatusCode);
        var tampered = await mcp.CallToolAsync("assess_skill_quality", args, cancellationToken: timeout.Token);
        Assert.Contains("unavailable", JsonSerializer.Serialize(tampered));
        Assert.DoesNotContain(digest, JsonSerializer.Serialize(tampered));
        File.WriteAllBytes(artifactPath, approvedBytes);
        Assert.True(host.Services.GetRequiredService<SkillSecurityStore>().TryTransition(Name, Version, digest,
            "APPROVED", "REVOKED", "synthetic-reviewer", "Fixture revocation"));
        var revoked = await mcp.CallToolAsync("assess_skill_quality", args, cancellationToken: timeout.Token);
        Assert.Contains("unavailable", JsonSerializer.Serialize(revoked));
        Assert.DoesNotContain(digest, JsonSerializer.Serialize(revoked));
        using var readRevoked = new HttpRequestMessage(HttpMethod.Get, uri);
        readRevoked.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Reader);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(readRevoked)).StatusCode);
    }
}
