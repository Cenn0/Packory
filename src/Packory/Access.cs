using System.Security.Cryptography;
using System.Text;

namespace Packory;

/// <summary>
/// Static token authorization. Audit identities represent credential roles, not individual users.
/// Never store, log, or return submitted tokens.
/// </summary>
public static class Access
{
    private const string ActorKey = "packory.actor";
    private const string RoleKey = "packory.role";
    private const string ManagedNamespaceKey = "packory.managed-namespace";
    private const string ManagedIdKey = "packory.managed-id";

    public static IResult? Require(HttpContext context, Settings settings, bool write = false)
    {
        if (!write && settings.AnonymousRead) return null;
        var token = ExtractToken(context);
        if (string.IsNullOrEmpty(token) && OidcCredentialRole(context, settings) is null)
            return Results.Unauthorized();

        var role = (token is null ? null : CredentialRole(token, settings, context)) ??
            OidcCredentialRole(context, settings);
        if (role is null) return Results.Unauthorized();
        // A grant scoped to one private feed or publish namespace is NOT a
        // global registry read permission. Native private feed routes use
        // RequireFeed instead; unscoped managed readers retain global access.
        // Public legacy routes remain public when AnonymousRead is enabled.
        if (!write && role is ("managed-read" or "managed-write") &&
            context.Items[ManagedNamespaceKey] is string)
            return Results.Json(new { error = "Scoped credentials cannot read global registries" },
                statusCode: StatusCodes.Status403Forbidden);
        if (role is "admin-token" or "write-token" or "oidc-admin" or "oidc-write" or "managed-write" ||
            (!write && role is "read-token" or "oidc-read" or "managed-read"))
        {
            context.Items[RoleKey] = role;
            context.Items[ActorKey] = role.StartsWith("managed-", StringComparison.Ordinal)
                ? "managed-token:" + (context.Items[ManagedIdKey] as string ?? "unknown") : role;
            return null;
        }
        return Results.Json(new { error = "Token lacks required permissions" },
            statusCode: StatusCodes.Status403Forbidden);
    }

    // Remote MCP must require an authenticated reader even when the public REST catalog is anonymous.
    public static IResult? RequireMcp(HttpContext context, Settings settings)
    {
        var token = ExtractToken(context);
        var role = (token is null ? null : CredentialRole(token, settings, context)) ??
            OidcCredentialRole(context, settings);
        if (role is not ("admin-token" or "write-token" or "read-token" or "managed-write"
            or "managed-read" or "oidc-admin" or "oidc-write" or "oidc-read"))
            return Results.Unauthorized();
        context.Items[RoleKey] = role;
        context.Items[ActorKey] = role.StartsWith("managed-", StringComparison.Ordinal)
            ? "managed-token:" + (context.Items[ManagedIdKey] as string ?? "unknown") : role;
        return null;
    }

    public static bool CanPingPrivateOci(HttpContext context, Settings settings)
    {
        if (RequireMcp(context, settings) is not null) return false;
        var candidates = new List<string>();
        if (context.Items[ManagedNamespaceKey] is string grant && ValidFeedGrant(grant))
            candidates.Add(grant.Split(':')[1]);
        if (settings.OidcEnabled && context.User.Identity?.IsAuthenticated == true)
            candidates.AddRange(context.User.FindAll("packory_feed")
                .Select(claim => claim.Value.Split(':')[0]).Where(FeedDefinition.ValidId));
        var db = context.RequestServices.GetRequiredService<IPackageDatabase>();
        return candidates.Any(id => db.Feed(id) is
            { Kind: "oci", Mode: "hosted", Visibility: "private", Enabled: true } &&
            RequireFeed(context, settings, id) is null);
    }

    // A per-feed or package namespace managed token must never become a global
    // Raw catalog reader when the instance disables anonymous reads.
    public static IResult? RequireGlobalRaw(HttpContext context, Settings settings)
    {
        if (Require(context, settings) is { } denied) return denied;
        if (context.Items[RoleKey] is string role &&
            role is "managed-read" or "managed-write" &&
            context.Items[ManagedNamespaceKey] is string)
            return Results.Json(new { error = "Scoped credentials cannot read the global Raw registry" },
                statusCode: StatusCodes.Status403Forbidden);
        return null;
    }

    /// <summary>
    /// Packory MCP exposes the global artifact catalog, not a single hosted
    /// feed. A managed credential restricted to one feed or publishing
    /// namespace must never escape that grant by calling MCP tools.
    /// RequireFeed continues to use RequireMcp so native feed tokens work.
    /// </summary>
    public static IResult? RequireGlobalMcp(HttpContext context, Settings settings)
    {
        if (RequireMcp(context, settings) is { } denied) return denied;
        var role = context.Items[RoleKey] as string;
        if ((role is "managed-read" or "managed-write") &&
            context.Items.TryGetValue(ManagedNamespaceKey, out var scope) &&
            scope is string)
            return Results.Json(new { error = "Scoped credentials cannot read the global MCP catalog" },
                statusCode: StatusCodes.Status403Forbidden);
        return null;
    }

