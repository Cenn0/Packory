using System.Text.Json;

namespace Packory;

/// <summary>Administrator-only scan findings and release decisions. Never exposes raw
/// untrusted reports to anonymous callers or the MCP surface.</summary>
public static class SkillSecurityEndpoints
{
    public sealed record DecisionRequest(string Sha256, string Reason);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/security/skills", (Settings settings, SkillSecurityStore security,
            HttpContext ctx, int? limit, int? offset) =>
        {
            if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
            if (limit is < 1 or > 1000 || offset is < 0 or > 10000) return Results.BadRequest();
            ctx.Response.Headers.CacheControl = "no-store";
            var size = limit ?? 100;
            var start = offset ?? 0;
            var page = security.List(size + 1, start);
            return Results.Ok(new
            {
                items = page.Take(size).Select(Summary).ToArray(),
                nextOffset = page.Count > size ? start + size : (int?)null
            });
        });

        app.MapGet("/api/security/skills/{name}/{version}", (string name, string version,
            Settings settings, SkillSecurityStore security, HttpContext ctx) =>
        {
            if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
            if (!Names.Skill(name) || !Names.Version(version)) return Results.BadRequest();
            var item = security.Get(name, version);
            if (item is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                skill = Summary(item),
                report = item.ReportJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(item.ReportJson),
                history = security.History(name, version)
            });
        });

        app.MapGet("/api/security/skills/{name}/{version}/source", SkillReviewSource.ListAsync);
        app.MapGet("/api/security/skills/{name}/{version}/source/file", SkillReviewSource.FileAsync);

        app.MapPost("/api/security/skills/{name}/{version}/approve", async (
            string name, string version, Settings settings, SkillSecurityStore security,
            SkillScanCoordinator scans, HttpContext ctx) =>
        {
            if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
            if (settings.InstanceRole == "reader") return Results.StatusCode(503);
            var request = await ReadDecision(ctx);
            if (request is null || !Names.Skill(name) || !Names.Version(version))
                return Results.BadRequest(new { error = "Valid digest and reason are required." });
            try
            {
                var approved = await scans.ApproveAsync(name, version, request.Sha256,
                    Access.Actor(ctx), request.Reason, ctx.RequestAborted);
                return approved ? Results.Ok(new { status = "APPROVED" })
                    : Results.Conflict(new { error = "Revision, digest or state changed." });
            }
            catch (IOException)
            {
                return Results.Json(new { error = "Publication recovery required." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        }).RequireRateLimiting("publish");

        app.MapPost("/api/security/skills/{name}/{version}/decline", async (
            string name, string version, Settings settings, SkillSecurityStore security,
            HttpContext ctx) =>
        {
            if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
            if (settings.InstanceRole == "reader") return Results.StatusCode(503);
            var request = await ReadDecision(ctx);
            if (request is null || !Names.Skill(name) || !Names.Version(version))
                return Results.BadRequest(new { error = "Valid digest and reason are required." });
            foreach (var previous in new[] { "NEEDS_REVIEW", "QUARANTINED", "SCAN_FAILED", "SCAN_INCOMPLETE" })
            {
                if (security.TryTransition(name, version, request.Sha256, previous,
                    "DECLINED", Access.Actor(ctx), request.Reason))
                    return Results.Ok(new { status = "DECLINED" });
            }
            return Results.Conflict(new { error = "Revision, digest or state changed." });
        }).RequireRateLimiting("publish");

        app.MapPost("/api/security/skills/{name}/{version}/revoke", async (
            string name, string version, Settings settings, SkillSecurityStore security,
            HttpContext ctx) =>
        {
            if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
            if (settings.InstanceRole == "reader") return Results.StatusCode(503);
            var request = await ReadDecision(ctx);
            if (request is null || !Names.Skill(name) || !Names.Version(version))
                return Results.BadRequest();
            return security.TryTransition(name, version, request.Sha256, "APPROVED",
                "REVOKED", Access.Actor(ctx), request.Reason)
                ? Results.Ok(new { status = "REVOKED" }) : Results.Conflict();
        }).RequireRateLimiting("publish");

        app.MapPost("/api/security/skills/{name}/{version}/rescan", async (
            string name, string version, Settings settings, SkillSecurityStore security,
            HttpContext ctx) =>
        {
            if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
            if (settings.InstanceRole == "reader") return Results.StatusCode(503);
            var request = await ReadDecision(ctx);
            if (request is null || !Names.Skill(name) || !Names.Version(version))
                return Results.BadRequest();
            foreach (var previous in new[] { "APPROVED", "NEEDS_REVIEW", "QUARANTINED", "SCAN_FAILED", "SCAN_INCOMPLETE", "REVOKED", "DECLINED" })
            {
                if (security.TryTransition(name, version, request.Sha256, previous,
                    "QUEUED", Access.Actor(ctx), request.Reason))
                    return Results.Accepted(value: new { status = "QUEUED" });
            }
            return Results.Conflict(new { error = "Revision, digest or state changed." });
        }).RequireRateLimiting("publish");

        // Existing installations already have immutable Skill ZIPs in the catalog.
        // An explicit admin-only backfill copies and revalidates them into the new
        // quarantine namespace. No legacy revision is grandfathered as APPROVED.
        app.MapPost("/api/security/skills/backfill", async (
            Settings settings, IPackageDatabase database, BlobStore blobs,
            SkillSecurityStore security, ArtifactLocks locks, HttpContext ctx, int? limit) =>
        {
            if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
            if (settings.InstanceRole == "reader" || !settings.SkillSecurityEnforced)
                return Results.Conflict(new { error = "Writer with skill enforcement is required." });
            if (limit is < 1 or > 100) return Results.BadRequest(new { error = "limit must be 1..100." });
            var entries = database.Skills().Where(skill =>
                    Names.Skill(skill.Name) && Names.Version(skill.Version) &&
                    security.Get(skill.Name, skill.Version) is null)
                .Take(limit ?? 25).ToArray();
            var queued = 0;
            var skipped = 0;
            var maxBytes = Math.Min(settings.MaxArtifactBytes, 20L * 1024 * 1024);
            foreach (var skill in entries)
            {
                using var gate = await locks.AcquireAsync("skill", skill.Name, skill.Version, ctx.RequestAborted);
                if (security.Get(skill.Name, skill.Version) is not null) continue;
                var source = blobs.Existing(SkillEndpoints.SkillKey(skill.Name, skill.Version));
                if (source is null || new FileInfo(source).Length > maxBytes)
                {
                    skipped++;
                    continue;
                }
                var bytes = await File.ReadAllBytesAsync(source, ctx.RequestAborted);
                SkillEndpoints.SkillManifest manifest;
                try { manifest = SkillEndpoints.InspectZip(bytes); }
                catch (InvalidDataException) { skipped++; continue; }
                if (manifest.Name != skill.Name)
                {
                    skipped++;
                    continue;
                }
                var digest = Convert.ToHexStringLower(
                    System.Security.Cryptography.SHA256.HashData(bytes));
                var stagingKey = $"skill-staging/{digest}.zip";
                var existing = blobs.Existing(stagingKey);
                if (existing is not null &&
                    SkillScanCoordinator.HashFile(existing) != digest)
                    return Results.StatusCode(503);
                if (existing is null && !await blobs.PutAsync(stagingKey,
                    new MemoryStream(bytes, false), maxBytes, ctx.RequestAborted))
                    return Results.Conflict(new { error = "Staging collision; investigate storage." });
                if (security.TryStage(skill.Name, skill.Version, skill.Description, digest, stagingKey))
                    queued++;
            }
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { queued, skipped, inspected = entries.Length,
                remaining = database.Skills().Count(skill =>
                    Names.Skill(skill.Name) && Names.Version(skill.Version) &&
                    security.Get(skill.Name, skill.Version) is null) });
        }).RequireRateLimiting("publish");

        app.MapGet("/api/security/status", (Settings settings, SkillSecurityStore security,
            HttpContext ctx) =>
        {
            if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                enforcement = settings.SkillSecurityEnforced,
                scanner = "NVIDIA SkillSpector",
                mode = "static-only / offline sidecar; manual approval",
                queued = security.List(1000).Count(r => r.State == "QUEUED")
            });
        });
    }

    private static object Summary(SkillSecurityStore.Revision item) => new
    {
        item.Name, item.Version, item.Description, item.Sha256,
        item.State, item.RiskScore, item.Recommendation, item.UpdatedAt
    };

    private static async Task<DecisionRequest?> ReadDecision(HttpContext ctx)
    {
        DecisionRequest? value;
        try { value = await ctx.Request.ReadFromJsonAsync<DecisionRequest>(ctx.RequestAborted); }
        catch (JsonException) { return null; }
        return value is not null &&
            value.Sha256 is { Length: 64 } &&
            value.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            value.Reason?.Length is >= 3 and <= 512 ? value : null;
    }
}
