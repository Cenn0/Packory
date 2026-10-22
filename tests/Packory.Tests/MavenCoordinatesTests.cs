using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class MavenCoordinatesTests
{
    [Theory]
    [InlineData("org/example/library/1.2-SNAPSHOT/library-1.2-20261022.101530-1.jar", null, "jar")]
    [InlineData("org/example/library/1.2-SNAPSHOT/library-1.2-20261022.101530-1-sources.jar", "sources", "jar")]
    [InlineData("org/example/library/1.2-SNAPSHOT/library-1.2-20261022.101530-1.tar.gz", null, "tar.gz")]
    public void TimestampedSnapshotsKeepBaseAndResolvedVersionsSeparate(string path,
        string? classifier, string extension)
    {
        Assert.True(MavenEndpoints.ValidAssetPath(path, out var artifact));
        Assert.NotNull(artifact);
        Assert.Equal("1.2-SNAPSHOT", artifact.Version);
        Assert.Equal("1.2-20261022.101530-1", artifact.ResolvedVersion);
        Assert.Equal(classifier, artifact.Classifier);
        Assert.Equal(extension, artifact.Extension);
        Assert.Equal(path, MavenCoordinates.RepositoryPath(artifact));
    }

    [Theory]
    [InlineData("1.2-SNAPSHOT", "1.2-SNAPSHOT")]
    [InlineData("1.2-SNAPSHOT", "1.2-20260230.101530-1")]
    [InlineData("1.2-SNAPSHOT", "1.2-20261022.251530-1")]
    [InlineData("1.2-SNAPSHOT", "1.2-20261022.101530-0")]
    [InlineData("1.2-SNAPSHOT", "1.2-20261022.101530-01")]
    [InlineData("1.2-SNAPSHOT", "1.2-20261022.101530-2147483648")]
    [InlineData("1.2-SNAPSHOT", "1.3-20261022.101530-1")]
    [InlineData("1.2-snapshot", "1.2-20261022.101530-1")]
    public void InvalidOrMutableSnapshotCoordinatesAreRejected(string version, string resolved)
    {
        Assert.False(MavenEndpoints.ValidAssetPath(
            $"org/example/library/{version}/library-{resolved}.jar", out _));
    }

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
