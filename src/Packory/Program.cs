using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Packory;
using System.Text;
using System.Security.Cryptography;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
var settings = new Settings();
builder.Configuration.GetSection("Packory").Bind(settings);
// Explicit environment overrides avoid ever requiring secrets in checked-in JSON.
static string? TokenFromEnv(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value) ? null : value;
}
settings.OidcAuthority = Environment.GetEnvironmentVariable("PACKORY_OIDC_AUTHORITY") ?? settings.OidcAuthority;
settings.OidcAudience = Environment.GetEnvironmentVariable("PACKORY_OIDC_AUDIENCE") ?? settings.OidcAudience;
settings.AdminToken = TokenFromEnv("PACKORY_ADMIN_TOKEN") ?? settings.AdminToken;
settings.ReadToken = TokenFromEnv("PACKORY_READ_TOKEN") ?? settings.ReadToken;
settings.WriteToken = TokenFromEnv("PACKORY_WRITE_TOKEN") ?? settings.WriteToken;
if (Environment.GetEnvironmentVariable("PACKORY_WRITE_NAMESPACES") is { } grants)
    settings.WriteNamespaces = grants.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
if (bool.TryParse(Environment.GetEnvironmentVariable("PACKORY_OIDC_REQUIRE_NAMESPACE_GRANTS"), out var requireGrants))
    settings.RequireOidcNamespaceGrants = requireGrants;
settings.DataDirectory = Environment.GetEnvironmentVariable("PACKORY_DATA_DIRECTORY") ?? settings.DataDirectory;
settings.MetadataProvider = Environment.GetEnvironmentVariable("PACKORY_METADATA_PROVIDER") ?? settings.MetadataProvider;
settings.InstanceRole = Environment.GetEnvironmentVariable("PACKORY_INSTANCE_ROLE") ?? settings.InstanceRole;
settings.BlobProvider = Environment.GetEnvironmentVariable("PACKORY_BLOB_PROVIDER") ?? settings.BlobProvider;
settings.S3Endpoint = Environment.GetEnvironmentVariable("PACKORY_S3_ENDPOINT") ?? settings.S3Endpoint;
settings.S3Bucket = Environment.GetEnvironmentVariable("PACKORY_S3_BUCKET") ?? settings.S3Bucket;
settings.S3Region = Environment.GetEnvironmentVariable("PACKORY_S3_REGION") ?? settings.S3Region;
settings.S3KeyPrefix = Environment.GetEnvironmentVariable("PACKORY_S3_KEY_PREFIX") ?? settings.S3KeyPrefix;
settings.S3AccessKey = Environment.GetEnvironmentVariable("PACKORY_S3_ACCESS_KEY") ?? settings.S3AccessKey;
settings.S3SecretKey = Environment.GetEnvironmentVariable("PACKORY_S3_SECRET_KEY") ?? settings.S3SecretKey;
if (bool.TryParse(Environment.GetEnvironmentVariable("PACKORY_S3_ALLOW_LOOPBACK_HTTP"), out var s3Loopback))
    settings.S3AllowLoopbackHttp = s3Loopback;
settings.PostgresConnectionString = Environment.GetEnvironmentVariable("PACKORY_POSTGRES_CONNECTION_STRING") ?? settings.PostgresConnectionString;
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
if (Environment.GetEnvironmentVariable("PACKORY_MAX_BLOB_STORAGE_BYTES") is { } maxBlobStorage)
    settings.MaxBlobStorageBytes = long.TryParse(maxBlobStorage, out var parsedBlobCapacity)
        ? parsedBlobCapacity : throw new InvalidOperationException("Invalid PACKORY_MAX_BLOB_STORAGE_BYTES.");
if (Environment.GetEnvironmentVariable("PACKORY_PUBLISH_REQUESTS_PER_MINUTE") is { } maxPublish)
    settings.PublishRequestsPerMinute = int.TryParse(maxPublish, out var parsedPublish)
        ? parsedPublish : throw new InvalidOperationException("Invalid PACKORY_PUBLISH_REQUESTS_PER_MINUTE.");
