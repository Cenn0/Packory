using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class MavenCoordinatesTests
{
    [Theory]
    [InlineData("org/example/library/1.2.3/library-1.2.3.jar", "org.example", "library", "1.2.3", null, "jar")]
    [InlineData("org/example/library/1.2.3/library-1.2.3-sources.jar", "org.example", "library", "1.2.3", "sources", "jar")]
    [InlineData("com/acme/tool/2.0/tool-2.0.pom", "com.acme", "tool", "2.0", null, "pom")]
    public void CanonicalPathsRoundTrip(string path, string group, string name,
        string version, string? classifier, string extension)
    {
        Assert.True(MavenCoordinates.TryParseArtifactPath(path, out var artifact));
        Assert.NotNull(artifact);
        Assert.Equal(group, artifact!.GroupId);
        Assert.Equal(name, artifact.ArtifactId);
        Assert.Equal(version, artifact.Version);
        Assert.Equal(classifier, artifact.Classifier);
        Assert.Equal(extension, artifact.Extension);
        Assert.Equal(path, MavenCoordinates.RepositoryPath(artifact));
    }

    [Theory]
    [InlineData("../escape/tool/1.0/tool-1.0.jar")]
    [InlineData("com//tool/1.0/tool-1.0.jar")]
    [InlineData("com/acme/tool/1.0/other-1.0.jar")]
    [InlineData("com/acme/tool/1.0/tool-1.0.jar%2f..")]
    [InlineData("com.acme/tool/1.0/tool-1.0.jar")]
    [InlineData("com/acme/tool/1.0/tool-1.0.jar/")]
    [InlineData("com/acme/tool/1.0/tool-1.0-.jar")]
    [InlineData("com/acme/tool/1.0/tool-1.0.jar\n")]
    [InlineData("com\\acme\\tool\\1.0\\tool-1.0.jar")]
    public void RejectsNonCanonicalOrUnsafePaths(string path) =>
        Assert.False(MavenCoordinates.TryParseArtifactPath(path, out _));

    [Fact]
    public void RejectsMalformedCoordinatesBeforeBuildingPaths()
    {
        Assert.Throws<ArgumentException>(() => MavenCoordinates.RepositoryPath(
            new MavenCoordinates.Artifact("com..example", "lib", "1.0", null, "jar")));
        Assert.False(MavenCoordinates.TryParseArtifactPath(null, out _));
    }
}
