using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packory;

public static class SkillAiEndpoints
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record QueueRequest(string Name, string Version, string Sha256, string RequestId, string Reason);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ProbeRequest(string RequestId, string Reason);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record CancelRequest(string Reason);

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/admin/skill-ai");
        group.AddEndpointFilter(async (context, next) =>
        {
            var ctx = context.HttpContext;
            var cfg = ctx.RequestServices.GetRequiredService<Settings>();
            ctx.Response.Headers.CacheControl = "no-store";
            if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
            if (cfg.InstanceRole == "reader" || cfg.RecoveryMaintenance || !cfg.SkillSecurityEnforced)
                return Results.Json(new { error = "AI administration requires an enforcing writer." }, statusCode: 503);
            var origin = ctx.Request.Headers.Origin.ToString();
            if ((origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                origin != new Uri(cfg.PublicBaseUrl).GetLeftPart(UriPartial.Authority) || uri.AbsolutePath != "/")) ||
                ctx.Request.Headers["Sec-Fetch-Site"] == "cross-site") return Results.StatusCode(403);
            try { return await next(context); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or FormatException or
                                       ArgumentException or System.Security.Cryptography.CryptographicException)
            { return Results.BadRequest(new { error = "Invalid AI configuration, exchange or credential keyring." }); }
            catch (InvalidOperationException)
            { return Results.Conflict(new { error = "AI policy, revision or job state changed; refresh before retrying." }); }
            catch (IOException)
            { return Results.Json(new { error = "AI configuration storage is unavailable." }, statusCode: 503); }
        });
        group.MapGet("/settings", (SkillAiStore store, SkillAiDeployment deployment) =>
        {
            var saved = store.Configuration();
            return Results.Ok(new
            {
                configuration = saved.Configuration, hasApiKey = saved.Ciphertext is not null,
                endpoints = deployment.Endpoints.Select(e => new { e.Id, e.Provider, e.BaseUrl, e.External }),
                usage = store.Budget(), scannerVersion = SkillAiDeployment.ScannerVersion,
                scannerCommit = SkillAiDeployment.ScannerCommit, profile = SkillAiDeployment.Profile
            });
        });
        group.MapPut("/settings", async (SkillAiStore store, HttpContext ctx) =>
        {
            var change = await ReadAsync<SkillAiSettingsChange>(ctx);
            await store.PolicyGate.WaitAsync(ctx.RequestAborted);
            try
            {
                var saved = store.Save(change, Access.Actor(ctx));
                return Results.Ok(new { configuration = saved.Configuration, hasApiKey = saved.Ciphertext is not null });
            }
            finally { store.PolicyGate.Release(); }
        }).RequireRateLimiting("publish");
        group.MapPost("/probe", async (SkillAiStore store, HttpContext ctx) =>
        {
            var request = await ReadAsync<ProbeRequest>(ctx);
            await store.PolicyGate.WaitAsync(ctx.RequestAborted);
            try
            {
                var job = store.Queue(null, request.RequestId, Access.Actor(ctx), request.Reason);
                return Results.Accepted("/api/admin/skill-ai/jobs/" + job.Id, SkillAiStore.Summary(job));
            }
            finally { store.PolicyGate.Release(); }
        }).RequireRateLimiting("publish");
        group.MapPost("/assessments", async (SkillAiStore store, SkillSecurityStore security, HttpContext ctx) =>
        {
            var request = await ReadAsync<QueueRequest>(ctx);
            if (!Names.Skill(request.Name ?? "") || !Names.Version(request.Version ?? "")) return Results.BadRequest();
            await store.PolicyGate.WaitAsync(ctx.RequestAborted);
            try
            {
                var revision = security.Get(request.Name!, request.Version!);
                if (revision is null || revision.Sha256 != request.Sha256) return Results.Conflict();
                var job = store.Queue(revision, request.RequestId, Access.Actor(ctx), request.Reason);
                return Results.Accepted("/api/admin/skill-ai/jobs/" + job.Id, SkillAiStore.Summary(job));
            }
            finally { store.PolicyGate.Release(); }
        }).RequireRateLimiting("publish");
        group.MapGet("/assessments", (string? name, string? version, SkillAiStore store) =>
        {
            if ((name is not null || version is not null) && (!Names.Skill(name ?? "") || !Names.Version(version ?? "")))
                return Results.BadRequest();
            return Results.Ok(new { items = store.List(name, version).Select(SkillAiStore.Summary) });
        });
        group.MapGet("/jobs/{id}", (string id, SkillAiStore store) =>
        {
            if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest();
            var job = store.Get(id);
            return job is null ? Results.NotFound() : Results.Ok(new
            {
                job = SkillAiStore.Summary(job),
                report = job.ReportJson is null ? (JsonElement?)null : SkillAiJson.Read<JsonElement>(job.ReportJson, 1024 * 1024)
            });
        });
        group.MapPost("/jobs/{id}/cancel", async (string id, SkillAiStore store, HttpContext ctx) =>
        {
            if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest();
            var request = await ReadAsync<CancelRequest>(ctx);
            await store.PolicyGate.WaitAsync(ctx.RequestAborted);
            try { return store.Cancel(id, Access.Actor(ctx), request.Reason) ? Results.Ok(new { status = "CANCELLED" }) : Results.Conflict(); }
            finally { store.PolicyGate.Release(); }
        }).RequireRateLimiting("publish");
        group.MapGet("/audit", (SkillAiStore store) => Results.Ok(new { items = store.History() }));
    }
    private static async Task<T> ReadAsync<T>(HttpContext ctx)
    {
        if (!ctx.Request.HasJsonContentType()) throw new InvalidDataException("JSON is required.");
        var bytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body, 32768, ctx.RequestAborted);
        return SkillAiJson.Read<T>(new UTF8Encoding(false, true).GetString(bytes), 32768);
    }
}
