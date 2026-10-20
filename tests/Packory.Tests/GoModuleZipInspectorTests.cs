using System.IO.Compression;
using System.Text;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class GoModuleZipInspectorTests
{
    private const string Request = "example.com/mod/@v/v1.2.3.zip";
    private const string Prefix = "example.com/mod@v1.2.3/";

    private static byte[] Zip(params (string Path, byte[] Bytes)[] files)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files)
            {
                var entry = zip.CreateEntry(path);
                using var writer = entry.Open();
                writer.Write(bytes);
            }
        }
        return buffer.ToArray();
    }

    private static (string Path, byte[] Bytes) File(string name, string contents) =>
        (name, Encoding.UTF8.GetBytes(contents));

    private static void MustReject(byte[] archive, string coordinate = Request) =>
        Assert.Throws<InvalidDataException>(() =>
            GoModuleZipInspector.Inspect(new MemoryStream(archive), coordinate));

    [Fact]
    public void AcceptsVersionedModuleWithoutRunningAnything()
    {
        var bytes = Zip(File(Prefix + "go.mod", "module example.com/mod\n\ngo 1.23\n"),
            File(Prefix + "lib/main.go", "package lib\n"),
            File(Prefix + "LICENSE", "sample"));
        using var input = new MemoryStream(bytes);
        input.Position = 7;
        var result = GoModuleZipInspector.Inspect(input, Request);
        Assert.Equal("example.com/mod", result.ModulePath);
        Assert.Equal("v1.2.3", result.Version);
        Assert.Equal(3, result.Files);
        Assert.True(result.HasGoMod);
        Assert.Equal(64, result.ZipSha256.Length);
        Assert.Equal("h1:fDumM9tojWfK3amoLyIreXFecxVda9OvhNSFQOYn7Hk=", result.ZipH1);
        Assert.Equal(7, input.Position);
    }

    [Fact]
    public void GoH1IsIndependentOfZipEntryOrderButBoundToFileContents()
    {
        var first = File(Prefix + "go.mod", "module example.com/mod\n\ngo 1.23\n");
        var second = File(Prefix + "lib/main.go", "package lib\n");
        var ordered = GoModuleZipInspector.Inspect(
            new MemoryStream(Zip(first, second)), Request);
        var reversed = GoModuleZipInspector.Inspect(
            new MemoryStream(Zip(second, first)), Request);
        var tampered = GoModuleZipInspector.Inspect(
            new MemoryStream(Zip(first, File(Prefix + "lib/main.go", "package other\n"))),
            Request);
        Assert.Equal(ordered.ZipH1, reversed.ZipH1);
        Assert.NotEqual(ordered.ZipSha256, reversed.ZipSha256);
        Assert.NotEqual(ordered.ZipH1, tampered.ZipH1);
    }

    [Fact]
    public void RootGoModIsOptionalAccordingToGoFormat()
    {
        var report = GoModuleZipInspector.Inspect(
            new MemoryStream(Zip(File(Prefix + "main.go", "package mod\n"))), Request);
        Assert.False(report.HasGoMod);
    }

    [Theory]
    [InlineData("../file.go")]
    [InlineData("sub/../../file.go")]
    [InlineData("nested//file.go")]
    [InlineData("sub\\evil.go")]
    [InlineData("C:/file.go")]
    [InlineData("CON.txt")]
    [InlineData("lib/com1.go")]
    [InlineData("lib/file.")]
    [InlineData("lib/file ")]
    [InlineData("lib/\u00e4.go")]
    [InlineData("sub/go.mod")]
    [InlineData("sub/Go.mod")]
    [InlineData("Go.mod")]
    [InlineData("empty/")]
    public void RejectsUnsafeOrUnsupportedEntries(string path)
    {
        MustReject(Zip(File(Prefix + "main.go", "package mod"),
            File(Prefix + path, "x")));
    }

    [Fact]
    public void RejectsMixedCaseDuplicates()
    {
        MustReject(Zip(File(Prefix + "lib/a.go", "package lib"),
            File(Prefix + "lib/A.go", "package lib")));
    }

    [Theory]
    [InlineData("parent", "parent/file.go")]
    [InlineData("parent/file.go", "parent")]
    public void RejectsFileDirectoryCollisions(string first, string second)
    {
        MustReject(Zip(File(Prefix + first, "test"), File(Prefix + second, "test")));
    }

    [Theory]
    [InlineData("example.com/other@v1.2.3/main.go")]
    [InlineData("example.com/mod@v1.2.4/main.go")]
    [InlineData("example.com/mod@v1.2.3/../main.go")]
    public void RejectsInvalidModulePrefix(string path) =>
        MustReject(Zip(File(path, "package mod")));

    [Theory]
    [InlineData("module example.com/other\n")]
    [InlineData("module example.com/mod\nmodule example.com/mod\n")]
    [InlineData("module \"example.com/mod\"\n")]
    [InlineData("module example.com/mod/evil\n")]
    public void RejectsAmbiguousGoModIdentity(string contents) =>
        MustReject(Zip(File(Prefix + "go.mod", contents)));

    [Fact]
    public void RejectsInvalidUtf8InGoMod()
    {
        MustReject(Zip((Prefix + "go.mod", new byte[] { 0x6d, 0x6f, 0x64, 0x75,
            0x6c, 0x65, 0x20, 0xc3, 0x28 })));
    }

    [Fact]
    public void RejectsDeclaredGoModAboveBudget()
    {
        MustReject(Zip((Prefix + "go.mod",
            new byte[GoModuleZipInspector.MaxGoModBytes + 1])));
    }

    [Fact]
    public void RejectsUnixSymlinkEntries()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry(Prefix + "link");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var output = entry.Open();
            output.Write(Encoding.UTF8.GetBytes("target"));
        }
        MustReject(buffer.ToArray());
    }

    [Fact]
    public void RejectsUnseekableAndNoncanonicalCoordinates()
    {
        var data = Zip(File(Prefix + "main.go", "package mod"));
        using var input = new MemoryStream(data);
        Assert.Throws<ArgumentException>(() =>
            GoModuleZipInspector.Inspect(input, "example.com/mod/@v/v1.2.3.info"));
        Assert.Throws<ArgumentException>(() =>
            GoModuleZipInspector.Inspect(input, "example.com/mod/@v/v2.0.0.zip"));
    }
}