if (bool.TryParse(Environment.GetEnvironmentVariable("PACKORY_OCI_AUTHENTICATED_PING"), out var ociPing))
    settings.OciAuthenticatedPing = ociPing;
if (bool.TryParse(Environment.GetEnvironmentVariable("PACKORY_STRICT_DEPLOYMENT"), out var strictDeployment))
    settings.StrictDeployment = strictDeployment;
if (bool.TryParse(Environment.GetEnvironmentVariable("PACKORY_RECOVERY_MAINTENANCE"), out var maintenance))
    settings.RecoveryMaintenance = maintenance;
if (bool.TryParse(Environment.GetEnvironmentVariable("PACKORY_ANONYMOUS_READ"), out var anonymousRead))
    settings.AnonymousRead = anonymousRead;
if (bool.TryParse(Environment.GetEnvironmentVariable("PACKORY_SKILL_SECURITY_ENFORCED"), out var skillSecurity))
    settings.SkillSecurityEnforced = skillSecurity;
if (bool.TryParse(Environment.GetEnvironmentVariable("PACKORY_MCP_ENABLED"), out var mcpEnabled))
    settings.McpEnabled = mcpEnabled;
if (bool.TryParse(Environment.GetEnvironmentVariable("PACKORY_VALIDATION_ENABLED"), out var validationEnabled))
    settings.ValidationEnabled = validationEnabled;
settings.ValidationExportDirectory = Environment.GetEnvironmentVariable("PACKORY_VALIDATION_EXPORT_DIRECTORY") ?? settings.ValidationExportDirectory;
settings.ValidationVerifierPublicKeyPath = Environment.GetEnvironmentVariable("PACKORY_VALIDATION_VERIFIER_PUBLIC_KEY_PATH") ?? settings.ValidationVerifierPublicKeyPath;
settings.ValidationVerifierKeyId = Environment.GetEnvironmentVariable("PACKORY_VALIDATION_VERIFIER_KEY_ID") ?? settings.ValidationVerifierKeyId;
settings.ValidationAudience = Environment.GetEnvironmentVariable("PACKORY_VALIDATION_AUDIENCE") ?? settings.ValidationAudience;
settings.ValidationTenant = Environment.GetEnvironmentVariable("PACKORY_VALIDATION_TENANT") ?? settings.ValidationTenant;
if (Environment.GetEnvironmentVariable("PACKORY_VALIDATION_APPROVED_ALIASES") is { } validationAliases)
    settings.ValidationApprovedAliases = validationAliases.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
if (Environment.GetEnvironmentVariable("PACKORY_VALIDATION_APPROVED_EXPORTS") is { } validationExports)
    settings.ValidationApprovedExports = validationExports.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
if (Environment.GetEnvironmentVariable("PACKORY_VALIDATION_REVOKED_KEY_IDS") is { } revokedKeys)
    settings.ValidationRevokedKeyIds = revokedKeys.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
