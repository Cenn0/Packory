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

}
