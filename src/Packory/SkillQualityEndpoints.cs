namespace Packory;

public static class SkillQualityEndpoints
{
    // The report covers a global Skill revision, so a grant limited to a
    // different feed/namespace must not become a global read credential.
    public static IResult? RequireRead(HttpContext context, Settings settings) =>
        Access.RequireDashboardGlobalRead(context, settings);

    public static void Map(WebApplication app) =>
        app.MapGet("/api/v1/skills/{name}/{version}/quality", (string name, string version,
            Settings settings, IPackageDatabase db, BlobStore blobs,
            SkillSecurityStore security, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (RequireRead(context, settings) is { } denied) return denied;
            if (!Names.Skill(name) || !Names.Version(version)) return Results.BadRequest();
            var report = SkillQualityAssessment.AssessPublished(name, version, db, blobs,
                security, settings, context.RequestAborted);
            // Revoked, absent and digest-mismatched revisions have the same response.
            return report is null ? Results.NotFound() : Results.Json(report);
        }).RequireRateLimiting("skill-quality");
}