settings.Validate();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = settings.MaxArtifactBytes * 2 + 1024 * 1024);
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = settings.MaxArtifactBytes;
    options.ValueLengthLimit = settings.MaxMetadataBytes;
});
if (settings.OidcEnabled)
{
    // The JWT middleware validates signature, issuer, audience and lifetime using
    // the HTTPS OpenID Connect discovery document and signing keys.
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = settings.OidcAuthority;
            options.Audience = settings.OidcAudience;
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };
        });
}
builder.Services.AddSingleton(settings);
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ValidationEvidenceStore>();
builder.Services.AddSingleton<ValidationMcpQuota>();
builder.Services.AddSingleton<IPackageDatabase>(provider =>
{
    var effective = provider.GetRequiredService<Settings>();
    return effective.MetadataProvider == "postgresql"
        ? new PostgresDatabase(effective) : new Database(effective);
});
builder.Services.AddSingleton<BlobStore>(provider =>
{
    var cfg = provider.GetRequiredService<Settings>();
    return cfg.BlobProvider == "s3" ? new S3BlobStore(cfg) : new BlobStore(cfg);
});
builder.Services.AddSingleton<ArtifactLocks>();
builder.Services.AddSingleton<ArtifactRecovery>();
builder.Services.AddSingleton<ArtifactIntegrityScanner>();
builder.Services.AddSingleton<PublicationRecoveryService>();
builder.Services.AddSingleton<PostgresSingleWriterLease>();
builder.Services.AddSingleton<OciUploadStore>();
builder.Services.AddSingleton<SkillSecurityStore>();
builder.Services.AddSingleton<McpRegistryStore>();
builder.Services.AddSingleton<Microsoft.Extensions.Options.IConfigureOptions<McpServerOptions>, PackoryMcpOptionsConfigurator>();
builder.Services.AddSingleton<SkillScanCoordinator>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<SkillScanCoordinator>());
if (settings.McpEnabled)
{
    var mcpBuilder = builder.Services.AddMcpServer()
        .WithHttpTransport(options =>
        {
            options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients;
        })
        .WithTools<PackoryMcpTools>()
        .WithTools<PackorySkillQualityTools>()
        .WithTools<McpRegistryMcpTools>()
        .WithResources<PackoryMcpResources>()
        .WithResources<McpRegistryMcpResources>()
        .WithPrompts<PackoryMcpPrompts>();
    if (settings.ValidationEnabled)
        mcpBuilder.WithTools<PackoryValidationMcpTools>();
}
builder.Services.AddRateLimiter(options =>
{
    // Shared zero-queue request budget for expensive Streamable HTTP MCP reads.
    // The authentication gate runs before the rate-limit middleware.
    options.AddPolicy("mcp", _ =>
        RateLimitPartition.GetFixedWindowLimiter("mcp-shared",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300, Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0, AutoReplenishment = true
            }));
    // Public quality reads cannot consume authenticated MCP transport capacity.
    // Partitions are bounded credential roles, never arbitrary token strings.
    options.AddPolicy("skill-quality", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            "skill-quality-" + Access.RateLimitPartition(context,
                context.RequestServices.GetRequiredService<Settings>()),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60, Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0, AutoReplenishment = true
            }));
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
    // A single in-process budget, independent of untrusted caller attributes.
    // Integrity verification can hash large blobs and is maintenance-only.
    options.AddPolicy("integrity-scan", _ =>
        RateLimitPartition.GetFixedWindowLimiter("integrity-operator",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 2,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
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
var activeSettings = app.Services.GetRequiredService<Settings>();
var startupLease = app.Services.GetRequiredService<PostgresSingleWriterLease>();
var readerInstance = activeSettings.InstanceRole == "reader";
if (!readerInstance) startupLease.Acquire();
app.Lifetime.ApplicationStopped.Register(startupLease.Dispose);
if (!readerInstance)
{
    var metadata = app.Services.GetRequiredService<IPackageDatabase>();
    if (activeSettings.RecoveryMaintenance)
    {
        // The maintenance process must never create/migrate tables or
        // automatically clear publication intents / move orphan bytes.
        // Operators repair incompatible schemas through a separate reviewed
        // offline procedure, not implicit application startup.
        if (!metadata.IsHealthy())
            throw new InvalidOperationException(
                "Recovery maintenance requires an initialized compatible metadata schema.");
    }
    else
    {
        metadata.Initialize();
        app.Services.GetRequiredService<SkillSecurityStore>().Initialize();
        app.Services.GetRequiredService<McpRegistryStore>().Initialize();
        app.Services.GetRequiredService<ArtifactRecovery>().Reconcile();
    }
}
else if (!app.Services.GetRequiredService<IPackageDatabase>().IsHealthy())
{
    throw new InvalidOperationException("Read-only replica requires an initialized shared metadata database.");
}
app.UseRouting();
// Authenticate before applying the MCP token/OIDC scope gate.
if (settings.OidcEnabled) app.UseAuthentication();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/mcp"))
    {
        ctx.Response.Headers.CacheControl = "no-store";
        // Explicit Origin validation plus mandatory bearer auth for remote MCP.
        var origin = ctx.Request.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin))
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) ||
                !Uri.TryCreate(activeSettings.Base, UriKind.Absolute, out var publicUri) ||
                originUri.GetLeftPart(UriPartial.Authority) != publicUri.GetLeftPart(UriPartial.Authority))
            {
                ctx.Response.StatusCode = 403;
                return;
            }
        }
        // NuGet Basic credentials and X-NuGet-ApiKey are intentionally
        // supported elsewhere, but must never authenticate the remote MCP
        // endpoint. MCP always needs an explicit Bearer access token.
        if (!ctx.Request.Headers.Authorization.ToString()
            .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = McpOAuthResourceMetadata.Challenge(activeSettings);
            return;
        }
        if (Access.RequireGlobalMcp(ctx, activeSettings) is { } denied)
        {
            // Invalid Bearer tokens require the same OAuth discovery hint as
            // absent credentials. Scoped-token 403 must not be misreported
            // as a challenge to obtain broader permissions.
            if (denied is Microsoft.AspNetCore.Http.IStatusCodeHttpResult
                { StatusCode: StatusCodes.Status401Unauthorized })
                ctx.Response.Headers.WWWAuthenticate =
                    McpOAuthResourceMetadata.Challenge(activeSettings);
            await denied.ExecuteAsync(ctx);
            return;
        }
    }
    if (HttpMethods.IsGet(ctx.Request.Method) &&
        ctx.Request.Path.StartsWithSegments("/api/v1/skills") &&
        ctx.Request.Path.Value?.EndsWith("/quality", StringComparison.OrdinalIgnoreCase) == true)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        // Reject unauthorized reads before spending a role's quality budget.
        if (SkillQualityEndpoints.RequireRead(ctx, activeSettings) is { } qualityDenied)
        {
            await qualityDenied.ExecuteAsync(ctx);
            return;
        }
    }
    await next(ctx);
});
app.Use(async (ctx, next) =>
{
    if (readerInstance)
    {
        // GET is not universally read-only: NuGet/npm read-through mirrors write
        // upstream metadata and artifacts. Block those endpoints fail-closed.
        var path = ctx.Request.Path;
        var method = ctx.Request.Method;
        var safe = (method is "GET" or "HEAD") &&
            !path.StartsWithSegments("/nuget") &&
            !path.StartsWithSegments("/npm") &&
            !path.StartsWithSegments("/api/admin") &&
            !(path.StartsWithSegments("/v2") &&
              path.Value?.Contains("/blobs/uploads", StringComparison.Ordinal) == true);
        if (!safe)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.Headers.RetryAfter = "60";
            return;
        }
    }
    // A lost PostgreSQL lease must fail closed, never permit a second writer.
    if (!readerInstance && (ctx.Request.Method is "POST" or "PUT" or "PATCH" or "DELETE") && !startupLease.IsHeld())
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }
    var integrityRead = HttpMethods.IsGet(ctx.Request.Method) &&
        string.Equals(ctx.Request.Path.Value, "/api/admin/integrity/scan", StringComparison.OrdinalIgnoreCase);
    // Authenticate BEFORE the expensive-tool rate limiter. Otherwise anonymous
    // callers can exhaust its tiny shared operator quota, denying real operators.
    if (integrityRead && Access.RequireAdmin(ctx, activeSettings) is { } rejected)
    {
        await rejected.ExecuteAsync(ctx);
        return;
    }
    if (activeSettings.RecoveryMaintenance &&
        !ctx.Request.Path.StartsWithSegments("/api/admin/publications") &&
        !ctx.Request.Path.StartsWithSegments("/health") && !integrityRead)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }
    await next(ctx);
});
app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    try { await next(ctx); }
    catch (Amazon.S3.AmazonS3Exception) { if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 503; }
    catch (PayloadTooLargeException) { if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 413; }
    catch (StorageQuotaExceededException) { if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 507; }
    catch (HttpRequestException) { if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502; }
    catch (TaskCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
    { if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 504; }
    catch (System.Text.Json.JsonException) { if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502; }
    catch (InvalidDataException) { if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 502; }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (IPackageDatabase db, ArtifactRecovery recovery) =>
{
    if (activeSettings.RecoveryMaintenance || (!readerInstance && !startupLease.IsHeld()) || !db.IsHealthy())
        return Results.Json(new { status = "maintenance-or-lease-lost" },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    var missing = recovery.MissingHostedCount();
    var unresolved = recovery.StaleUnresolvedCount();
    return missing > 0 || unresolved > 0
        ? Results.Json(new { status = "degraded", missingHostedArtifacts = missing,
            stalePendingPublications = unresolved },
            statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(new { status = "ready" });
});
app.MapGet("/api/packages", (string? q, string? kind, int? limit, Settings cfg, IPackageDatabase db, SkillSecurityStore security, HttpContext ctx) =>
{
    if (Access.RequireGlobalRaw(ctx, cfg) is { } denied) return denied;
    if (kind is not null && kind is not ("nuget" or "npm" or "skill" or "cargo" or "pypi" or "oci" or "raw" or "maven" or "go")) return Results.BadRequest(new { error = "Unknown package kind" });
    return Results.Json(new { items = db.Search(q ?? "", kind, limit ?? 50)
        .Where(x => x.Kind != "skill" || !cfg.SkillSecurityEnforced ||
                    security.IsApproved(x.Name, x.Version)).ToList() });
});
app.MapGet("/api/admin/operations/status", (Settings cfg, IPackageDatabase db,
    ArtifactRecovery recovery, HttpContext ctx) =>
{
    if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new
    {
        instanceRole = cfg.InstanceRole,
        strictDeploymentChecksEnabled = cfg.StrictDeployment,
        metadataBackend = cfg.MetadataProvider, blobBackend = cfg.BlobProvider,
        pendingPublicationCount = db.PendingPublications().Count,
        staleUnresolvedCount = recovery.StaleUnresolvedCount(),
        missingHostedCount = recovery.MissingHostedCount()
    });
});
app.MapGet("/api/admin/audit", (long? before, int? limit, Settings cfg, IPackageDatabase db, HttpContext ctx) =>
{
    if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
    if (before <= 0) return Results.BadRequest(new { error = "before must be positive" });
    return Results.Json(new { items = db.AuditEvents(before, limit ?? 50) });
});
app.MapPost("/api/admin/cache/mutable/evict", (Settings cfg, BlobStore blobs, HttpContext ctx) =>
{
    if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
    if (cfg.BlobProvider != "filesystem")
        return Results.Conflict(new { error = "Local metadata eviction is only supported with filesystem storage" });
    return Results.Ok(new { evictedFiles = blobs.EvictMutableMetadata() });
}).RequireRateLimiting("publish");
app.MapPost("/api/admin/tokens", async (Settings cfg, IPackageDatabase db, HttpContext ctx) =>
{
    if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
    ManagedTokenRequest? input;
    try { input = await ctx.Request.ReadFromJsonAsync<ManagedTokenRequest>(ctx.RequestAborted); }
    catch (System.Text.Json.JsonException) { return Results.BadRequest(new { error = "Invalid token request" }); }
    if (input is null || input.Role is not ("read" or "write") || input.ExpiresInHours is < 1 or > 2160 ||
        (input.Role == "write" && (input.Namespace is null ||
                                   !(Access.ValidNamespaceGrant(input.Namespace) ||
                                     (Access.ValidFeedGrant(input.Namespace) &&
                                      input.Namespace.EndsWith(":publish", StringComparison.Ordinal))))) ||
        (input.Role == "read" && input.Namespace is not null &&
         !(Access.ValidFeedGrant(input.Namespace) &&
           input.Namespace.EndsWith(":read", StringComparison.Ordinal))))
        return Results.BadRequest(new { error = "Role, namespace or expiry is invalid" });
    if (input.Namespace is { } feedGrant && Access.ValidFeedGrant(feedGrant))
    {
        var feedId = feedGrant.Split(':')[1];
        if (db.Feed(feedId) is not
            { Kind: "nuget" or "raw" or "maven", Mode: "hosted", Visibility: "private", Enabled: true })
            return Results.BadRequest(new { error = "Feed grants require an active private hosted feed" });
    }
    var id = Guid.NewGuid().ToString("N");
    var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    var expiry = DateTimeOffset.UtcNow.AddHours(input.ExpiresInHours).ToString("O");
    db.CreateManagedToken(new ManagedTokenRecord(id, hash, input.Role, input.Namespace, expiry));
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Json(new { id, token = secret, expiresAt = expiry },
        statusCode: StatusCodes.Status201Created);
}).RequireRateLimiting("publish");
app.MapDelete("/api/admin/tokens/{id}", (string id, Settings cfg, IPackageDatabase db, HttpContext ctx) =>
{
    if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
    if (!Guid.TryParseExact(id, "N", out _)) return Results.BadRequest(new { error = "Invalid token id" });
    return db.RevokeManagedToken(id) ? Results.NoContent() : Results.NotFound();
}).RequireRateLimiting("publish");
// Maintenance-only, administrator-authenticated, bounded inspection of
// catalog references. It never marks a system as certified or deletes bytes.
app.MapGet("/api/admin/integrity/scan", async (int? after, int? limit, Settings cfg,
    ArtifactIntegrityScanner inspector, HttpContext ctx) =>
{
    if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
    ctx.Response.Headers.CacheControl = "no-store";
    if (!cfg.RecoveryMaintenance)
        return Results.Conflict(new { error = "Offline recovery maintenance mode is required" });
    if (!startupLease.IsHeld())
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    if (after is < 0 || limit is < 1 or > 10)
        return Results.BadRequest(new { error = "after must be nonnegative and limit 1..10" });
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
    timeout.CancelAfter(TimeSpan.FromSeconds(45));
    try
    {
        var page = await inspector.ScanAsync(after ?? 0, limit ?? 5, timeout.Token);
        return Results.Ok(page);
    }
    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
    {
        return Results.Json(new { error = "Integrity scan timed out; no acceptance result" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("integrity-scan");

app.MapGet("/api/admin/publications/pending", (Settings cfg, PublicationRecoveryService recovery, HttpContext ctx) =>
{
    if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
    ctx.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { items = recovery.Inspect() });
});
app.MapPost("/api/admin/publications/reconcile", async (Settings cfg, PublicationRecoveryService recovery,
    HttpContext ctx) =>
{
    if (Access.RequireAdmin(ctx, cfg) is { } denied) return denied;
    if (!cfg.RecoveryMaintenance)
        return Results.Conflict(new { error = "Exclusive maintenance mode is required" });
    RecoveryReconcileRequest? request;
    try { request = await ctx.Request.ReadFromJsonAsync<RecoveryReconcileRequest>(ctx.RequestAborted); }
    catch (System.Text.Json.JsonException) { return Results.BadRequest(); }
    if (request is null || string.IsNullOrWhiteSpace(request.Key) ||
        string.IsNullOrWhiteSpace(request.ExpectedStartedAt))
        return Results.BadRequest();
    ctx.Response.Headers.CacheControl = "no-store";
    try { return Results.Ok(await recovery.ResolveAsync(request.Key, request.ExpectedStartedAt, ctx.RequestAborted)); }
    catch (InvalidOperationException e) { return Results.Conflict(new { error = e.Message }); }
}).RequireRateLimiting("publish");
FeedEndpoints.Map(app);
FeedNugetEndpoints.Map(app);
FeedRawEndpoints.Map(app);
FeedMavenEndpoints.Map(app);
RawEndpoints.Map(app);
MavenEndpoints.Map(app);
GoEndpoints.Map(app);
RegistryCapabilities.Map(app);
OciEndpoints.Map(app);
CargoEndpoints.Map(app);
PyPiEndpoints.Map(app);
NuGetEndpoints.Map(app);
NpmEndpoints.Map(app);
SkillEndpoints.Map(app);
SkillSecurityEndpoints.Map(app);
McpRegistryEndpoints.Map(app);
SkillQualityEndpoints.Map(app);
McpOAuthResourceMetadata.Map(app);
if (settings.McpEnabled) app.MapMcp("/mcp").RequireRateLimiting("mcp");
app.Run();

public partial class Program { }
public sealed record ManagedTokenRequest(string Role, string? Namespace, int ExpiresInHours);

public sealed record RecoveryReconcileRequest(string Key, string ExpectedStartedAt);
