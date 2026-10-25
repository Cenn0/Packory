using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Packory;

/// <summary>
/// Explicit opt-in browser authorization. A native package request, Bearer API,
/// or MCP call never acquires privileges from a browser session cookie.
/// </summary>
public static class BrowserSso
{
    public const string CookieScheme = "PackoryBrowser";
    public const string OidcScheme = "PackoryOpenIdConnect";
    public const string BrowserPrincipalKey = "packory.browser-validated-principal";

    public static bool WantsBrowser(HttpContext ctx) =>
        ((ctx.Request.Path.StartsWithSegments("/api") &&
          !string.Equals(ctx.Request.Path.Value?.TrimEnd('/'), "/api/go/modules",
              StringComparison.OrdinalIgnoreCase)) ||
         (ctx.RequestServices?.GetService<Settings>()?.BrowserNativePublishEnabled == true &&
          NativePublishPath(ctx.Request))) &&
        ctx.Request.Headers["X-Packory-Browser"].ToString() == "1" &&
        string.IsNullOrEmpty(ctx.Request.Headers.Authorization.ToString()) &&
        string.IsNullOrEmpty(ctx.Request.Headers["X-NuGet-ApiKey"].ToString());

    private static bool NativePublishPath(HttpRequest request)
    {
        var path = request.Path.Value ?? "";
        var put = HttpMethods.IsPut(request.Method);
        var post = HttpMethods.IsPost(request.Method);
        var patch = HttpMethods.IsPatch(request.Method);
        if ((put || post || patch) && path.StartsWith("/v2/", StringComparison.Ordinal)) return true;
        if (put && (path.StartsWith("/raw/", StringComparison.Ordinal) ||
                    path.StartsWith("/npm/", StringComparison.Ordinal) ||
                    path.StartsWith("/maven/", StringComparison.Ordinal) ||
                    path == "/cargo/api/v1/crates/new")) return true;
        if ((put || post) && path == "/nuget/v3/push") return true;
        if (post && path == "/pypi/legacy/") return true;
        if (post && string.Equals(path.TrimEnd('/'), "/api/go/modules", StringComparison.OrdinalIgnoreCase)) return true;
        var parts = path.Split('/');
        if (parts.Length < 5 || parts[1] != "feeds" || !FeedDefinition.ValidId(parts[2])) return false;
        var root = "/feeds/" + parts[2];
        if (put && (path.StartsWith(root + "/raw/", StringComparison.Ordinal) ||
                    path.StartsWith(root + "/npm/", StringComparison.Ordinal) ||
                    path.StartsWith(root + "/maven/", StringComparison.Ordinal) ||
                    path == root + "/cargo/api/v1/crates/new")) return true;
        if ((put || post) && path == root + "/nuget/v3/push") return true;
        if (post && (path == root + "/pypi/legacy/" || path == root + "/api/go/modules")) return true;
        return false;
    }

    public static string Role(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true) return "none";
        var roles = principal.FindAll("packory_role").Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal);
        if (roles.Contains("admin")) return "admin";
        if (roles.Contains("write")) return "write";
        if (roles.Contains("read")) return "read";
        return "none";
    }

    /// <summary>
    /// Invoked after the OIDC handler has validated the ID token for the
    /// intended web client. The operator maps roles into ID token claims.
    /// Strip identifiers, email, arbitrary IdP claims and all OAuth tokens
    /// before creating the server-side session.
    /// </summary>
    public static ClaimsPrincipal RestrictIdentity(ClaimsPrincipal source)
    {
        var claims = new List<Claim>();
        foreach (var role in source.FindAll("packory_role").Select(c => c.Value).Distinct())
            if (role is "read" or "write" or "admin")
                claims.Add(new Claim("packory_role", role));
        foreach (var grant in source.FindAll("packory_namespace").Take(64).Select(c => c.Value))
            if (grant.Length <= 256 && Access.ValidNamespaceGrant(grant))
                claims.Add(new Claim("packory_namespace", grant));
        foreach (var grant in source.FindAll("packory_feed").Take(64).Select(c => c.Value))
        {
            var parts = grant.Split(':');
            if (parts.Length == 2 && FeedDefinition.ValidId(parts[0]) &&
                parts[1] is "read" or "publish")
                claims.Add(new Claim("packory_feed", grant));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieScheme));
    }

    public static async Task AllowBrowserApi(HttpContext ctx, RequestDelegate next)
    {
        if (!WantsBrowser(ctx))
        {
            await next(ctx);
            return;
        }
        var authentication = await ctx.AuthenticateAsync(CookieScheme);
        if (!authentication.Succeeded || authentication.Principal is null ||
            authentication.Principal.Identity?.AuthenticationType != CookieScheme)
        {
            if (IsUnsafe(ctx.Request.Method))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(ctx);
            return;
        }
        if (IsUnsafe(ctx.Request.Method))
        {
            var antiforgery = ctx.RequestServices.GetRequiredService<IAntiforgery>();
            if (!await antiforgery.IsRequestValidAsync(ctx))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
        }
        ctx.Items[BrowserPrincipalKey] = authentication.Principal;
        await next(ctx);
    }

    public static bool IsUnsafe(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) &&
        !HttpMethods.IsOptions(method) && !HttpMethods.IsTrace(method);
}

/// <summary>
/// Per-process, bounded, revocable server-side session store. Restarts log
/// browsers out; multi-node session distribution is not provided.
/// </summary>
public sealed class BrowserTicketStore : ITicketStore
{
    private const int MaxSessions = 10000;
    private readonly ConcurrentDictionary<string, AuthenticationTicket> tickets = new();

    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        PurgeExpired();
        if (tickets.Count >= MaxSessions)
            throw new InvalidOperationException("Browser session capacity reached.");
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        tickets[key] = ticket;
        return Task.FromResult(key);
    }

    public Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        if (tickets.ContainsKey(key)) tickets[key] = ticket;
        return Task.CompletedTask;
    }

    public Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (!tickets.TryGetValue(key, out var ticket))
            return Task.FromResult<AuthenticationTicket?>(null);
        if (ticket.Properties.ExpiresUtc is { } expires && expires > DateTimeOffset.UtcNow)
            return Task.FromResult<AuthenticationTicket?>(ticket);
        tickets.TryRemove(key, out _);
        return Task.FromResult<AuthenticationTicket?>(null);
    }

    public Task RemoveAsync(string key)
    {
        tickets.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private void PurgeExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in tickets)
            if (entry.Value.Properties.ExpiresUtc is not { } expiry || expiry <= now)
                tickets.TryRemove(entry.Key, out _);
    }
}
