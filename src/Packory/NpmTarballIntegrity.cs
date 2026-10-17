using System.Security.Cryptography;
using System.Text.Json;

namespace Packory;

/// <summary>
/// Validates immutable npm tarballs against recorded publication metadata.
/// A digest derived from the file being served is never treated as an expected digest.
/// </summary>
public static class NpmTarballIntegrity
{
    public sealed record ExpectedDigest(string Algorithm, byte[] Bytes);

    public static ExpectedDigest? ParseExpected(string? json, string name, string version, bool packument)
    {
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (!Unique(root, "name", out var rootName) ||
                rootName.ValueKind != JsonValueKind.String || rootName.GetString() != name)
                return null;
            if (packument)
            {
                if (!Unique(root, "versions", out var versions) ||
                    !Unique(versions, version, out root)) return null;
            }
            if (!Unique(root, "name", out var package) || package.ValueKind != JsonValueKind.String ||
                package.GetString() != name ||
                !Unique(root, "version", out var release) || release.ValueKind != JsonValueKind.String ||
                release.GetString() != version ||
                !Unique(root, "dist", out var dist)) return null;

            // When integrity is supplied it is authoritative. Never downgrade a
            // malformed SHA-512 claim to a weaker shasum.
            if (Unique(dist, "integrity", out var integrity))
            {
                if (integrity.ValueKind != JsonValueKind.String) return null;
                var sri = integrity.GetString()!;
                if (!sri.StartsWith("sha512-", StringComparison.Ordinal)) return null;
                try
                {
                    var digest = Convert.FromBase64String(sri[7..]);
                    return digest.Length == 64 && Convert.ToBase64String(digest) == sri[7..]
                        ? new ExpectedDigest("sha512", digest) : null;
                }
                catch (FormatException) { return null; }
            }

            // Some previously cached npm metadata contains only legacy shasum.
            if (!Unique(dist, "shasum", out var checksum) || checksum.ValueKind != JsonValueKind.String)
                return null;
            var hex = checksum.GetString()!;
            if (hex.Length != 40 || !hex.All(c => c is >= '0' and <= '9' or
                >= 'a' and <= 'f' or >= 'A' and <= 'F')) return null;
            return new ExpectedDigest("sha1", Convert.FromHexString(hex));
        }
        catch (JsonException) { return null; }
    }

    public static async Task<bool> VerifyAsync(string file, string origin, string name,
        string version, IPackageDatabase db, BlobStore blobs, Settings settings, CancellationToken ct)
    {
        string? json;
        if (origin == "hosted")
        {
            json = db.NpmVersion(name, version);
        }
        else if (origin == "mirror")
        {
            var metadata = blobs.Existing("npm-metadata/" + Names.HashName(name) + ".json");
            if (metadata is null) return false;
            try
            {
                var info = new FileInfo(metadata);
                if (info.Length > settings.MaxMetadataBytes) return false;
                json = await File.ReadAllTextAsync(metadata, ct);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        else return false;

        var expected = ParseExpected(json, name, version, packument: origin == "mirror");
        if (expected is null) return false;
        try
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = expected.Algorithm == "sha512"
                ? await SHA512.HashDataAsync(stream, ct)
                : await SHA1.HashDataAsync(stream, ct);
            return CryptographicOperations.FixedTimeEquals(actual, expected.Bytes);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool Unique(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        var found = false;
        foreach (var property in obj.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found) return false;
            found = true;
            value = property.Value;
        }
        return found;
    }
}
