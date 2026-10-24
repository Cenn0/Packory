using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Packory;
using Xunit;

namespace Packory.Tests;

/// <summary>
/// Synthetic browser-boundary tests. Interactive Keycloak code/PKCE,
/// cryptographic cookie validation and real-browser CSRF need separate E2E.
/// </summary>
public sealed class BrowserSsoTests
{
    private static Settings Enabled() => new()
    {
        OidcAuthority = "https://identity.example.invalid/realms/test",
        OidcAudience = "packory-api",
        OidcClientId = "packory-dashboard",
        OidcClientSecret = "synthetic-client-secret-of-at-least-32-characters",
        AnonymousRead = false,
        RequireOidcNamespaceGrants = true,
        PublicBaseUrl = "https://registry.example.invalid"
    };

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "validated-id-token"));

    private static DefaultHttpContext ApiSession(params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/admin/operations/status";
        context.Request.Headers["X-Packory-Browser"] = "1";
        context.Items[BrowserSso.BrowserPrincipalKey] = BrowserSso.RestrictIdentity(Principal(claims));
        return context;
    }

    [Fact]
    public void RestrictsValidatedIdentityToExplicitRolesAndGrants()
    {
        var verified = Principal(
            new Claim("sub", "synthetic-user"),
            new Claim("email", "private@example.invalid"),
            new Claim("scope", "packory.admin"),
            new Claim("packory_role", "admin"),
            new Claim("packory_role", "unexpected"),
            new Claim("packory_namespace", "npm:@team/*"),
            new Claim("packory_namespace", "npm:../bad"),
            new Claim("packory_feed", "my-feed:read"),
            new Claim("packory_feed", "bad-feed:admin"));
        var session = BrowserSso.RestrictIdentity(verified);

        Assert.Equal("admin", BrowserSso.Role(session));
        Assert.All(session.Claims, claim => Assert.Contains(claim.Type,
            new[] { "packory_role", "packory_namespace", "packory_feed" }));
        Assert.DoesNotContain(session.Claims, c => c.Value == "synthetic-user" ||
            c.Value == "private@example.invalid" || c.Value == "packory.admin" ||
            c.Value == "unexpected" || c.Value == "npm:../bad" ||
            c.Value == "bad-feed:admin");
        Assert.Contains(session.Claims, c => c.Type == "packory_feed" && c.Value == "my-feed:read");
    }

    [Fact]
    public void ArbitraryCookiePrincipalDoesNotGrantApiOrMcpAccess()
    {
        var ctx = new DefaultHttpContext
        {
            User = Principal(new Claim("packory_role", "admin"),
                new Claim("scope", "packory.admin"))
        };
        ctx.Request.Headers.Cookie = "Packory.Session=fake";
        ctx.Request.Path = "/api/admin/operations/status";
        ctx.Request.Headers["X-Packory-Browser"] = "1";
        Assert.NotNull(Access.RequireAdmin(ctx, Enabled()));
        Assert.NotNull(Access.RequireMcp(ctx, Enabled()));
    }

    [Fact]
    public void ValidatedBrowserSessionGrantsApiButNeverMcpOrValidation()
    {
        var ctx = ApiSession(new Claim("packory_role", "admin"),
            new Claim("packory_feed", "my-feed:read"),
            new Claim("packory_namespace", "npm:@team/*"));
        var settings = Enabled();
        settings.ValidationEnabled = true;
        Assert.Null(Access.RequireAdmin(ctx, settings));
        Assert.Equal("browser-admin", Access.Actor(ctx));
        Assert.NotNull(Access.RequireMcp(ctx, settings));
        Assert.NotNull(Access.RequireGlobalMcp(ctx, settings));
        Assert.False(Access.HasValidationMcpAccess(ctx, settings));
    }

    [Fact]
    public void ReadAndWriteBrowserRolesRemainScoped()
    {
        var settings = Enabled();
        var reader = ApiSession(new Claim("packory_role", "read"),
            new Claim("packory_feed", "team-feed:read"));
        Assert.Null(Access.Require(reader, settings));
        Assert.NotNull(Access.Require(reader, settings, write: true));
        Assert.NotNull(Access.RequireAdmin(reader, settings));
        Assert.Null(Access.RequireFeed(reader, settings, "team-feed"));
        Assert.NotNull(Access.RequireFeed(reader, settings, "other-feed"));
        Assert.NotNull(Access.RequireFeed(reader, settings, "team-feed", publish: true));

        var publisher = ApiSession(new Claim("packory_role", "write"),
            new Claim("packory_namespace", "npm:@team/*"),
            new Claim("packory_feed", "team-feed:publish"));
        Assert.Null(Access.Require(publisher, settings, write: true));
        Assert.Null(Access.RequireNamespace(publisher, settings, "npm", "@team/pkg"));
        Assert.NotNull(Access.RequireNamespace(publisher, settings, "npm", "@other/pkg"));
        Assert.Null(Access.RequireFeed(publisher, settings, "team-feed", publish: true));
        Assert.NotNull(Access.RequireFeed(publisher, settings, "other-feed", publish: true));
        Assert.NotNull(Access.RequireAdmin(publisher, settings));
    }

    [Fact]
    public void WebClientCannotBeEnabledWithoutSecureConfiguration()
    {
        var enabled = Enabled();
        enabled.Validate();
        enabled.OidcClientSecret = null;
        Assert.Throws<InvalidOperationException>(enabled.Validate);

        var insecure = Enabled();
        insecure.PublicBaseUrl = "http://registry.example.invalid";
        Assert.Throws<InvalidOperationException>(insecure.Validate);

        var noIssuer = Enabled();
        noIssuer.OidcAuthority = null;
        noIssuer.OidcAudience = null;
        Assert.Throws<InvalidOperationException>(noIssuer.Validate);
    }

    [Fact]
    public void BrowserOptInRejectsNativeAuthorizationHeaders()
    {
        var ctx = ApiSession(new Claim("packory_role", "admin"));
        Assert.True(BrowserSso.WantsBrowser(ctx));
        ctx.Request.Headers.Authorization = "Bearer invalid";
        Assert.False(BrowserSso.WantsBrowser(ctx));
        ctx.Request.Headers.Authorization = "";
        ctx.Request.Path = "/mcp";
        Assert.False(BrowserSso.WantsBrowser(ctx));
        ctx.Request.Path = "/feeds/team-feed/npm/";
        Assert.False(BrowserSso.WantsBrowser(ctx));
    }

    [Fact]
    public async Task TicketStoreSupportsExplicitRevocationAndExpiry()
    {
        var store = new BrowserTicketStore();
        var props = new AuthenticationProperties
        {
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        var ticket = new AuthenticationTicket(
            BrowserSso.RestrictIdentity(Principal(new Claim("packory_role", "read"))),
            props, BrowserSso.CookieScheme);
        var key = await store.StoreAsync(ticket);
        Assert.NotNull(await store.RetrieveAsync(key));
        await store.RemoveAsync(key);
        Assert.Null(await store.RetrieveAsync(key));

        var expired = new AuthenticationTicket(ticket.Principal,
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1) },
            BrowserSso.CookieScheme);
        Assert.Null(await store.RetrieveAsync(await store.StoreAsync(expired)));
    }
}
