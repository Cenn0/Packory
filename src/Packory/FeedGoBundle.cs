using System.IO.Compression;
using System.Security.Cryptography;

namespace Packory;

/// <summary>
/// Reconstruct an immutable Go triplet ledger from the verified, single-object
/// feed bundle. The independent feed asset SHA-256 is authoritative; derived
/// inner lengths and hashes are used only after that bundle has been verified.
/// </summary>
public static class FeedGoBundle
{
    private const string MediaType = "application/vnd.packory.go-module-bundle+zip";

    public static FeedAssetEntry Entry(string feedId, string module,
        string version, GoModuleBundle.Package package) =>
        new(feedId, "go", FeedAssetEntry.GoPath(module, version),
            module, version, MediaType, package.BundleSha256,
            package.Bundle.LongLength, package.PublishedAt);

    public static string BundleKey(string feedId, string module, string version)
    {
        if (!FeedDefinition.ValidId(feedId) ||
            !GoModuleBundle.ValidCoordinates(module, version))
            throw new ArgumentException("Invalid Go feed coordinate");
        static string Hash(string text) =>
            Convert.ToHexStringLower(SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(text)));
        return "feeds/" + feedId + "/go/" +
            Hash(module) + "/" + Hash(version) + ".bundle";
    }

    public static bool Valid(FeedAssetEntry item) =>
        item.Kind == "go" &&
        GoModuleBundle.ValidCoordinates(item.Name, item.Version) &&
        item.Path == FeedAssetEntry.GoPath(item.Name, item.Version) &&
        item.MediaType == MediaType &&
        item.Size <= GoModuleBundle.MaxBundleBytes;

    public static GoModuleRevision Ledger(FeedAssetEntry item, byte[] verifiedBundle)
    {
        if (!FeedAssetEntry.Valid(item) || item.Kind != "go" ||
            verifiedBundle.LongLength != item.Size ||
            !Convert.ToHexStringLower(SHA256.HashData(verifiedBundle))
                .Equals(item.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("Go feed bundle digest differs from committed ledger.");

        using var archive = new ZipArchive(
            new MemoryStream(verifiedBundle, writable: false), ZipArchiveMode.Read);
        if (archive.Entries.Count != 3 ||
            archive.Entries.Any(e => e.FullName is not ("info" or "mod" or "zip")) ||
            archive.Entries.Select(e => e.FullName)
                .Distinct(StringComparer.Ordinal).Count() != 3)
            throw new InvalidDataException("Unexpected Go feed bundle structure.");

        static (string Sha, long Size) Inspect(ZipArchiveEntry? entry, long limit)
        {
            if (entry is null || entry.Length is <= 0 || entry.Length > limit)
                throw new InvalidDataException("Invalid Go bundle member size.");
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                if (memory.Length + count > limit)
                    throw new InvalidDataException("Go bundle expands past its limit.");
                memory.Write(buffer, 0, count);
            }
            if (memory.Length != entry.Length)
                throw new InvalidDataException("Go bundle member truncated.");
            return (Convert.ToHexStringLower(SHA256.HashData(memory.ToArray())),
                memory.Length);
        }

        var mod = Inspect(archive.GetEntry("mod"), GoModuleBundle.MaxModBytes);
        var zip = Inspect(archive.GetEntry("zip"), GoModuleZipInspector.MaxArchiveBytes);
        var revision = new GoModuleRevision(item.Name, item.Version, item.Sha256,
            item.Size, zip.Sha, zip.Size, mod.Sha, mod.Size, item.CreatedAt);
        if (!GoModuleBundle.ValidRecord(revision))
            throw new InvalidDataException("Go bundle metadata is inconsistent.");
        return revision;
    }
}
