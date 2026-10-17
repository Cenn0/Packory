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
        if (!settings.SkillSecurityEnforced || settings.InstanceRole == "reader") return;
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
    /// <summary>Accept only the verified SkillSpector JSON contract and conservative policy decisions.</summary>
    public static (string State, int? Risk, string? Recommendation, string? Report) Parse(
        string serialized, string requiredDigest)
    {
        if (serialized.Length > 1024 * 1024) throw new InvalidDataException("Oversized scanner response.");
        using var envelope = JsonDocument.Parse(serialized);
        var root = envelope.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.GetProperty("sha256").GetString() != requiredDigest)
            throw new InvalidDataException("Scanner response digest mismatch.");

        var exitCode = root.GetProperty("exit_code").GetInt32();
        if (exitCode is not (0 or 1)) return ("SCAN_FAILED", null, null, null);
        var report = root.GetProperty("report");
        if (report.ValueKind != JsonValueKind.Object) return ("SCAN_FAILED", null, null, null);
        if (!report.TryGetProperty("execution_successful", out var success) ||
            success.ValueKind != JsonValueKind.True)
            return ("SCAN_FAILED", null, null, null);
        if (!report.TryGetProperty("analysis_completeness", out var completeness) ||
            completeness.ValueKind != JsonValueKind.Object ||
            !completeness.TryGetProperty("is_complete", out var complete) ||
            complete.ValueKind != JsonValueKind.True)
            return ("SCAN_FAILED", null, null, null);
        if (!report.TryGetProperty("metadata", out var metadata) ||
            metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("llm_requested", out var llm) ||
            llm.ValueKind != JsonValueKind.False)
            return ("SCAN_FAILED", null, null, null);
        var assessment = report.GetProperty("risk_assessment");
        var score = assessment.GetProperty("score").GetInt32();
        var severity = assessment.GetProperty("severity").GetString();
        var recommendation = assessment.GetProperty("recommendation").GetString();
        if (score is < 0 or > 100 || severity is not ("LOW" or "MEDIUM" or "HIGH" or "CRITICAL") ||
            recommendation is not ("SAFE" or "CAUTION" or "DO_NOT_INSTALL"))
            return ("SCAN_FAILED", null, null, null);
        if (!report.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
            return ("SCAN_FAILED", null, null, null);

        var state = severity is "HIGH" or "CRITICAL" || recommendation == "DO_NOT_INSTALL"
            ? "QUARANTINED" : "NEEDS_REVIEW";
        // The original findings can contain credentials or prompt injection. Never return this JSON
        // via public listing or MCP; privileged UI treats all fields strictly as text.
        return (state, score, recommendation, report.GetRawText());
    }
}