    // Validation is never authorized by static catalog credentials or general packory.read.
    // The authenticated OIDC token must carry both the ordinary MCP entry scope
    // and the additional, dedicated validation scope.
    public static bool HasValidationMcpAccess(HttpContext? context, Settings settings)
    {
        if (!settings.ValidationEnabled || !settings.OidcEnabled ||
            context?.User.Identity?.IsAuthenticated != true) return false;
        var scopes = context.User.FindAll("scope").Concat(context.User.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        return scopes.Contains("packory.validation.read") &&
               (scopes.Contains("packory.read") || scopes.Contains("packory.admin"));
    }

    public static IResult? RequireAdmin(HttpContext context, Settings settings)
    {
        if (Require(context, settings, write: true) is { } denied) return denied;
        return Actor(context) is "admin-token" or "oidc-admin"
            ? null : Results.Json(new { error = "Administrator credential required" },
                statusCode: StatusCodes.Status403Forbidden);
    }

    public static string Actor(HttpContext context) =>
        context.Items.TryGetValue(ActorKey, out var value) ? value as string ?? "unknown" : "unknown";

    // Used for pre-handler rate limiting: never partition by untrusted token or IP/header value.
    // At most four partitions exist (three known roles plus unauthenticated).
    public static string RateLimitPartition(HttpContext context, Settings settings)
    {
        var token = ExtractToken(context);
        return (token is null ? null : CredentialRole(token, settings, context))
            ?? OidcCredentialRole(context, settings) ?? "unauthenticated";
    }

    private static string? OidcCredentialRole(HttpContext context, Settings settings)
    {
        if (!settings.OidcEnabled || context.User.Identity?.IsAuthenticated != true) return null;
        // No blanket access based only on being logged in: require an explicit
        // Packory scope from the access token. Never store subject/email claims.
        var scopes = context.User.FindAll("scope").Concat(context.User.FindAll("scp"))
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.Ordinal);
        if (scopes.Contains("packory.admin")) return "oidc-admin";
        if (scopes.Contains("packory.write")) return "oidc-write";
        if (scopes.Contains("packory.read")) return "oidc-read";
        return null;
    }

    /// <summary>
    /// Authorization for a specific artifact target, performed after identity verification
    /// and package-name validation, but before writing bytes or mutating metadata.
    /// Only grants from server configuration or validated OIDC identity claims are trusted.
    /// </summary>
    public static IResult? RequireNamespace(HttpContext context, Settings settings, string kind, string name)
    {
        var role = context.Items.TryGetValue(RoleKey, out var value)
            ? value as string ?? Actor(context) : Actor(context);
        if (role is "admin-token" or "oidc-admin") return null;
        if (role == "write-token" && settings.WriteNamespaces.Length == 0) return null;
        if (role == "oidc-write" && !settings.RequireOidcNamespaceGrants) return null;
        IEnumerable<string> grants = role switch
        {
            "write-token" => settings.WriteNamespaces,
            "managed-write" when context.Items[ManagedNamespaceKey] is string grant =>
                new[] { grant },
            "oidc-write" when context.User.Identity?.IsAuthenticated == true =>
                context.User.FindAll("packory_namespace").Select(x => x.Value),
            _ => []
        };
        return grants.Any(grant => MatchesNamespace(grant, kind, name))
            ? null : Results.Json(new { error = "Publishing to this namespace is not permitted" },
                statusCode: StatusCodes.Status403Forbidden);
    }

    // Feed grants live in the existing revocable managed-token namespace field
    // but NEVER grant access to legacy/global NuGet publishing. The grant only
    // authorizes the matching private feed and one permission.
    public static bool ValidFeedGrant(string? grant)
    {
        if (grant is null) return false;
        var parts = grant.Split(':');
        return parts.Length == 3 && parts[0] == "feed" &&
            FeedDefinition.ValidId(parts[1]) &&
            parts[2] is "read" or "publish";
    }

