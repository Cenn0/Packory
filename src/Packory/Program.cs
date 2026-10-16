using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Packory;

var builder = WebApplication.CreateBuilder(args);
var settings = new Settings();
builder.Configuration.GetSection("Packory").Bind(settings);
// Explicit environment overrides avoid ever requiring secrets in checked-in JSON.
static string? TokenFromEnv(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value) ? null : value;
}
settings.AdminToken = TokenFromEnv("PACKORY_ADMIN_TOKEN") ?? settings.AdminToken;
settings.ReadToken = TokenFromEnv("PACKORY_READ_TOKEN") ?? settings.ReadToken;
settings.WriteToken = TokenFromEnv("PACKORY_WRITE_TOKEN") ?? settings.WriteToken;
settings.DataDirectory = Environment.GetEnvironmentVariable("PACKORY_DATA_DIRECTORY") ?? settings.DataDirectory;
settings.PublicBaseUrl = Environment.GetEnvironmentVariable("PACKORY_PUBLIC_BASE_URL") ?? settings.PublicBaseUrl;
settings.NugetUpstreamUrl = Environment.GetEnvironmentVariable("PACKORY_NUGET_UPSTREAM_URL") ?? settings.NugetUpstreamUrl;
settings.NpmUpstreamUrl = Environment.GetEnvironmentVariable("PACKORY_NPM_UPSTREAM_URL") ?? settings.NpmUpstreamUrl;
settings.NugetRegistrationUrl = Environment.GetEnvironmentVariable("PACKORY_NUGET_REGISTRATION_URL") ?? settings.NugetRegistrationUrl;
if (Environment.GetEnvironmentVariable("PACKORY_RESERVED_NUGET_PREFIXES") is { } reservedNuget)
    settings.ReservedNugetPrefixes = reservedNuget.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
if (Environment.GetEnvironmentVariable("PACKORY_RESERVED_NPM_SCOPES") is { } reservedNpm)
    settings.ReservedNpmScopes = reservedNpm.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
if (Environment.GetEnvironmentVariable("PACKORY_DENIED_NUGET_PREFIXES") is { } deniedNuget)
    settings.DeniedNugetPrefixes = deniedNuget.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
if (Environment.GetEnvironmentVariable("PACKORY_DENIED_NPM_SCOPES") is { } deniedNpm)
    settings.DeniedNpmScopes = deniedNpm.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
if (Environment.GetEnvironmentVariable("PACKORY_PUBLISH_REQUESTS_PER_MINUTE") is { } maxPublish)
    settings.PublishRequestsPerMinute = int.TryParse(maxPublish, out var parsedPublish)
        ? parsedPublish : throw new InvalidOperationException("Invalid PACKORY_PUBLISH_REQUESTS_PER_MINUTE.");
if (bool.TryParse(Environment.GetEnvironmentVariable("PACKORY_ANONYMOUS_READ"), out var anonymousRead))
    settings.AnonymousRead = anonymousRead;
settings.Validate();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = settings.MaxArtifactBytes * 2 + 1024 * 1024);
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = settings.MaxArtifactBytes;
    options.ValueLengthLimit = settings.MaxMetadataBytes;
});
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<BlobStore>();
builder.Services.AddSingleton<ArtifactLocks>();
builder.Services.AddSingleton<ArtifactRecovery>();
builder.Services.AddSingleton<OciUploadStore>();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("publish", context =>
    {
        var configured = context.RequestServices.GetRequiredService<Settings>();
        return RateLimitPartition.GetFixedWindowLimiter(
            Access.RateLimitPartition(context, configured),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = configured.PublishRequestsPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});
builder.Services.AddHttpClient("upstream", client => client.Timeout = TimeSpan.FromSeconds(90))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                 System.Net.DecompressionMethods.Deflate |
                                 System.Net.DecompressionMethods.Brotli,
        // Registry metadata must never redirect requests to an arbitrary address.
        AllowAutoRedirect = false
    });

var app = builder.Build();
app.Services.GetRequiredService<Database>().Initialize();
app.Services.GetRequiredService<ArtifactRecovery>().Reconcile();
app.UseRouting();
app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    try { await next(ctx); }
    catch (PayloadTooLargeException) { if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 413; }
    catch (HttpRequestException) { if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502; }
    catch (TaskCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
    { if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 504; }
    catch (System.Text.Json.JsonException) { if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502; }
    catch (InvalidDataException) { if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502; }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (Database db, ArtifactRecovery recovery) =>
{
    using var connection = db.Open();
    var missing = recovery.MissingHostedCount();
    return missing > 0
        ? Results.Json(new { status = "degraded", missingHostedArtifacts = missing },
            statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(new { status = "ready" });
});
app.MapGet("/api/packages", (string? q, string? kind, int? limit, Settings cfg, Database db, HttpContext ctx) =>
{
    if (Access.Require(ctx, cfg) is { } denied) return denied;
    if (kind is not null && kind is not ("nuget" or "npm" or "skill" or "cargo" or "pypi" or "oci")) return Results.BadRequest(new { error = "Unknown package kind" });
    return Results.Json(new { items = db.Search(q ?? "", kind, limit ?? 50) });
});
app.MapGet("/api/admin/audit", (long? before, int? limit, Settings cfg, Database db, HttpContext ctx) =>
{
    if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
    if (before <= 0) return Results.BadRequest(new { error = "before must be positive" });
    return Results.Json(new { items = db.AuditEvents(before, limit ?? 50) });
});
OciEndpoints.Map(app);
CargoEndpoints.Map(app);
PyPiEndpoints.Map(app);
NuGetEndpoints.Map(app);
NpmEndpoints.Map(app);
SkillEndpoints.Map(app);
app.Run();

public partial class Program { }
