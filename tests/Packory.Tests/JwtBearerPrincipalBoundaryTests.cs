using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Packory;
using Xunit;

namespace Packory.Tests;

/// <summary>
/// Synthetic scheme isolation checks; signed JWT validation is separately
/// tested with the real Keycloak resource-server workflow.
/// </summary>
public sealed class JwtBearerPrincipalBoundaryTests
{
    private static Settings Settings() => new()
    {
        OidcAuthority = "https://identity.example.invalid/realm/synthetic",
        OidcAudience = "packory-api",
        AnonymousRead = false,
        ValidationEnabled = true,
        RequireOidcNamespaceGrants = true
    };

    private static ClaimsPrincipal Principal(string scheme, params Claim[] claims) =>
        new(new ClaimsIdentity(claims, scheme));

    [Fact]
    public void CookieIdentityWithPrivilegedClaimsCannotAccessNativeOrMcpApis()
    {
        var cookie = Principal("Cookies",
            new Claim("scope", "packory.admin packory.write packory.validation.read"),
            new Claim("packory_feed", "alpha-feed:publish"),
            new Claim("packory_namespace", "npm:@private/*"));
        var context = new DefaultHttpContext { User = cookie };
        context.Request.Headers.Cookie = "packory-session=synthetic";
        var settings = Settings();

        Assert.NotNull(Access.Require(context, settings, write: true));
        Assert.NotNull(Access.RequireMcp(context, settings));
        Assert.NotNull(Access.RequireAdmin(context, settings));
        Assert.NotNull(Access.RequireFeed(context, settings, "alpha-feed", publish: true));
        Assert.False(Access.HasValidationMcpAccess(context, settings));
        Assert.Equal("unauthenticated", Access.RateLimitPartition(context, settings));
    }

    [Fact]
    public void PrincipalNotRecordedByBearerValidationIsDeniedDespiteBearerHeader()
    {
        var context = new DefaultHttpContext
        {
            User = Principal("fake-auth", new Claim("scope", "packory.admin"))
        };
        context.Request.Headers.Authorization = "Bearer synthetic-not-cryptographically-validated";

        Assert.NotNull(Access.RequireAdmin(context, Settings()));
        Assert.NotNull(Access.RequireMcp(context, Settings()));
    }

    [Fact]
    public void MarkerWithoutAuthorizationBearerHeaderCannotGrantApiAccess()
    {
        var context = new DefaultHttpContext
        {
            User = Principal("signed-fixture", new Claim("scope", "packory.admin"))
        };
        Access.RecordValidatedJwt(context, context.User);
        Assert.NotNull(Access.RequireAdmin(context, Settings()));
    }

    [Fact]
    public void JwtReadScopeRemainsLimitedWhenCookiePrincipalHasAdminAndWriteClaims()
    {
        var jwt = Principal("signed-fixture", new Claim("scope", "packory.read"),
            new Claim("packory_feed", "alpha-feed:read"));
        var context = new DefaultHttpContext
        {
            User = Principal("Cookies", new Claim("scope", "packory.admin packory.write"),
                new Claim("packory_feed", "beta-feed:publish"))
        };
        context.Request.Headers.Authorization = "Bearer synthetic-verified-jwt";
        Access.RecordValidatedJwt(context, jwt);
        var settings = Settings();

        Assert.Null(Access.RequireMcp(context, settings));
        Assert.NotNull(Access.RequireAdmin(context, settings));
        Assert.NotNull(Access.Require(context, settings, write: true));
        Assert.False(Access.HasValidationMcpAccess(context, settings));
        Assert.Null(Access.RequireFeed(context, settings, "alpha-feed"));
        Assert.NotNull(Access.RequireFeed(context, settings, "beta-feed", publish: true));
        Assert.Equal("oidc-read", Access.RateLimitPartition(context, settings));
    }

    [Fact]
    public void ValidatedBearerScopeStillRequiresIndependentNamespaceAndValidationClaims()
    {
        var jwt = Principal("signed-fixture",
            new Claim("scope", "packory.write packory.validation.read"),
            new Claim("packory_namespace", "npm:@approved/*"));
        var context = new DefaultHttpContext { User = jwt };
        context.Request.Headers.Authorization = "Bearer synthetic-verified-jwt";
        Access.RecordValidatedJwt(context, jwt);
        var settings = Settings();

        Assert.Null(Access.Require(context, settings, write: true));
        Assert.Null(Access.RequireNamespace(context, settings, "npm", "@approved/library"));
        Assert.NotNull(Access.RequireNamespace(context, settings, "npm", "@other/library"));
        Assert.False(Access.HasValidationMcpAccess(context, settings));
    }
}
