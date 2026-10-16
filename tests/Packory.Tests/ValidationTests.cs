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

    [Fact]
    public void OptionalOidc_RejectsInsecureOrIncompleteIssuerConfiguration()
    {
        new OidcOptions().Validate(); // optional provider, no runtime requirement
        Assert.Throws<InvalidOperationException>(() => new OidcOptions
        {
            Authority = "http://localhost:8080/realms/demo", ClientId = "packory", ClientSecret = new string('x',32)
        }.Validate());
        Assert.Throws<InvalidOperationException>(() => new OidcOptions
        {
            Authority = "https://id.example.invalid/realms/demo", ClientId = "packory"
        }.Validate());
        new OidcOptions
        {
            Authority = "https://id.example.invalid/realms/demo", ClientId = "packory", ClientSecret = new string('x',32)
        }.Validate();
    }
}
