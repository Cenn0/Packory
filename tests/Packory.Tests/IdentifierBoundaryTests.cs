using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class IdentifierBoundaryTests
{
    private static bool Valid(string kind, string? value) => kind switch
    {
        "nuget" => Names.Nuget(value),
        "version" => Names.Version(value),
        "npm" => Names.Npm(value),
        "skill" => Names.Skill(value),
        _ => throw new ArgumentException("Unknown fixture kind")
    };

    [Theory]
    [InlineData("nuget", "Internal.Package_1")]
    [InlineData("version", "1.2.3-alpha.4+build")]
    [InlineData("npm", "@team/demo-package")]
    [InlineData("skill", "demo-skill")]
    public void ValidGrammarIsPreservedButControlsCannotExtendAnIdentifier(string kind, string name)
    {
        Assert.True(Valid(kind, name));
        foreach (var control in new[] { "\n", "\r", "\r\n", "\t", "\0", "\u007f", "\u0085", "\u2028", "\u2029", " " })
        {
            Assert.False(Valid(kind, name + control));
            Assert.False(Valid(kind, control + name));
            Assert.False(Valid(kind, name + control + "x"));
        }
    }

    [Theory]
    [InlineData("nuget")]
    [InlineData("version")]
    [InlineData("npm")]
    [InlineData("skill")]
    public void MissingAndEmptyNamesAreRejectedWithoutExceptions(string kind)
    {
        Assert.False(Valid(kind, null));
        Assert.False(Valid(kind, ""));
        Assert.False(Valid(kind, "../elsewhere"));
        Assert.False(Valid(kind, "name%0a"));
    }

    [Fact]
    public void IdentifierLengthLimitsAreUnchanged()
    {
        Assert.True(Names.Nuget(new string('a', 128)));
        Assert.False(Names.Nuget(new string('a', 129)));
        Assert.True(Names.Version(new string('1', 128)));
        Assert.False(Names.Version(new string('1', 129)));
        Assert.True(Names.Skill(new string('a', 64)));
        Assert.False(Names.Skill(new string('a', 65)));
        Assert.True(Names.Npm("@" + new string('a', 101) + "/" + new string('b', 101)));
        Assert.False(Names.Npm("@" + new string('a', 102) + "/b"));
    }

    [Theory]
    [InlineData("bucket")]
    [InlineData("region")]
    [InlineData("prefix")]
    public void S3IdentifiersDoNotAcceptControlSuffixes(string target)
    {
        var valid = new Settings { BlobProvider = "s3", S3Bucket = "fixture-bucket" };
        valid.Validate();
        foreach (var control in new[] { "\n", "\r\n", "\t", "\0", "\u2028" })
        {
            var cfg = new Settings { BlobProvider = "s3", S3Bucket = "fixture-bucket" };
            switch (target)
            {
                case "bucket": cfg.S3Bucket += control; break;
                case "region": cfg.S3Region += control; break;
                case "prefix": cfg.S3KeyPrefix += control; break;
            }
            Assert.Throws<InvalidOperationException>(cfg.Validate);
        }
    }

    [Fact]
    public void NamespaceReservationsDoNotSilentlyAcceptTrailingLineFeeds()
    {
        var reserved = new Settings { ReservedNugetPrefixes = ["internal.\n"] };
        Assert.Throws<InvalidOperationException>(reserved.Validate);
        var denied = new Settings { DeniedNugetPrefixes = ["blocked.\n"] };
        Assert.Throws<InvalidOperationException>(denied.Validate);
    }
}
