using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class SkillDispatcherReviewTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "packory-dispatch-review-" + Guid.NewGuid().ToString("N"));
    private readonly SkillSecurityStore security;
    private readonly Database db;
    private readonly SkillScanCoordinator coordinator;
    private const string Digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static string ValidResult => System.Text.Json.JsonSerializer.Serialize(new {
        sha256 = Digest, exit_code = 0, report = new { execution_successful = true,
            analysis_completeness = new { is_complete = true },
            metadata = new { llm_requested = false },
            risk_assessment = new { score = 0, severity = "LOW", recommendation = "SAFE" },
            issues = Array.Empty<object>() }
    });

    public SkillDispatcherReviewTests()
    {
        var settings = new Settings { DataDirectory = root };
        db = new Database(settings); db.Initialize();
        security = new SkillSecurityStore(settings); security.Initialize();
        coordinator = new SkillScanCoordinator(settings, security, db, new BlobStore(settings),
            NullLogger<SkillScanCoordinator>.Instance, new PostgresSingleWriterLease(settings));
        Directory.CreateDirectory(coordinator.Incoming);
        Directory.CreateDirectory(coordinator.Outgoing);
    }

    private Task Tick() => (Task)typeof(SkillScanCoordinator).GetMethod("TickAsync",
        BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(coordinator, [CancellationToken.None])!;

    [Fact]
    public async Task OldQueuedRevisionIsNotHiddenByThousandRecentCompletedRevisions()
    {
        Assert.True(security.TryStage("old-pending", "1.0.0", "Synthetic", Digest, "skill-staging/missing.zip"));
        using (var connection = db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE skill_security_revisions SET updated_at='2000-01-01T00:00:00Z';
                WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<1001)
                INSERT INTO skill_security_revisions(name,version,description,sha256,staging_key,state,updated_at)
                SELECT 'recent-' || x,'1.0.0','Synthetic',@digest,'skill-staging/missing.zip','NEEDS_REVIEW','2026-01-01T00:00:00Z' FROM n;
                """;
            command.Parameters.AddWithValue("digest", Digest);
            command.ExecuteNonQuery();
        }
        await Tick();
        Assert.Equal("SCAN_FAILED", security.Get("old-pending", "1.0.0")!.State);
    }

    [Theory]
    [InlineData("oversized")]
    [InlineData("invalid-utf8")]
    [InlineData("symlink")]
    public async Task InvalidWorkerFileFailsOnlyItsRevisionAndDoesNotBlockOtherJobs(string kind)
    {
        Assert.True(security.TryStage("bad-report", "1.0.0", "Synthetic", Digest, "skill-staging/missing.zip"));
        var item = security.Get("bad-report", "1.0.0")!;
        var report = Path.Combine(coordinator.Outgoing, SkillScanCoordinator.JobId(item) + ".json");
        if (kind == "oversized") await File.WriteAllTextAsync(report, new string(' ', 1024 * 1024 + 1));
        if (kind == "invalid-utf8") await File.WriteAllBytesAsync(report,
            [.. Encoding.UTF8.GetBytes(ValidResult[..^1] + ",\"note\":\""), 0xc3, 0x28, .. Encoding.UTF8.GetBytes("\"}")]);
        if (kind == "symlink")
        {
            var target = Path.Combine(root, "operator-owned-evidence.txt");
            await File.WriteAllTextAsync(target, ValidResult);
            File.CreateSymbolicLink(report, target);
        }
        Assert.True(security.TryStage("next-report", "1.0.0", "Synthetic", Digest, "skill-staging/missing.zip"));
        await Tick();
        Assert.Equal("SCAN_FAILED", security.Get("bad-report", "1.0.0")!.State);
        Assert.Equal("SCAN_FAILED", security.Get("next-report", "1.0.0")!.State);
        Assert.False(File.Exists(report));
        if (kind == "symlink") Assert.Equal(ValidResult,
            await File.ReadAllTextAsync(Path.Combine(root, "operator-owned-evidence.txt")));
    }

    [Fact]
    public async Task DispatcherAndApprovalCannotWriteWithoutPostgresLease()
    {
        var settings = new Settings { DataDirectory = root, MetadataProvider = "postgresql" };
        // No connection was acquired: deterministic lease-unavailable fixture,
        // not a claim that a real PostgreSQL partition was exercised.
        using var lease = new PostgresSingleWriterLease(settings);
        Assert.False(lease.IsHeld());
        Assert.True(security.TryStage("pending", "1.0.0", "Synthetic", Digest, "skill-staging/missing.zip"));
        using var stopped = new SkillScanCoordinator(settings, security, db, new BlobStore(settings),
            NullLogger<SkillScanCoordinator>.Instance, lease);
        await (Task)typeof(SkillScanCoordinator).GetMethod("TickAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(stopped, [CancellationToken.None])!;
        Assert.Equal("QUEUED", security.Get("pending", "1.0.0")!.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => stopped.ApproveAsync("pending", "1.0.0",
            Digest, "synthetic-operator", "Synthetic review", CancellationToken.None));
    }

    public void Dispose()
    {
        coordinator.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
