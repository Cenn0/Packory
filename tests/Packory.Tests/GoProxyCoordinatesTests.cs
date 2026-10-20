using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class GoProxyCoordinatesTests
{
    [Theory]
    [InlineData("github.com/!azure/sdk", "github.com/Azure/sdk")]
    [InlineData("golang.org/x/mod", "golang.org/x/mod")]
    [InlineData("example.com/service/v2", "example.com/service/v2")]
    [InlineData("gopkg.in/yaml.v3", "gopkg.in/yaml.v3")]
    public void ModulePathsDecodeAndReencodeCanonically(string encoded, string expected)
    {
        Assert.True(GoProxyCoordinates.TryDecodeModulePath(encoded, out var decoded));
        Assert.Equal(expected, decoded);
        Assert.Equal(encoded, GoProxyCoordinates.EncodeModulePath(decoded));
    }

    [Theory]
    [InlineData("github.com/Azure/sdk")]
    [InlineData("github.com/!Azure/sdk")]
    [InlineData("github.com/!0/sdk")]
    [InlineData("github.com/!")]
    [InlineData("github.com//sdk")]
    [InlineData("github.com/../sdk")]
    [InlineData("github.com/.hidden")]
    [InlineData("github.com/trailing.")]
    [InlineData("github.com/foo%2fbar")]
    [InlineData("github.com\\foo")]
    [InlineData("github.com/CON/test")]
    [InlineData("github.com/com1/settings")]
    [InlineData("github.com/EXAMPL~1/test")]
    [InlineData("example.com/v01")]
    [InlineData("example.com/v1")]
    [InlineData("example.com/v2.0")]
    [InlineData("localhost/missing-domain")]
    public void RejectsNonCanonicalOrUnsafeModulePaths(string encoded) =>
        Assert.False(GoProxyCoordinates.TryDecodeModulePath(encoded, out _));

    [Theory]
    [InlineData("golang.org/x/mod/@v/list", "golang.org/x/mod", "list", null)]
    [InlineData("github.com/!azure/sdk/@v/v1.2.3.info", "github.com/Azure/sdk", "info", "v1.2.3")]
    [InlineData("github.com/!azure/sdk/@v/v1.2.3.mod", "github.com/Azure/sdk", "mod", "v1.2.3")]
    [InlineData("github.com/!azure/sdk/@v/v1.2.3.zip", "github.com/Azure/sdk", "zip", "v1.2.3")]
    [InlineData("example.com/foo/v2/@v/v2.3.0.info", "example.com/foo/v2", "info", "v2.3.0")]
    [InlineData("gopkg.in/yaml.v3/@v/v3.0.0.mod", "gopkg.in/yaml.v3", "mod", "v3.0.0")]
    [InlineData("example.com/oldmodule/@v/v2.0.0+incompatible.mod", "example.com/oldmodule", "mod", "v2.0.0+incompatible")]
    [InlineData("example.com/foo/@v/v0.2.0-!beta.1.info", "example.com/foo", "info", "v0.2.0-Beta.1")]
    public void ParsesGoproxyRequestsWithoutImplicitUpstream(string path, string module,
        string operation, string? version)
    {
        Assert.True(GoProxyCoordinates.TryParseRequest(path, out var request));
        Assert.NotNull(request);
        Assert.Equal(module, request!.ModulePath);
        Assert.Equal(operation, request.Operation);
        Assert.Equal(version, request.Version);
    }

    [Theory]
    [InlineData("example.com/foo/@v/v1.2.3.exe")]
    [InlineData("example.com/foo/@v/v1.2.3")]
    [InlineData("example.com/foo/@v/v01.2.3.info")]
    [InlineData("example.com/foo/@v/v1.2.3-01.info")]
    [InlineData("example.com/foo/@v/v1.2.3-!Beta.info")]
    [InlineData("example.com/foo/@v/v1.2.3-!0.info")]
    [InlineData("example.com/foo/v2/@v/v1.0.0.mod")]
    [InlineData("example.com/foo/v2/@v/v2.0.0+incompatible.mod")]
    [InlineData("gopkg.in/yaml.v3/@v/v2.1.0.info")]
    [InlineData("example.com/oldmodule/@v/v2.0.0.mod")]
    [InlineData("gopkg.in/yaml.v3/@v/v3.0.0+incompatible.mod")]
    [InlineData("example.com/foo/@latest")]
    [InlineData("example.com/foo/@v/..")]
    [InlineData("example.com/foo/@v/list?x=1")]
    [InlineData("example.com/foo/@v/list#fragment")]
    [InlineData("/example.com/foo/@v/list")]
    [InlineData("example.com/foo/@v/")]
    public void RejectsInvalidGoproxyRequests(string request) =>
        Assert.False(GoProxyCoordinates.TryParseRequest(request, out _));

    [Theory]
    [InlineData("v0.0.0", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("v2.0.0+incompatible", true)]
    [InlineData("v1.2.3-alpha.1", true)]
    [InlineData("v1.2.3-01", false)]
    [InlineData("v01.2.3", false)]
    [InlineData("v1.02.3", false)]
    [InlineData("v1.2", false)]
    [InlineData("v1.2.3+anything", false)]
    [InlineData("../v1.2.3", false)]
    public void VersionSubsetIsExplicitAndConservative(string version, bool valid) =>
        Assert.Equal(valid, GoProxyCoordinates.ValidCanonicalVersion(version));
}
