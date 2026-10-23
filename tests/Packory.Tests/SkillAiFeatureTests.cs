using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packory;
using Xunit;

namespace Packory.Tests;

/// <summary>Offline, credential-free tests for the optional, fail-closed AI policy.</summary>
public sealed class SkillAiFeatureTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "packory-skill-ai-" + Guid.NewGuid().ToString("N"));
    private static SkillAiEndpoint Local => new("local", "ollama", "http://localhost:11434/v1", ["127.0.0.0/8"]);
    private readonly SkillAiDeployment deployment;
    private readonly SkillAiStore store;

    public SkillAiFeatureTests()
    {
        Directory.CreateDirectory(folder);
        deployment = new SkillAiDeployment { Endpoints = [Local] }.Validate();
        var settings = new Settings { DataDirectory = folder, MetadataProvider = "sqlite" };
        store = new SkillAiStore(settings, deployment, new SkillAiSecrets(deployment));
        store.Initialize();
    }

    [Fact]
    public void AiIsOffByDefaultAndNeverRequiredForApproval()
    {
        var cfg = store.Configuration().Configuration;
        Assert.False(cfg.Enabled);
        Assert.False(cfg.RequireForApproval);
        Assert.True(store.CanApprove("inert", "1.0", new string('a', 64)));
    }

    [Fact]
    public void PolicyChangesHaveOptimisticRevisionAndCancelQueuedJobs()
    {
        var cfg = store.Configuration().Configuration with {
            Enabled = true, EndpointId = Local.Id, Model = "synthetic-model",
            RequireForApproval = true, InputUsdPerMillionTokens = 1m,
            OutputUsdPerMillionTokens = 2m
        };
        var saved = store.Save(new(cfg, null, false, "Operator opt in"), "synthetic-operator");
        Assert.Equal(1L, saved.Configuration.Revision);
        Assert.False(store.CanApprove("inert", "1.0", new string('a', 64)));
        var job = store.Queue(null, Guid.NewGuid().ToString("N"), "synthetic-operator", "Probe model");
        Assert.Equal("QUEUED", job.State);
        Assert.True(store.Start(job.Id));
        Assert.True(store.AdmitRequest(job.Id, Guid.NewGuid().ToString("N"), out var error), error);
        Assert.Equal(1, store.Budget().Requests);
        Assert.Throws<InvalidOperationException>(() =>
            store.Save(new(cfg, null, false, "Stale update"), "synthetic-operator"));
        var disabled = store.Save(new(saved.Configuration with {
            Enabled = false, RequireForApproval = false
        }, null, false, "Operator disables model"), "synthetic-operator");
        Assert.Equal(2L, disabled.Configuration.Revision);
        Assert.Equal("CANCELLED", store.Get(job.Id)!.State);
        Assert.False(store.Start(job.Id));
        Assert.True(store.CanApprove("inert", "1.0", new string('a', 64)));
        Assert.Contains(store.History(), _ => true);
    }

    [Fact]
    public void ProviderRequestNeverAcceptsToolsOrExternalModelOverride()
    {
        var cfg = new SkillAiConfiguration { EndpointId = "local", Model = "approved-model" };
        var raw = "{\"model\":\"malicious-model\",\"messages\":[{\"role\":\"user\",\"content\":\"untrusted skill text\"}]}";
        var validated = JsonNode.Parse(SkillAiGateway.ValidateRequest(raw, cfg, Local))!;
        Assert.Equal("approved-model", validated["model"]!.GetValue<string>());
        Assert.False(validated["stream"]!.GetValue<bool>());
        Assert.Equal(4096, validated["max_tokens"]!.GetValue<int>());
        var injection = "{\"messages\":[{\"role\":\"user\",\"content\":\"inert\"}],\"tool_choice\":\"required\",\"tools\":[{\"type\":\"web_search\"}]}";
        Assert.Throws<InvalidDataException>(() => SkillAiGateway.ValidateRequest(injection, cfg, Local));
        Assert.Throws<InvalidDataException>(() => SkillAiGateway.ValidateRequest(
            "{\"model\":\"a\",\"model\":\"b\",\"messages\":[{\"role\":\"user\",\"content\":\"inert\"}]}", cfg, Local));
    }

    [Fact]
    public void NetworkPolicyRejectsMetadataAndPrivateDestinationsUnlessExplicitlyAllowlisted()
    {
        var cloud = new SkillAiEndpoint("cloud", "openai", "https://api.openai.com/v1");
        foreach (var value in new[] { "169.254.169.254", "127.0.0.1", "10.0.0.1", "192.168.1.1", "::1" })
            Assert.False(SkillAiTransport.AddressAllowed(cloud, IPAddress.Parse(value)), value);
        Assert.True(SkillAiTransport.AddressAllowed(Local, IPAddress.Loopback));
        Assert.False(SkillAiTransport.AddressAllowed(Local, IPAddress.Parse("169.254.169.254")));
        Assert.True(SkillAiTransport.AddressAllowed(cloud, IPAddress.Parse("1.1.1.1")));
        Assert.Throws<InvalidDataException>(() =>
            new SkillAiDeployment { Endpoints = [new("unsafe", "openai", "http://169.254.169.254")] }.Validate());
    }

    [Fact]
    public void SecretsAreBoundToEndpointAndCannotBeReadWithWrongContext()
    {
        var ring = Path.Combine(folder, "synthetic-ring.json");
        File.WriteAllText(ring, JsonSerializer.Serialize(new {
            active = "k1", keys = new Dictionary<string, string> { ["k1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
        }));
        var crypt = new SkillAiSecrets(new SkillAiDeployment { KeyRingFile = ring });
        var encrypted = crypt.Protect("synthetic-not-a-real-secret", "one");
        Assert.DoesNotContain("synthetic-not-a-real-secret", encrypted);
        Assert.Equal("synthetic-not-a-real-secret", crypt.Unprotect(encrypted, "one"));
        Assert.ThrowsAny<CryptographicException>(() => crypt.Unprotect(encrypted, "two"));
    }

    [Fact]
    public void LlmReportRequiresCompletedInferenceMetadataAndCanBlockWithoutAutoApproval()
    {
        const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var report = JsonSerializer.SerializeToNode(new {
            sha256 = digest, exit_code = 0, report = new {
                execution_successful = true,
                analysis_completeness = new { is_complete = true, status = "complete", coverage_percent = 100 },
                metadata = new { llm_requested = true, llm_available = true, llm_used = true },
                risk_assessment = new { score = 92, severity = "HIGH", recommendation = "DO_NOT_INSTALL" },
                issues = new[] { new { severity = "HIGH" } }
            }
        })!;
        Assert.Equal("QUARANTINED", SkillScanResult.Parse(report.ToJsonString(), digest, expectLlm: true).State);
        report["report"]!["metadata"]!["llm_used"] = false;
        Assert.Equal("SCAN_FAILED", SkillScanResult.Parse(report.ToJsonString(), digest, expectLlm: true).State);
        report["report"]!["metadata"]!["llm_used"] = true;
        Assert.Equal("SCAN_FAILED", SkillScanResult.Parse(report.ToJsonString(), digest).State);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }
}