    public static IResult? RequireFeed(HttpContext ctx, Settings settings, string feedId,
        bool publish = false)
    {
        if (!FeedDefinition.ValidId(feedId))
            return Results.BadRequest(new { error = "Invalid feed ID" });
        // Unlike generic package reads, private feeds must authenticate
        // even when anonymous catalog reads are configured.
        if (RequireMcp(ctx, settings) is { } denied)
        {
            // NuGet clients need a standards-compliant authentication challenge
            // before retrying a private service-index request with configured
            // Basic credentials. Never echo credentials or relax feed grants.
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"Packory private feed\"";
            return denied;
        }
        var role = ctx.Items[RoleKey] as string;
        if (role is "admin-token" or "oidc-admin") return null;
        if (role is "managed-read" or "managed-write" &&
            ctx.Items[ManagedNamespaceKey] is string grant && ValidFeedGrant(grant))
        {
            var required = "feed:" + feedId + ":" + (publish ? "publish" : "read");
            if ((!publish || role == "managed-write") &&
                (string.Equals(grant, required, StringComparison.Ordinal) ||
                 (!publish && string.Equals(grant, "feed:" + feedId + ":publish",
                     StringComparison.Ordinal))))
                return null;
        }
        if (settings.OidcEnabled && ctx.User.Identity?.IsAuthenticated == true &&
            role is "oidc-read" or "oidc-write")
        {
            var required = feedId + ":" + (publish ? "publish" : "read");
            if ((!publish || role == "oidc-write") &&
                ctx.User.FindAll("packory_feed").Any(c =>
                    string.Equals(c.Value, required, StringComparison.Ordinal) ||
                    (!publish && string.Equals(c.Value, feedId + ":publish",
                        StringComparison.Ordinal))))
                return null;
        }
        return Results.Json(new { error = "Feed permission required" },
            statusCode: StatusCodes.Status403Forbidden);
    }

    public static bool ValidNamespaceGrant(string grant)
    {
        var sep = grant.IndexOf(':');
        if (sep <= 0 || sep == grant.Length - 1) return false;
        var kind = grant[..sep];
        if (kind is not ("nuget" or "npm" or "skill" or "cargo" or "pypi" or "oci" or "raw" or "maven" or "go")) return false;
        var value = grant[(sep + 1)..];
        var wildcard = value.EndsWith('*');
        if (wildcard) value = value[..^1];
        // No traversal, arbitrary regex, whitespace, embedded wildcards or URLs.
        return value.Length is >= 1 and <= 200 &&
               !value.Contains("..", StringComparison.Ordinal) &&
               value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/' or '@');
    }

    public static bool MatchesNamespace(string grant, string kind, string name)
    {
        if (!ValidNamespaceGrant(grant)) return false;
        var separator = grant.IndexOf(':');
        if (!string.Equals(kind, grant[..separator], StringComparison.Ordinal)) return false;
        var value = grant[(separator + 1)..];
        if (value.EndsWith('*'))
            return name.StartsWith(value[..^1], StringComparison.Ordinal);
        return string.Equals(name, value, StringComparison.Ordinal);
    }

    private static string? CredentialRole(string token, Settings settings, HttpContext? context = null)
    {
        if (Matches(token, settings.AdminToken)) return "admin-token";
        if (Matches(token, settings.WriteToken)) return "write-token";
        if (Matches(token, settings.ReadToken)) return "read-token";
        // Only generated 32-byte hexadecimal tokens can trigger a database lookup.
        // This avoids unbounded DB reads for arbitrary Authorization garbage.
        if (context is null || token.Length != 64 || !token.All(Uri.IsHexDigit)) return null;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var record = context.RequestServices?.GetService<IPackageDatabase>()?.FindManagedToken(hash);
        if (record is null ||
            !DateTimeOffset.TryParse(record.ExpiresAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var expires) ||
            expires <= DateTimeOffset.UtcNow) return null;
        if (record.Role is not ("read" or "write")) return null;
        context.Items[ManagedNamespaceKey] = record.Namespace;
        context.Items[ManagedIdKey] = record.Id;
        return record.Role == "write" ? "managed-write" : "managed-read";
    }

    private static string? ExtractToken(HttpContext context)
    {
        var auth = context.Request.Headers.Authorization.ToString();
        if (auth.Length > 8192) return null;
        string? token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..] : null;
        if (string.IsNullOrWhiteSpace(token) && auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            // NuGet.Config may carry a token as the Basic password.
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..]));
                var delimiter = decoded.IndexOf(':');
                if (delimiter >= 0) token = decoded[(delimiter + 1)..];
            }
            catch (FormatException) { }
        }
        if (string.IsNullOrWhiteSpace(token) && (context.Request.Path.StartsWithSegments("/cargo") || IsPrivateCargoPath(context.Request.Path)) &&
            !string.IsNullOrWhiteSpace(auth) && !auth.Contains(' ') && !auth.Contains('\t'))
            token = auth; // Cargo's registry API uses a bare Authorization token.
        if (string.IsNullOrWhiteSpace(token)) token = context.Request.Headers["X-NuGet-ApiKey"].ToString();
        return !string.IsNullOrWhiteSpace(token) && token.Length <= 8192 ? token : null;
    }

    private static bool IsPrivateCargoPath(PathString path)
    {
        var pieces = (path.Value ?? "").Split('/');
        return pieces.Length >= 4 && pieces[1] == "feeds" &&
            FeedDefinition.ValidId(pieces[2]) && pieces[3] == "cargo";
    }

    private static bool Matches(string provided, string? expected)
    {
        if (expected is null) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}
