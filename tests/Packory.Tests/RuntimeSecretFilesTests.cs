using System.Text;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class RuntimeSecretFilesTests
{
    private static Func<string, string?> Variables(params (string Key, string Value)[] pairs)
    {
        var vars = pairs.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        return key => vars.TryGetValue(key, out var value) ? value : null;
    }

    [Theory]
    [InlineData("PACKORY_ADMIN_TOKEN")]
    [InlineData("PACKORY_READ_TOKEN")]
    [InlineData("PACKORY_WRITE_TOKEN")]
    [InlineData("PACKORY_POSTGRES_CONNECTION_STRING")]
    [InlineData("PACKORY_S3_ACCESS_KEY")]
    [InlineData("PACKORY_S3_SECRET_KEY")]
    public void ResolvesEachDocumentedFileSetting(string name)
    {
        using var file = new SecretFile("synthetic-value with spaces\r\n");
        var value = RuntimeSecretFiles.Resolve(name, null,
            getEnvironment: Variables((name + "_FILE", file.Path)));
        Assert.Equal("synthetic-value with spaces", value);
    }

    [Fact]
    public void LeavesMeaningfulTrailingSpaceAndAcceptsSingleUnixNewline()
    {
        using var file = new SecretFile("synthetic-secret \n");
        Assert.Equal("synthetic-secret ", RuntimeSecretFiles.Resolve("PACKORY_ADMIN_TOKEN", null,
            getEnvironment: Variables(("PACKORY_ADMIN_TOKEN_FILE", file.Path))));
    }

    [Fact]
    public void DirectSettingAndBoundConfigurationRemainCompatible()
    {
        Assert.Equal("synthetic-direct", RuntimeSecretFiles.Resolve("PACKORY_ADMIN_TOKEN", "synthetic-bound",
            blankEnvironmentMeansUnset: true,
            getEnvironment: Variables(("PACKORY_ADMIN_TOKEN", "synthetic-direct"))));
        Assert.Equal("synthetic-bound", RuntimeSecretFiles.Resolve("PACKORY_ADMIN_TOKEN", "synthetic-bound",
            blankEnvironmentMeansUnset: true,
            getEnvironment: Variables(("PACKORY_ADMIN_TOKEN", ""))));
        Assert.Equal("synthetic-bound", RuntimeSecretFiles.Resolve("PACKORY_ADMIN_TOKEN", "synthetic-bound",
            getEnvironment: Variables()));
    }

    [Fact]
    public void ConflictsAreRejectedEvenForEmptyDirectEnvironmentSetting()
    {
        using var file = new SecretFile("synthetic");
        Assert.Throws<InvalidOperationException>(() => RuntimeSecretFiles.Resolve("PACKORY_ADMIN_TOKEN", null,
            getEnvironment: Variables(("PACKORY_ADMIN_TOKEN", ""), ("PACKORY_ADMIN_TOKEN_FILE", file.Path))));
        Assert.Throws<InvalidOperationException>(() => RuntimeSecretFiles.Resolve("PACKORY_ADMIN_TOKEN", "synthetic-bound",
            getEnvironment: Variables(("PACKORY_ADMIN_TOKEN_FILE", file.Path))));
    }

    [Fact]
    public void MissingRelativeEmptyAndOversizedFilesFailWithoutContentOrPathLeakage()
    {
        using var empty = new SecretFile("");
        using var large = new SecretFile(new string('S', 16 * 1024 + 1));
        var paths = new[] { "/this/path/should/not/exist/packory", "./relative-file", "", empty.Path, large.Path };
        foreach (var path in paths)
        {
            var error = Assert.Throws<InvalidOperationException>(() => RuntimeSecretFiles.Resolve(
                "PACKORY_ADMIN_TOKEN", null, getEnvironment: Variables(("PACKORY_ADMIN_TOKEN_FILE", path))));
            Assert.Contains("PACKORY_ADMIN_TOKEN", error.Message);
            Assert.DoesNotContain(path.Length > 0 ? path : "NOT_PRESENT_IN_ERROR", error.Message);
        }
    }

    [Theory]
    [InlineData("first\nsecond")]
    [InlineData("first\n\n")]
    [InlineData("first\r")]
    [InlineData("first\u0000second")]
    [InlineData("\uFEFFsecret")]
    [InlineData(" \n")]
    public void MultilineOrMalformedTextIsRejected(string value)
    {
        using var file = new SecretFile(value);
        Assert.Throws<InvalidOperationException>(() => RuntimeSecretFiles.Resolve("PACKORY_WRITE_TOKEN", null,
            getEnvironment: Variables(("PACKORY_WRITE_TOKEN_FILE", file.Path))));
    }

    [Fact]
    public void InvalidUtf8IsRejectedAndNeverEchoed()
    {
        using var file = new SecretFile([0xc3, 0x28]);
        var error = Assert.Throws<InvalidOperationException>(() => RuntimeSecretFiles.Resolve(
            "PACKORY_ADMIN_TOKEN", null, getEnvironment: Variables(("PACKORY_ADMIN_TOKEN_FILE", file.Path))));
        Assert.Equal("Invalid or unreadable file-backed configuration for PACKORY_ADMIN_TOKEN.", error.Message);
    }

    [Fact]
    public void ExistingTokenUniquenessAndS3CredentialPairValidationApplyAfterResolution()
    {
        using var file = new SecretFile(new string('a', 48));
        var admin = RuntimeSecretFiles.Resolve("PACKORY_ADMIN_TOKEN", null,
            getEnvironment: Variables(("PACKORY_ADMIN_TOKEN_FILE", file.Path)));
        var settings = new Settings { AdminToken = admin };
        settings.Validate();

        settings.ReadToken = admin;
        Assert.Throws<InvalidOperationException>(settings.Validate);
        settings.ReadToken = null;

        settings.BlobProvider = "s3";
        settings.S3Bucket = "packory-test-bucket";
        settings.S3AccessKey = "synthetic-key";
        Assert.Throws<InvalidOperationException>(settings.Validate);
        settings.S3SecretKey = "synthetic-secret";
        settings.Validate();
    }

    [Fact]
    public void ChangedSecretIsLoadedOnlyOnTheNextExplicitResolution()
    {
        using var file = new SecretFile(new string('a', 48));
        var env = Variables(("PACKORY_ADMIN_TOKEN_FILE", file.Path));
        var first = RuntimeSecretFiles.Resolve("PACKORY_ADMIN_TOKEN", null, getEnvironment: env);
        file.Replace(new string('b', 48));
        Assert.Equal(new string('a', 48), first);
        Assert.Equal(new string('b', 48),
            RuntimeSecretFiles.Resolve("PACKORY_ADMIN_TOKEN", null, getEnvironment: env));
    }

    private sealed class SecretFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "packory-secret-" + Guid.NewGuid().ToString("N"));
        public SecretFile(string contents) => Replace(contents);
        public SecretFile(byte[] contents) => File.WriteAllBytes(Path, contents);
        public void Replace(string contents) => File.WriteAllText(Path, contents, new UTF8Encoding(false));
        public void Dispose() => File.Delete(Path);
    }
}
