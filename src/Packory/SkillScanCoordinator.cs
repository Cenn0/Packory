using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Packory;

/// <summary>
/// Writer-owned scan dispatcher. The API writes only opaque, bounded snapshots to the
/// input-only volume; an unprivileged sidecar consumes these and writes JSON into a
/// different output volume. No untrusted skill code is executed in the Packory process.
/// </summary>
public sealed class SkillScanCoordinator(
    Settings settings, SkillSecurityStore store, IPackageDatabase database,
    BlobStore blobs, ILogger<SkillScanCoordinator> logger) : BackgroundService
{
    public string Root => Path.Combine(Path.GetFullPath(settings.DataDirectory), "skill-scans");
    public string Incoming => Path.Combine(Root, "in");
    public string Outgoing => Path.Combine(Root, "out");

    public static string JobId(SkillSecurityStore.Revision rev) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            rev.Name + "\n" + rev.Version + "\n" + rev.Sha256)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.SkillSecurityEnforced || settings.InstanceRole == "reader" ||
            settings.RecoveryMaintenance) return;
        Directory.CreateDirectory(Incoming);
        Directory.CreateDirectory(Outgoing);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Skill security dispatcher failed; artifacts remain unavailable."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        // Defense in depth for direct callers as well as the hosted dispatcher.
        if (settings.RecoveryMaintenance) return;
        // Complete results first; each result is matched to an immutable digest and job id.
        foreach (var entry in store.List(1000))
        {
            ct.ThrowIfCancellationRequested();
            if (entry.State == "PUBLISHING")
            {
                try { await FinishApprovalAsync(entry, ct); }
                catch (Exception ex) { logger.LogWarning(ex, "Skill publication recovery is pending."); }
                continue;
            }
            if (entry.State != "QUEUED") continue;
            var id = JobId(entry);
            var answer = Path.Combine(Outgoing, id + ".json");
            if (File.Exists(answer))
            {
                var text = File.ReadAllText(answer);
                try
                {
                    var (state, risk, recommendation, report) = SkillScanResult.Parse(text, entry.Sha256);
                    if (store.SaveResult(entry.Name, entry.Version, entry.Sha256,
                        state, risk, recommendation, report))
                    {
                        File.Delete(answer);
                        DeleteInput(id);
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    logger.LogWarning("Rejected malformed skill scan report; revision remains blocked.");
                    if (store.SaveResult(entry.Name, entry.Version, entry.Sha256,
                        "SCAN_FAILED", null, null, null))
                    {
                        File.Delete(answer);
                        DeleteInput(id);
                    }
                }
                continue;
            }
            var manifestPath = Path.Combine(Incoming, id + ".json");
            if (File.Exists(manifestPath)) continue;
            var source = blobs.Existing(entry.StagingKey);
            if (source is null)
            {
                store.SaveResult(entry.Name, entry.Version, entry.Sha256, "SCAN_FAILED", null, null, null);
                continue;
            }
            var zipPath = Path.Combine(Incoming, id + ".zip");
            if (!File.Exists(zipPath))
            {
                File.Copy(source, zipPath);
                if (!string.Equals(HashFile(zipPath), entry.Sha256, StringComparison.Ordinal))
                {
                    File.Delete(zipPath);
                    store.SaveResult(entry.Name, entry.Version, entry.Sha256, "SCAN_FAILED", null, null, null);
                    continue;
                }
            }
            var payload = JsonSerializer.Serialize(new { id, sha256 = entry.Sha256 });
            var tmp = manifestPath + ".tmp";
            await File.WriteAllTextAsync(tmp, payload, ct);
            File.Move(tmp, manifestPath, overwrite: true);
        }
    }

    private void DeleteInput(string id)
    {
        foreach (var suffix in new[] { ".json", ".zip" })
        {
            var path = Path.Combine(Incoming, id + suffix);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public async Task<bool> ApproveAsync(string name, string version, string digest,
        string actor, string reason, CancellationToken ct)
    {
        if (settings.RecoveryMaintenance)
            throw new InvalidOperationException("Skill approval is disabled during offline recovery maintenance.");
        if (!store.TryTransition(name, version, digest, "NEEDS_REVIEW", "PUBLISHING", actor, reason))
            return false;
        var rev = store.Get(name, version)!;
        await FinishApprovalAsync(rev, ct);
        return store.IsApproved(name, version);
    }

    private async Task FinishApprovalAsync(SkillSecurityStore.Revision rev, CancellationToken ct)
    {
        if (rev.State != "PUBLISHING") return;
        var source = blobs.Existing(rev.StagingKey);
        if (source is null || HashFile(source) != rev.Sha256)
            throw new InvalidDataException("Staged skill differs from approved digest.");
        var key = SkillEndpoints.SkillKey(rev.Name, rev.Version);
        var published = blobs.Existing(key);
        if (published is null)
        {
            database.BeginPublication(key, "skill", rev.Name, rev.Version);
            await using var input = File.OpenRead(source);
            if (!await blobs.PutAsync(key, input, settings.MaxArtifactBytes, ct))
                throw new IOException("Concurrent artifact publish detected.");
        }
        else if (HashFile(published) != rev.Sha256)
            throw new InvalidDataException("Existing published skill has a different digest.");

        if (!database.Skills(rev.Name).Any(x => x.Version == rev.Version))
        {
            if (!database.AddSkill(rev.Name, rev.Version, rev.Description, "security-approval") &&
                !database.Skills(rev.Name).Any(x => x.Version == rev.Version))
                throw new IOException("Security-approved skill catalog commit failed.");
        }
        database.CompletePublication(key);
        store.TryTransition(rev.Name, rev.Version, rev.Sha256, "PUBLISHING",
            "APPROVED", "system", "Verified immutable artifact and catalog visibility");
    }

    public static string HashFile(string file) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
}

public static class SkillScanResult
{
    private static readonly (string State, int? Risk, string? Recommendation, string? Report) Failed =
        ("SCAN_FAILED", null, null, null);

    /// <summary>
    /// The scanner's aggregate risk may be lower than an individual HIGH/CRITICAL
    /// finding. Evaluate every finding and never interpret missing/malformed fields as
    /// a clean result. Unknown suppressed findings are not safe to release.
    /// </summary>
    public static (string State, int? Risk, string? Recommendation, string? Report) Parse(
        string serialized, string requiredDigest)
    {
        if (serialized.Length > 1024 * 1024)
            throw new InvalidDataException("Oversized scanner response.");
        using var envelope = JsonDocument.Parse(serialized);
        var root = envelope.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("sha256", out var providedDigest) ||
            providedDigest.ValueKind != JsonValueKind.String ||
            !string.Equals(providedDigest.GetString(), requiredDigest, StringComparison.Ordinal))
            throw new InvalidDataException("Scanner response digest mismatch.");

        try
        {
            if (!root.TryGetProperty("exit_code", out var code) ||
                code.ValueKind != JsonValueKind.Number ||
                !code.TryGetInt32(out var exitCode) || exitCode is not (0 or 1))
                return Failed;
            if (!root.TryGetProperty("report", out var report) ||
                report.ValueKind != JsonValueKind.Object)
                return Failed;
            if (!report.TryGetProperty("execution_successful", out var success) ||
                success.ValueKind != JsonValueKind.True)
                return Failed;
            if (!report.TryGetProperty("analysis_completeness", out var completeness) ||
                completeness.ValueKind != JsonValueKind.Object ||
                !completeness.TryGetProperty("is_complete", out var complete) ||
                complete.ValueKind != JsonValueKind.True)
                return Failed;
            if (completeness.TryGetProperty("status", out var completionStatus) &&
                (completionStatus.ValueKind != JsonValueKind.String ||
                 completionStatus.GetString() != "complete"))
                return Failed;
            if (!report.TryGetProperty("metadata", out var metadata) ||
                metadata.ValueKind != JsonValueKind.Object ||
                !metadata.TryGetProperty("llm_requested", out var llm) ||
                llm.ValueKind != JsonValueKind.False)
                return Failed;

            if (!report.TryGetProperty("risk_assessment", out var assessment) ||
                assessment.ValueKind != JsonValueKind.Object ||
                !assessment.TryGetProperty("score", out var rawScore) ||
                rawScore.ValueKind != JsonValueKind.Number ||
                !rawScore.TryGetInt32(out var score) || score is < 0 or > 100 ||
                !assessment.TryGetProperty("severity", out var rawSeverity) ||
                rawSeverity.ValueKind != JsonValueKind.String ||
                !assessment.TryGetProperty("recommendation", out var rawRecommendation) ||
                rawRecommendation.ValueKind != JsonValueKind.String)
                return Failed;
            var severity = rawSeverity.GetString();
            var recommendation = rawRecommendation.GetString();
            if (severity is not ("LOW" or "MEDIUM" or "HIGH" or "CRITICAL") ||
                recommendation is not ("SAFE" or "CAUTION" or "DO_NOT_INSTALL"))
                return Failed;
            if (!report.TryGetProperty("issues", out var issues) ||
                issues.ValueKind != JsonValueKind.Array)
                return Failed;

            var dangerousFinding = false;
            foreach (var issue in issues.EnumerateArray())
            {
                if (issue.ValueKind != JsonValueKind.Object ||
                    !issue.TryGetProperty("severity", out var findingSeverity) ||
                    findingSeverity.ValueKind != JsonValueKind.String)
                    return Failed;
                var rating = findingSeverity.GetString();
                if (rating is not ("LOW" or "MEDIUM" or "HIGH" or "CRITICAL"))
                    return Failed;
                if (rating is "HIGH" or "CRITICAL") dangerousFinding = true;
            }
            // No publisher-controlled baselines/suppressions are configured in
            // Packory. Unexpected suppressions might hide a severe finding.
            if (report.TryGetProperty("suppressed", out var suppressed))
            {
                if (suppressed.ValueKind != JsonValueKind.Array) return Failed;
                if (suppressed.GetArrayLength() != 0) dangerousFinding = true;
            }
            if (report.TryGetProperty("suppressed_count", out var suppressedCount))
            {
                if (suppressedCount.ValueKind != JsonValueKind.Number ||
                    !suppressedCount.TryGetInt32(out var count) || count < 0)
                    return Failed;
                if (count != 0) dangerousFinding = true;
            }

            var state = dangerousFinding || severity is "HIGH" or "CRITICAL" ||
                        recommendation == "DO_NOT_INSTALL"
                ? "QUARANTINED" : "NEEDS_REVIEW";
            // Retain findings for admin review only. The original report must
            // never appear in anonymous REST endpoints or MCP tools.
            return (state, score, recommendation, report.GetRawText());
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException
                                   or KeyNotFoundException or OverflowException)
        {
            return Failed;
        }
    }
}
