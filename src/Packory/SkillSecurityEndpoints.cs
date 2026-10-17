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
            HttpContext ctx, int? limit) =>
        {
            if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                items = security.List(limit ?? 100).Select(Summary)
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
            foreach (var previous in new[] { "APPROVED", "NEEDS_REVIEW", "QUARANTINED", "SCAN_FAILED", "REVOKED" })
            {
                if (security.TryTransition(name, version, request.Sha256, previous,
                    "QUEUED", Access.Actor(ctx), request.Reason))
                    return Results.Accepted(value: new { status = "QUEUED" });
            }
            return Results.Conflict(new { error = "Revision, digest or state changed." });
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
            value.Sha256.Length == 64 &&
            value.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            value.Reason?.Length is >= 3 and <= 512 ? value : null;
    }
}
