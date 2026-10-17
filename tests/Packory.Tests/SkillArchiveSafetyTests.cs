using System.IO.Compression;
using System.Text;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class SkillArchiveSafetyTests
{
    private const string Markdown =
        "---\nname: hello-world\ndescription: Test skill\n---\n# Test\n";

    private static byte[] Zip(params (string Name, byte[] Content)[] files)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var output = archive.CreateEntry(name).Open();
                output.Write(content);
            }
        }
        return buffer.ToArray();
    }

    private static (string Name, byte[] Content) Entry(string name, string content) =>
        (name, Encoding.UTF8.GetBytes(content));

    [Fact]
    public void ValidSkillAndSupportingFilesRemainAccepted()
    {
        var data = Zip(Entry("hello-world/SKILL.md", Markdown),
            Entry("hello-world/references/guide.md", "# Safe reference"),
            Entry("hello-world/scripts/example.sh", "echo example"));
        var manifest = SkillEndpoints.InspectZip(data);
        Assert.Equal("hello-world", manifest.Name);
        Assert.Equal("Test skill", manifest.Description);
    }

    [Theory]
    [InlineData("hello-world/readme.txt", "hello-world/readme.txt")]
    [InlineData("hello-world/Notes.txt", "hello-world/notes.txt")]
    [InlineData("hello-world/scripts/a.sh", "hello-world/scripts/A.sh")]
    public void DuplicateOrCaseConflictingPathsAreRejected(string first, string second)
    {
        var data = Zip(Entry("hello-world/SKILL.md", Markdown),
            Entry(first, "one"), Entry(second, "two"));
        Assert.Throws<InvalidDataException>(() => SkillEndpoints.InspectZip(data));
    }

    [Theory]
    [InlineData("hello-world/a\nb")]
    [InlineData("hello-world/notes\t.txt")]
    [InlineData("hello-world/scripts//tool.sh")]
    [InlineData("hello-world/../other.sh")]
    public void DangerousOrAmbiguousPathsAreRejected(string name)
    {
        var data = Zip(Entry("hello-world/SKILL.md", Markdown), Entry(name, "no"));
        Assert.Throws<InvalidDataException>(() => SkillEndpoints.InspectZip(data));
    }

    [Fact]
    public void DuplicateFrontmatterClaimsAreRejected()
    {
        var duplicateName =
            "---\nname: hello-world\nname: hello-world\ndescription: Test skill\n---\n# test\n";
        var duplicateDescription =
            "---\nname: hello-world\ndescription: Test skill\ndescription: Other\n---\n# test\n";
        Assert.Throws<InvalidDataException>(() =>
            SkillEndpoints.InspectZip(Zip(Entry("hello-world/SKILL.md", duplicateName))));
        Assert.Throws<InvalidDataException>(() =>
            SkillEndpoints.InspectZip(Zip(Entry("hello-world/SKILL.md", duplicateDescription))));
    }

    [Fact]
    public void InvalidUtf8ManifestIsNotSilentlyReplaced()
    {
        var invalid = Encoding.UTF8.GetBytes(Markdown).Concat(new byte[] { 0xc3, 0x28 }).ToArray();
        Assert.Throws<InvalidDataException>(() =>
            SkillEndpoints.InspectZip(Zip(("hello-world/SKILL.md", invalid))));
    }

    [Fact]
    public void PerArchiveUncompressedBudgetAppliesBeforeDecompression()
    {
        var oversized = new byte[20 * 1024 * 1024 + 1];
        var data = Zip(Entry("hello-world/SKILL.md", Markdown),
            ("hello-world/oversized.data", oversized));
        Assert.Throws<InvalidDataException>(() => SkillEndpoints.InspectZip(data));
    }

    [Fact]
    public void SymlinksAreRejectedEvenIfNamesAreHarmless()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var output = archive.CreateEntry("hello-world/SKILL.md").Open())
                output.Write(Encoding.UTF8.GetBytes(Markdown));
            var link = archive.CreateEntry("hello-world/latest");
            link.ExternalAttributes = unchecked((int)(0xA000 << 16));
            using var outputLink = link.Open();
            outputLink.Write(Encoding.UTF8.GetBytes("SKILL.md"));
        }
        Assert.Throws<InvalidDataException>(() => SkillEndpoints.InspectZip(buffer.ToArray()));
    }
}
