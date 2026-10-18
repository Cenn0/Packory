using System.Text.Json;
using System.Text.Json.Nodes;
using Packory;
using Xunit;

namespace Packory.Tests;

/// <summary>
/// Synthetic, non-executable scanner contracts. An apparently clean score
/// cannot override contradictory coverage or a replayed scan attempt.
/// </summary>
public sealed class SkillVerificationTests
{
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static JsonNode ValidResult() => JsonSerializer.SerializeToNode(new
    {
        sha256 = Digest,
        exit_code = 0,
        report = new
        {
            execution_successful = true,
            analysis_completeness = new
            {
                is_complete = true,
                status = "complete",
                coverage_percent = 100,
                fully_inspected_files = 1,
                partially_inspected_files = 0,
                entirely_uninspected_files = 0,
                ledger_exceptions = Array.Empty<object>(),
                scope_exclusions = Array.Empty<object>(),
                limitations = Array.Empty<object>()
            },
            metadata = new { llm_requested = false, llm_used = false },
            risk_assessment = new { score = 0, severity = "LOW", recommendation = "SAFE" },
            issues = Array.Empty<object>()
        }
    })!;

    [Fact]
    public void CompleteStaticScanStillNeedsExplicitHumanReview()
    {
        var answer = SkillScanResult.Parse(ValidResult().ToJsonString(), Digest);
        Assert.Equal("NEEDS_REVIEW", answer.State);
        Assert.Equal(0, answer.Risk);
        Assert.NotNull(answer.Report);
    }

    [Theory]
    [InlineData("partially_inspected_files", 1)]
    [InlineData("entirely_uninspected_files", 1)]
    [InlineData("failed_files", 1)]
    [InlineData("skipped_files", 1)]
    [InlineData("coverage_percent", 99)]
    public void CleanScoreCannotOverrideContradictoryCoverage(string field, int count)
    {
        var result = ValidResult();
        result["report"]!["analysis_completeness"]![field] = count;
        Assert.Equal("SCAN_FAILED", SkillScanResult.Parse(result.ToJsonString(), Digest).State);
    }

    [Theory]
    [InlineData("ledger_exceptions")]
    [InlineData("scope_exclusions")]
    [InlineData("limitations")]
    public void UninspectedContentOrUnresolvedExceptionsRejectCleanScan(string field)
    {
        var result = ValidResult();
        result["report"]!["analysis_completeness"]![field] =
            new JsonArray(JsonValue.Create("synthetic incomplete coverage"));
        Assert.Equal("SCAN_FAILED", SkillScanResult.Parse(result.ToJsonString(), Digest).State);
    }

    [Fact]
    public void MalformedCoverageCountsOrUnexpectedLlmUsageFailClosed()
    {
        var result = ValidResult();
        result["report"]!["analysis_completeness"]!["partially_inspected_files"] = "zero";
        Assert.Equal("SCAN_FAILED", SkillScanResult.Parse(result.ToJsonString(), Digest).State);
        result = ValidResult();
        result["report"]!["metadata"]!["llm_used"] = true;
        Assert.Equal("SCAN_FAILED", SkillScanResult.Parse(result.ToJsonString(), Digest).State);
    }

    [Fact]
    public void DuplicateJsonPropertiesNeverBecomeApprovedScannerEvidence()
    {
        var good = ValidResult().ToJsonString();
        var duplicate = good.Replace("\"is_complete\":true",
            "\"is_complete\":true,\"is_complete\":true", StringComparison.Ordinal);
        Assert.NotEqual(good, duplicate);
        // A duplicate property is rejected at the envelope boundary. The
        // coordinator records this as SCAN_FAILED and never allows approval.
        Assert.Throws<InvalidDataException>(() => SkillScanResult.Parse(duplicate, Digest));
    }

    [Fact]
    public void OldScanResultCannotBeAddressedByARescanAttempt()
    {
        var first = new SkillSecurityStore.Revision(
            "fixture", "1.0.0", "Fixture", Digest, "skill-staging/" + Digest + ".zip",
            "QUEUED", null, null, null, "2026-10-18T23:00:00.0000000Z");
        var retry = first with { UpdatedAt = "2026-10-18T23:05:00.0000000Z" };
        var firstId = SkillScanCoordinator.JobId(first);
        var nextId = SkillScanCoordinator.JobId(retry);
        Assert.NotEqual(firstId, nextId);
        Assert.Equal(64, firstId.Length);
        Assert.Equal(64, nextId.Length);
        Assert.Equal(firstId, SkillScanCoordinator.JobId(first));
    }
}
