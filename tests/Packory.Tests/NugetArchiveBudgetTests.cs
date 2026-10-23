using System.Buffers.Binary;
using System.IO.Compression;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class NugetArchiveBudgetTests
{
    private const string Manifest = "<package><metadata><id>Bounded.Test</id><version>1.0.0</version><description>Inert fixture</description></metadata></package>";

    private static byte[] Package(string manifest = Manifest, int extraEntries = 0)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("Bounded.Test.nuspec").Open()))
                writer.Write(manifest);
            for (var index = 0; index < extraEntries; index++)
                zip.CreateEntry($"files/{index:D5}.txt");
        }
        return output.ToArray();
    }

    [Fact]
    public void NormalPackageStillParses()
    {
        using var input = new MemoryStream(Package(extraEntries: 2));
        var nuspec = NuGetEndpoints.ReadNuspec(input);
        Assert.Equal("Bounded.Test", nuspec.Id);
        Assert.Equal("1.0.0", nuspec.Version);
    }

    [Fact]
    public void EntryCountAboveProfileIsRejectedBeforeEnumeration()
    {
        using var input = new MemoryStream(Package(extraEntries: 10_000));
        Assert.Throws<InvalidDataException>(() => NuGetEndpoints.ReadNuspec(input));
    }

    [Fact]
    public void ForgedSmallTrailerCountCannotBypassActualEntryBudget()
    {
        var bytes = Package(extraEntries: 10_000);
        var trailer = bytes.AsSpan(bytes.Length - 22);
        Assert.Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(trailer));
        BinaryPrimitives.WriteUInt16LittleEndian(trailer[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(trailer[10..], 1);
        using var input = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => NuGetEndpoints.ReadNuspec(input));
    }

    [Fact]
    public void ForgedDirectorySizeIsRejectedBeforeEnumeration()
    {
        var bytes = Package();
        var trailer = bytes.AsSpan(bytes.Length - 22);
        Assert.Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(trailer));
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[12..], 4 * 1024 * 1024 + 1);
        using var input = new MemoryStream(bytes);
        Assert.Throws<InvalidDataException>(() => NuGetEndpoints.ReadNuspec(input));
    }

    [Fact]
    public void OverstatedCompressedRatioCannotReturnLargeNuspec()
    {
        var xml = Manifest.Replace("Inert fixture", new string('a', 1024 * 1024 + 1));
        var bytes = Package(xml);
        var found = false;
        for (var offset = 0; offset <= bytes.Length - 46; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)) != 0x02014b50u)
                continue;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 24, 4), 64);
            found = true;
            break;
        }
        Assert.True(found);
        using var input = new MemoryStream(bytes);
        var error = Assert.Throws<InvalidDataException>(() => NuGetEndpoints.ReadNuspec(input));
        Assert.Equal("Nuspec too large", error.Message);
    }
}
