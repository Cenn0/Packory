using System.Security.Cryptography;
using System.Text.Json;

namespace Packory;

/// <summary>Verify against publication-time metadata, never a hash learned from suspect bytes.</summary>
public static class ArtifactDigests
{
    public static async Task<bool> MatchesRemoteAsync(BlobStore blobs, string key,
        string algorithm, byte[] expected, long expectedSize, long maximum, CancellationToken ct)
    {
        if (expectedSize < 0 || expectedSize > maximum) return false;
        if (blobs is not S3BlobStore remote) return true;
        var probe = await remote.InspectRemoteAsync(key, algorithm, maximum, ct);
        return probe.Exists && !probe.ExceedsLimit && probe.Size == expectedSize &&
            probe.Digest is not null && CryptographicOperations.FixedTimeEquals(probe.Digest, expected);
    }

    public static bool ValidSha256(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string? CargoExpected(IEnumerable<string> rows, string name, string version)
    {
        string? result = null;
        try
        {
            foreach (var row in rows)
            {
                using var document = JsonDocument.Parse(row);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("name", out var package) || package.ValueKind != JsonValueKind.String ||
                    package.GetString() != name ||
                    !root.TryGetProperty("vers", out var release) || release.ValueKind != JsonValueKind.String)
                    return null;
                if (release.GetString() != version) continue;
                if (result is not null || !root.TryGetProperty("cksum", out var checksum) ||
                    checksum.ValueKind != JsonValueKind.String || !ValidSha256(checksum.GetString())) return null;
                // Duplicate semantic fields are ambiguous even when the JSON parser accepts them.
                if (root.EnumerateObject().Count(p => p.Name is "name" or "vers" or "cksum") != 3) return null;
                result = checksum.GetString();
            }
        }
        catch (JsonException) { return null; }
        return result;
    }

    public static async Task<bool> MatchesSha256Async(string path, string? expected,
        long? expectedSize, CancellationToken ct)
    {
        if (!ValidSha256(expected) || expectedSize < 0) return false;
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (expectedSize.HasValue && input.Length != expectedSize.Value) return false;
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
            return string.Equals(actual, expected, StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
