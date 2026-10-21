using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Packory;
using Xunit;

namespace Packory.Tests;

/// <summary>
/// Feed authorization after JWT authentication. Signature/issuer validation is
/// separately exercised by the signed Keycloak workflow; these tests never
/// claim that a synthetic ClaimsPrincipal is a verified JWT.
/// </summary>
public sealed class FeedAuthorizationTests
{
    private static readonly Settings Oidc = new()
    {
        OidcAuthority = "https://identity.example.invalid/realms/synthetic",
        OidcAudience = "packory", AnonymousRead = true
    };

    private static DefaultHttpContext Principal(string scope, params string[] grants)
    {
        var claims = new List<Claim> { new("scope", scope) };
        claims.AddRange(grants.Select(grant => new Claim("packory_feed", grant)));
        return new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "synthetic-authenticated-principal"))
        };
    }

    private static int? Status(IResult? result) => result is IStatusCodeHttpResult code ? code.StatusCode : null;

    [Theory]
    [InlineData("nuget")]
    [InlineData("npm")]
    [InlineData("cargo")]
    [InlineData("pypi")]
    [InlineData("oci")]
    [InlineData("maven")]
    [InlineData("go")]
    [InlineData("raw")]
    public void AuthenticatedFeedClaimsRequireExactIdentityAndBothScopeAndPermission(string kind)
    {
        var alpha = kind + "-alpha";
        var beta = kind + "-beta";
        Assert.Null(Access.RequireFeed(Principal("packory.read", alpha + ":read"), Oidc, alpha));
        Assert.Equal(403, Status(Access.RequireFeed(Principal("packory.read", alpha + ":read"), Oidc, beta)));
        Assert.Equal(403, Status(Access.RequireFeed(Principal("packory.read", alpha + ":read"), Oidc, alpha, publish: true)));
        Assert.Equal(403, Status(Access.RequireFeed(Principal("packory.read", alpha + ":publish"), Oidc, alpha, publish: true)));
        Assert.Equal(403, Status(Access.RequireFeed(Principal("packory.write"), Oidc, alpha, publish: true)));
        Assert.Equal(403, Status(Access.RequireFeed(Principal("packory.write", alpha + ":read"), Oidc, alpha, publish: true)));
        Assert.Null(Access.RequireFeed(Principal("packory.write", alpha + ":publish"), Oidc, alpha, publish: true));
        Assert.Null(Access.RequireFeed(Principal("packory.write", alpha + ":publish"), Oidc, alpha));
        Assert.Equal(403, Status(Access.RequireFeed(Principal("packory.read", "*:*", alpha.ToUpperInvariant() + ":read"), Oidc, alpha)));
        Assert.Equal(401, Status(Access.RequireFeed(Principal("unrelated.scope", alpha + ":read"), Oidc, alpha)));
        Assert.Equal(401, Status(Access.RequireFeed(new DefaultHttpContext(), Oidc, alpha)));
        Assert.Null(Access.RequireFeed(Principal("packory.admin"), Oidc, alpha, publish: true));
    }
}
