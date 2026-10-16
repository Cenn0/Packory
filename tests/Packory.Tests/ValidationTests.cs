using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData("../escape")]
    [InlineData("foo/bar")]
    [InlineData("a\\b")]
    [InlineData("%2e%2e")]
    public void RejectsUnsafeNugetIds(string value) => Assert.False(Names.Nuget(value));

    [Theory]
    [InlineData("@demo/widget", true)]
    [InlineData("lodash", true)]
    [InlineData("../admin", false)]
    [InlineData("@demo/../foo", false)]
    [InlineData("demo/widget", false)]
    public void ValidatesNpmNames(string value, bool valid) => Assert.Equal(valid, Names.Npm(value));

    [Fact]
    public void RejectsShortOrDuplicateApiTokens()
    {
        var cfg = new Settings { AdminToken = "short" };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
        cfg.AdminToken = new string('a', 40);
        cfg.ReadToken = cfg.AdminToken;
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void RejectsUntrustedPublicUrlAndPlaintextUpstream()
    {
        var cfg = new Settings { PublicBaseUrl = "https://example.test/path" };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
        cfg.PublicBaseUrl = "https://example.test";
        cfg.NpmUpstreamUrl = "http://registry.npmjs.org/";
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10001)]
    public void RejectsUnsupportedPublishRateLimits(int value)
    {
        var cfg = new Settings { PublishRequestsPerMinute = value };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void RejectsInvalidBlocklistConfigurations()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new Settings { DeniedNpmScopes = ["plain"] }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new Settings { DeniedNugetPrefixes = ["../"] }.Validate());
    }


    [Theory]
    [InlineData("ab", "2/ab")]
    [InlineData("foo", "3/f/foo")]
    [InlineData("serde", "se/rd/serde")]
    public void CargoIndexPathFollowsSparseProtocol(string name, string expected) =>
        Assert.Equal(expected, CargoEndpoints.IndexPath(name));

    [Fact]
    public void PythonProjectNormalizationUsesPep503Rules()
    {
        Assert.Equal("foo-bar-baz", PyPiEndpoints.Normalize("Foo_Bar..baz"));
        Assert.Null(PyPiEndpoints.Normalize("../secret"));
        Assert.False(CargoEndpoints.IsName("../foo"));
    }


    [Fact]
    public void OciRejectsInvalidRepositoriesDigestsAndReferences()
    {
        Assert.True(OciEndpoints.ValidRepository("team/service"));
        Assert.False(OciEndpoints.ValidRepository("../service"));
        Assert.False(OciEndpoints.ValidRepository("HTTP://test"));
        Assert.False(OciEndpoints.ValidDigest("sha256:xyz"));
        Assert.True(OciEndpoints.ValidReference("latest"));
        Assert.False(OciEndpoints.ValidReference("../../latest"));
    }


    [Fact]
    public void OidcRequiresBothAuthorityAndAudienceOverHttps()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new Settings { OidcAuthority = "https://id.example.test/realms/packory" }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new Settings { OidcAuthority = "http://id.example.test/realms/packory", OidcAudience = "packory" }.Validate());
        new Settings
        {
            OidcAuthority = "https://id.example.test/realms/packory",
            OidcAudience = "packory"
        }.Validate();
    }

    [Fact]
    public void OidcScopeAuthorization_NeverGrantsAccessOnAuthenticationAlone()
    {
        static Microsoft.AspNetCore.Http.DefaultHttpContext Context(params string[] scopes)
        {
            var principal = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    scopes.Select(s => new System.Security.Claims.Claim("scope", s)),
                    "Bearer"));
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = principal };
            context.Request.Headers.Authorization = "Bearer mock-test-credential";
            return context;
        }
        var cfg = new Settings
        {
            OidcAuthority = "https://id.example.test/realms/packory",
            OidcAudience = "packory",
            AnonymousRead = false
        };
        var noPermissions = Context("profile");
        Assert.NotNull(Access.Require(noPermissions, cfg, write: true));
        var writer = Context("packory.write");
        Assert.Null(Access.Require(writer, cfg, write: true));
        Assert.Equal("oidc-write", Access.Actor(writer));
        Assert.NotNull(Access.RequireAdmin(writer, cfg));
        var administrator = Context("packory.admin");
        Assert.Null(Access.RequireAdmin(administrator, cfg));
        Assert.Equal("oidc-admin", Access.Actor(administrator));
    }


    [Fact]
    public void S3RejectsInsecureRemoteEndpointsAndIncompleteSettings()
    {
        Assert.Throws<InvalidOperationException>(() => new Settings { BlobProvider = "s3" }.Validate());
        var cfg = new Settings
        {
            BlobProvider = "s3", S3Bucket = "packory-tests",
            S3Endpoint = "http://remote.example.test",
            S3AccessKey = "test", S3SecretKey = "test"
        };
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
        cfg.S3Endpoint = "http://127.0.0.1:5000";
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
        cfg.S3AllowLoopbackHttp = true;
        cfg.Validate();
        cfg.S3KeyPrefix = "bad/../segment";
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
        cfg.S3KeyPrefix = "packory";
        cfg.S3SecretKey = null;
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

}
