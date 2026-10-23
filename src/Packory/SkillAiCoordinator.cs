using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packory;

/// <summary>File-only bridge to an offline scanner. Never runs submitted code or shells in the API.</summary>
public sealed class SkillAiCoordinator(Settings settings, SkillAiStore store, SkillSecurityStore security,
    BlobStore blobs, SkillAiDeployment deployment, SkillAiGateway gateway,
    ILogger<SkillAiCoordinator> logger) : BackgroundService
{
    public string Root => Path.Combine(Path.GetFullPath(settings.DataDirectory), "skill-ai");
    public string Incoming => Path.Combine(Root, "in");
    public string Outgoing => Path.Combine(Root, "out");
    private readonly Dictionary<string, (Task Task, CancellationTokenSource Cancel)> requests = new();

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record RelayRequest(string JobId, string RequestId, string Body);
    private static bool Id(string? id) => id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string BoundedFile(string path, int max)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length > max) throw new InvalidDataException("Invalid AI exchange file.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = new byte[max + 1]; var length = 0;
        while (length < bytes.Length)
        { var count = input.Read(bytes, length, bytes.Length - length); if (count == 0) break; length += count; }
        if (length > max) throw new InvalidDataException("Oversized AI exchange file.");
        return new UTF8Encoding(false, true).GetString(bytes, 0, length);
    }
    private static async Task WriteAsync(string path, object value, CancellationToken ct)
    {
        var temp = path + ".tmp";
        // Do not follow a leftover symlink in a shared exchange volume.
        File.Delete(temp);
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, SkillAiJson.Options), ct);
        File.Move(temp, path, true);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (settings.InstanceRole == "reader" || settings.RecoveryMaintenance || !settings.SkillSecurityEnforced) return;
        Directory.CreateDirectory(Incoming); Directory.CreateDirectory(Outgoing);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            do
            {
                try { await TickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { logger.LogWarning("AI dispatcher failed; no approval was granted."); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        finally
        {
            foreach (var item in requests.Values) item.Cancel.Cancel();
            await Task.WhenAll(requests.Values.Select(r => r.Task));
            foreach (var item in requests.Values) item.Cancel.Dispose();
        }
    }
    private DateTimeOffset nextAutomatic = DateTimeOffset.MinValue;
    internal async Task TickAsync(CancellationToken ct)
    {
        if (settings.RecoveryMaintenance || settings.InstanceRole == "reader" || !settings.SkillSecurityEnforced) return;
        var cfg = store.Configuration().Configuration;
        foreach (var item in requests.ToArray())
        {
            var job = store.Get(item.Key);
            if (job is null || job.State != "RUNNING" || !cfg.Enabled || job.Revision != cfg.Revision) item.Value.Cancel.Cancel();
            if (item.Value.Task.IsCompleted)
            {
                await item.Value.Task; item.Value.Cancel.Dispose(); requests.Remove(item.Key);
                if (store.Get(item.Key)?.State != "RUNNING") Cleanup(item.Key);
            }
        }
        if (cfg.Enabled && cfg.Trigger != "manual" && DateTimeOffset.UtcNow >= nextAutomatic)
        {
            nextAutomatic = DateTimeOffset.UtcNow.AddSeconds(5);
            foreach (var revision in store.AutomaticCandidates())
            {
                if (cfg.Trigger == "suspicious" && revision.RiskScore is not > 0 && revision.State != "QUARANTINED") continue;
                try
                {
                    await store.PolicyGate.WaitAsync(ct);
                    try
                    {
                        var current = security.Get(revision.Name, revision.Version);
                        if (current?.State is "NEEDS_REVIEW" or "QUARANTINED")
                            store.Queue(current, "", "system", "Configured automatic AI assessment", automatic: true);
                    }
                    finally { store.PolicyGate.Release(); }
                }
                catch (InvalidOperationException) { break; }
            }
        }
        foreach (var queued in store.List(pending: true, limit: 100).Reverse())
        {
            ct.ThrowIfCancellationRequested();
            if (!cfg.Enabled || queued.Revision != cfg.Revision)
            { store.Finish(queued.Id, "CANCELLED", "configuration_changed"); continue; }
            if (queued.State == "QUEUED" && !store.Start(queued.Id)) continue;
            var job = store.Get(queued.Id)!;
            if (job.Deadline is null || job.Deadline <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            { store.Finish(job.Id, "TIMED_OUT", "scan_timeout"); Cleanup(job.Id); continue; }
            if (requests.ContainsKey(job.Id)) continue;
            if (job.Kind == "probe") { StartTask(job.Id, token => ProbeAsync(job, token), ct); continue; }
            try
            {
                var revision = security.Get(job.Name, job.Version);
                if (revision is null || revision.Sha256 != job.Sha256 || revision.State is "QUEUED" or "SCAN_FAILED")
                { store.Finish(job.Id, "CANCELLED", "static_revision_changed"); Cleanup(job.Id); continue; }
                var result = Path.Combine(Outgoing, job.Id + ".result.json");
                if (File.Exists(result))
                {
                    var text = BoundedFile(result, 1024 * 1024);
                    var envelope = SkillAiJson.Read<JsonElement>(text, 1024 * 1024);
                    if (envelope.GetProperty("id").GetString() != job.Id ||
                        envelope.GetProperty("scanner_version").GetString() != SkillAiDeployment.ScannerVersion ||
                        envelope.GetProperty("revision").GetInt64() != job.Revision)
                        throw new InvalidDataException("AI result identity mismatch.");
                    var parsed = SkillScanResult.Parse(text, job.Sha256, expectLlm: true);
                    if (!store.HasCompletedInference(job.Id)) throw new InvalidDataException("No completed model request.");
                    var diagnostic = parsed.Report ?? (envelope.TryGetProperty("report", out var raw) && raw.ValueKind == JsonValueKind.Object ? raw.GetRawText() : null);
                    store.Finish(job.Id, parsed.State == "NEEDS_REVIEW" ? "COMPLETED" : parsed.State == "QUARANTINED" ? "BLOCKED" : "FAILED",
                        parsed.State == "SCAN_FAILED" ? "incomplete_analysis" : null, diagnostic, parsed.Risk, parsed.Recommendation);
                    Cleanup(job.Id); continue;
                }
                var manifest = Path.Combine(Incoming, job.Id + ".job.json");
                if (!File.Exists(manifest)) await DispatchAsync(job, manifest, ct);
                // A single in-flight provider call per job bounds concurrency even if an SDK fans out.
                var requestPath = Directory.EnumerateFiles(Outgoing, job.Id + ".*.request.json").Take(1).FirstOrDefault();
                if (requestPath is not null) StartTask(job.Id, token => RelayAsync(job, requestPath, token), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            { store.Finish(job.Id, "FAILED", "invalid_exchange"); Cleanup(job.Id); }
        }
        // Bounded cleanup of cancelled/finished jobs also removes abandoned requests after a restart.
        foreach (var path in Directory.EnumerateFiles(Incoming, "*.job.json").Take(128))
        {
            var id = Path.GetFileName(path).Split('.')[0];
            if (!Id(id)) continue;
            if (store.Get(id)?.State is not ("QUEUED" or "RUNNING")) Cleanup(id);
        }
    }
    private void StartTask(string id, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        requests.Add(id, (action(cancel.Token), cancel));
    }
    private async Task DispatchAsync(SkillAiStore.Job job, string manifest, CancellationToken ct)
    {
        var cfg = SkillAiJson.Read<SkillAiConfiguration>(job.Snapshot, 16384);
        var source = blobs.Existing(job.StagingKey) ?? throw new InvalidDataException("Staged skill is unavailable.");
        var info = new FileInfo(source);
        if (info.Length > 20 * 1024 * 1024 || info.LinkTarget is not null) throw new InvalidDataException("Invalid skill archive.");
        var zip = Path.Combine(Incoming, job.Id + ".zip");
        File.Delete(zip);
        File.Copy(source, zip);
        if (SkillScanCoordinator.HashFile(zip) != job.Sha256) throw new InvalidDataException("Staged digest mismatch.");
        await WriteAsync(manifest, new
        {
            id = job.Id, sha256 = job.Sha256, revision = job.Revision, deadline = job.Deadline,
            model = cfg.Model, provider = deployment.Resolve(cfg.EndpointId).Provider == "anthropic" ? "anthropic" : "openai",
            outputLanguage = cfg.OutputLanguage, maxPromptBytes = cfg.MaxPromptBytes,
            maxOutputTokens = cfg.MaxOutputTokens, maxRequests = cfg.MaxRequestsPerScan
        }, ct);
    }
    private async Task RelayAsync(SkillAiStore.Job job, string path, CancellationToken ct)
    {
        string? reply = null;
        try
        {
            var request = SkillAiJson.Read<RelayRequest>(BoundedFile(path, 1024 * 1024), 1024 * 1024);
            if (request.JobId != job.Id || !Id(request.RequestId) ||
                Path.GetFileName(path) != job.Id + "." + request.RequestId + ".request.json")
                throw new InvalidDataException("Invalid relay identity.");
            reply = Path.Combine(Incoming, job.Id + "." + request.RequestId + ".response.json");
            // A completed response is not re-sent after an API restart.
            if (File.Exists(reply)) { File.Delete(path); return; }
            var body = await gateway.CompleteAsync(job, request.RequestId, request.Body, ct);
            if (store.Get(job.Id)?.State == "RUNNING")
                await WriteAsync(reply, new { status = 200, body }, ct);
        }
        catch (Exception ex)
        {
            var error = ex is InvalidOperationException && ex.Message == "budget_exceeded" ? "budget_exceeded" : "provider_or_policy_failure";
            store.Finish(job.Id, error == "budget_exceeded" ? "BUDGET_EXCEEDED" : "FAILED", error);
            if (reply is not null) await WriteAsync(reply, new { status = 503, body = "{\"error\":{\"message\":\"AI request unavailable\"}}" }, CancellationToken.None);
        }
        finally { File.Delete(path); }
    }
    private async Task ProbeAsync(SkillAiStore.Job job, CancellationToken ct)
    {
        try
        {
            var body = "{\"messages\":[{\"role\":\"user\",\"content\":\"Reply with OK. This is a connection test, not a skill assessment.\"}]}";
            _ = await gateway.CompleteAsync(job, Guid.NewGuid().ToString("N"), body, ct);
            store.Finish(job.Id, "COMPLETED", report: "{\"connectionTest\":true,\"skillAssessment\":false}");
        }
        catch (Exception) { store.Finish(job.Id, "FAILED", "connection_test_failed"); }
    }
    private void Cleanup(string id)
    {
        if (!Id(id)) return;
        foreach (var directory in new[] { Incoming, Outgoing })
            foreach (var file in Directory.EnumerateFiles(directory, id + ".*").Take(512)) File.Delete(file);
    }
}
