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

    public static IResult? Require(HttpContext context, Settings settings, bool write = false)
    {
        if (!write && settings.AnonymousRead) return null;
        var token = ExtractToken(context);
        if (string.IsNullOrEmpty(token) && OidcCredentialRole(context, settings) is null)
            return Results.Unauthorized();

        var role = CredentialRole(token, settings) ?? OidcCredentialRole(context, settings);
        if (role is "admin-token" or "write-token" or "oidc-admin" or "oidc-write" ||
            (!write && role is "read-token" or "oidc-read"))
        {
            context.Items[ActorKey] = role;
            return null;
        }
        return Results.Json(new { error = "Token lacks required permissions" },
            statusCode: StatusCodes.Status403Forbidden);
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
        return (token is null ? null : CredentialRole(token, settings))
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

    private static string? CredentialRole(string token, Settings settings)
    {
        if (Matches(token, settings.AdminToken)) return "admin-token";
        if (Matches(token, settings.WriteToken)) return "write-token";
        if (Matches(token, settings.ReadToken)) return "read-token";
        return null;
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
        if (string.IsNullOrWhiteSpace(token) && context.Request.Path.StartsWithSegments("/cargo") &&
            !string.IsNullOrWhiteSpace(auth) && !auth.Contains(' ') && !auth.Contains('\t'))
            token = auth; // Cargo's registry API uses a bare Authorization token.
        if (string.IsNullOrWhiteSpace(token)) token = context.Request.Headers["X-NuGet-ApiKey"].ToString();
        return !string.IsNullOrWhiteSpace(token) && token.Length <= 8192 ? token : null;
    }

    private static bool Matches(string provided, string? expected)
    {
        if (expected is null) return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }
}
