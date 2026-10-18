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

    [Fact]
    public void NamespaceGrantParserRejectsUnsafeRulesAndMatchesOnlyIntendedPrefixes()
    {
        Assert.True(Access.ValidNamespaceGrant("nuget:internal.*"));
        Assert.True(Access.ValidNamespaceGrant("oci:team/*"));
        Assert.False(Access.ValidNamespaceGrant("nuget:../../*"));
        Assert.False(Access.ValidNamespaceGrant("unknown:package"));
        Assert.False(Access.ValidNamespaceGrant("nuget:*"));
        Assert.True(Access.MatchesNamespace("nuget:internal.*", "nuget", "internal.demo"));
        Assert.False(Access.MatchesNamespace("nuget:internal.*", "nuget", "internalization.demo"));
        Assert.False(Access.MatchesNamespace("oci:team/*", "nuget", "team/demo"));
        Assert.True(Access.MatchesNamespace("oci:team/*", "oci", "team/demo"));
        Assert.Throws<InvalidOperationException>(() =>
            new Settings { WriteNamespaces = ["npm:../danger"] }.Validate());
    }

    [Fact]
    public void OidcNamespaceWriteClaimsAreFailClosedWhenEnabled()
    {
        var cfg = new Settings
        {
            OidcAuthority = "https://identity.example.test/realms/test",
            OidcAudience = "packory-api",
            RequireOidcNamespaceGrants = true
        };
        var identity = new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim("scope", "packory.write"),
            new System.Security.Claims.Claim("packory_namespace", "npm:@trusted/*")
        }, "Bearer");
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(identity)
        };
        ctx.Request.Headers.Authorization = "Bearer dummy-jwt-for-claims-test";
        Assert.Null(Access.Require(ctx, cfg, write: true));
        Assert.Null(Access.RequireNamespace(ctx, cfg, "npm", "@trusted/library"));
        Assert.NotNull(Access.RequireNamespace(ctx, cfg, "npm", "@other/library"));
    }
    [Fact]
    public void ReadReplicaRequiresPostgresS3AndIndependentNonmaintenanceRole()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new Settings { InstanceRole = "reader" }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new Settings { InstanceRole = "unknown" }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new Settings
            {
                InstanceRole = "reader", MetadataProvider = "postgresql",
                PostgresConnectionString = "Host=localhost", BlobProvider = "s3",
                S3Bucket = "packory-tests", RecoveryMaintenance = true
            }.Validate());
        new Settings
        {
            InstanceRole = "reader", MetadataProvider = "postgresql",
            PostgresConnectionString = "Host=localhost", BlobProvider = "s3",
            S3Bucket = "packory-tests"
        }.Validate();
    }


    [Fact]
    public void StrictDeploymentRequiresHttpsPrivateAccessAndVerifiedPostgresTls()
    {
        var cfg = new Settings
        {
            StrictDeployment = true, MetadataProvider = "postgresql",
            BlobProvider = "s3", S3Bucket = "packory-example",
            PostgresConnectionString = "Host=db.example.test;Database=packory;Username=packory;Password=fixture;SSL Mode=VerifyFull",
            PublicBaseUrl = "https://packages.example.test",
            AnonymousRead = false, AdminToken = new string('a', 40)
        };
        cfg.Validate();
        cfg.PublicBaseUrl = "http://packages.example.test";
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
        cfg.PublicBaseUrl = "https://packages.example.test";
        cfg.AnonymousRead = true;
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
        cfg.AnonymousRead = false;
        cfg.PostgresConnectionString = "Host=db.example.test;Database=packory;SSL Mode=Require";
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
        cfg.PostgresConnectionString = "Host=db.example.test;Database=packory;SSL Mode=VerifyCA";
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
        cfg.PostgresConnectionString = "Host=db.example.test;Database=packory;SSL Mode=VerifyFull";
        cfg.S3AllowLoopbackHttp = true;
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public async Task StalePublicationReadinessDetectsUncommittedBytesWithoutReplaying()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-stale-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = new Settings { DataDirectory = root };
            var db = new Database(cfg);
            db.Initialize();
            var blobs = new BlobStore(cfg);
            var recovery = new ArtifactRecovery(cfg, db, blobs,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtifactRecovery>.Instance);
            var key = NuGetEndpoints.NugetKey("pending-test", "1.0.0");
            db.BeginPublication(key, "nuget", "pending-test", "1.0.0");
            Assert.True(await blobs.PutAsync(key, new MemoryStream(new byte[] { 1, 2, 3 }),
                1024, CancellationToken.None));
            Assert.Equal(1, recovery.StaleUnresolvedCount(TimeSpan.Zero));
            Assert.Empty(db.Versions("nuget", "pending-test"));
            db.PrepareQuarantine(new QuarantineEvidence(key, "recovery/quarantine/fixture/" + key,
                new string('a', 64), "test-etag", DateTimeOffset.UtcNow.ToString("O"), false));
            Assert.Equal(1, recovery.StaleUnresolvedCount());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("feed:alpha-feed:read", true)]
    [InlineData("feed:alpha-feed:publish", true)]
    [InlineData("feed:alpha-feed:admin", false)]
    [InlineData("feed:../test:read", false)]
    [InlineData("feed:alpha-feed:write", false)]
    [InlineData("nuget:demo.*", false)]
    [InlineData("feed:alpha-feed:read:admin", false)]
    [InlineData("feed:default:read", true)]
    public void FeedGrantSyntaxIsSeparateFromLegacyNamespaceGrants(
        string grant, bool accepted)
    {
        Assert.Equal(accepted, Access.ValidFeedGrant(grant));
        if (accepted)
            Assert.False(Access.MatchesNamespace(grant, "nuget", "Private.Package"));
    }

    [Fact]
    public void OidcFeedClaimsAreNarrowedToSignedAudienceAndScopedOperations()
    {
        var cfg = new Settings
        {
            OidcAuthority = "https://identity.example.invalid/realms/fixture",
            OidcAudience = "packory-api",
            AnonymousRead = true
        };
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var identity = new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim("scope", "packory.read packory.write"),
            new System.Security.Claims.Claim("packory_feed", "alpha-feed:read")
        }, "signed-test-fixture");
        context.User = new System.Security.Claims.ClaimsPrincipal(identity);
        Assert.Null(Access.RequireFeed(context, cfg, "alpha-feed"));
        Assert.NotNull(Access.RequireFeed(context, cfg, "alpha-feed", publish: true));
        Assert.NotNull(Access.RequireFeed(context, cfg, "beta-feed"));
        identity.AddClaim(new System.Security.Claims.Claim("packory_feed",
            "alpha-feed:publish"));
        Assert.Null(Access.RequireFeed(context, cfg, "alpha-feed", publish: true));
        // No authorization simply because a user is authenticated.
        context.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim("scope", "openid")
            }, "signed-test-fixture"));
        Assert.NotNull(Access.RequireFeed(context, cfg, "alpha-feed"));
    }

}
