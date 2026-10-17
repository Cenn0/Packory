using System.Security.Cryptography;
using System.Text.Json;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class ArtifactDigestTests
{
    private static string Row(string name, string version, string digest) =>
        JsonSerializer.Serialize(new { name, vers = version, cksum = digest });

    [Fact]
    public void CargoDigestIsBoundToExactIdentityAndVersion()
    {
        var first = new string('a', 64);
        var second = new string('b', 64);
        var rows = new[] { Row("demo", "1.0.0", first), Row("demo", "2.0.0", second) };
        Assert.Equal(first, ArtifactDigests.CargoExpected(rows, "demo", "1.0.0"));
        Assert.Equal(second, ArtifactDigests.CargoExpected(rows, "demo", "2.0.0"));
        Assert.Null(ArtifactDigests.CargoExpected(rows, "demo", "3.0.0"));
        Assert.Null(ArtifactDigests.CargoExpected(rows, "other", "1.0.0"));
        Assert.Null(ArtifactDigests.CargoExpected([rows[0], rows[0]], "demo", "1.0.0"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{broken")]
    [InlineData("{\"name\":\"demo\",\"vers\":\"1.0.0\",\"cksum\":42}")]
    [InlineData("{\"name\":\"demo\",\"vers\":\"1.0.0\",\"cksum\":\"invalid\"}")]
    public void CorruptedIndexMetadataCannotSupplyAnIntegrityClaim(string row) =>
        Assert.Null(ArtifactDigests.CargoExpected([row], "demo", "1.0.0"));

    [Fact]
    public void DuplicateSemanticMetadataFieldsAreNotLastValueWins()
    {
        var digest = new string('a', 64);
        var row = "{\"name\":\"demo\",\"name\":\"demo\",\"vers\":\"1.0.0\",\"cksum\":\"" + digest + "\"}";
        Assert.Null(ArtifactDigests.CargoExpected([row], "demo", "1.0.0"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0000")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void InvalidHashClaimsAreRejected(string? value) => Assert.False(ArtifactDigests.ValidSha256(value));

    [Fact]
    public async Task HashAndMetadataSizeMustBothMatchAndIoFailureIsClosed()
    {
        var path = Path.Combine(Path.GetTempPath(), "packory-digest-" + Guid.NewGuid().ToString("N"));
        var bytes = new byte[] { 1, 2, 3 };
        var expected = Convert.ToHexStringLower(SHA256.HashData(bytes));
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            Assert.True(await ArtifactDigests.MatchesSha256Async(path, expected, 3, CancellationToken.None));
            Assert.False(await ArtifactDigests.MatchesSha256Async(path, expected, 2, CancellationToken.None));
            Assert.False(await ArtifactDigests.MatchesSha256Async(path, expected, -1, CancellationToken.None));
            await File.WriteAllBytesAsync(path, [3, 2, 1]);
            Assert.False(await ArtifactDigests.MatchesSha256Async(path, expected, 3, CancellationToken.None));
            File.Delete(path);
            Assert.False(await ArtifactDigests.MatchesSha256Async(path, expected, null, CancellationToken.None));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\t")]
    [InlineData("\0")]
    [InlineData("\u2028")]
    public void CargoAndPythonIdentifiersRejectTrailingControls(string suffix)
    {
        Assert.False(CargoEndpoints.IsName("demo" + suffix));
        Assert.False(CargoEndpoints.IsVersion("1.0.0" + suffix));
        Assert.Null(PyPiEndpoints.Normalize("Demo_Package" + suffix));
        Assert.False(PyPiEndpoints.IsFilename("demo-1.0.0-py3-none-any.whl" + suffix));
    }

    [Fact]
    public void ValidNativeNamesAndFilenameBoundsArePreserved()
    {
        Assert.True(CargoEndpoints.IsName("demo_crate"));
        Assert.True(CargoEndpoints.IsVersion("1.0.0-beta.1+build"));
        Assert.False(CargoEndpoints.IsName(null));
        Assert.False(CargoEndpoints.IsVersion(null));
        Assert.Null(PyPiEndpoints.Normalize(null));
        Assert.Equal("demo-package", PyPiEndpoints.Normalize("Demo__Package"));
        Assert.True(PyPiEndpoints.IsFilename(new string('a', 251) + ".whl"));
        Assert.False(PyPiEndpoints.IsFilename(new string('a', 252) + ".whl"));
        Assert.False(PyPiEndpoints.IsFilename(null));
    }
}
