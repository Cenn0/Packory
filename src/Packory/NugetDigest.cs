using System.Security.Cryptography;

namespace Packory;

/// <summary>
/// Publication-time SHA-512 of the exact NuGet artifact bytes. An absent row
/// denotes a legacy revision, never a successful integrity verification.
/// </summary>
public sealed record NugetDigest(string Sha512, long Size, string Origin)
{
    public static bool Valid(string? value)
    {
        if (value is not { Length: 88 }) return false;
        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length == 64 &&
                string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal);
        }
        catch (FormatException) { return false; }
    }

    public static void Validate(string value, long size)
    {
        if (!Valid(value) || size < 0 || size > 536_870_912)
            throw new InvalidDataException("Invalid recorded NuGet SHA-512 or size.");
    }

    public static async Task<bool> MatchesAsync(string path, NugetDigest expected, CancellationToken ct)
    {
        if (!Valid(expected.Sha512) || expected.Size < 0) return false;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expected.Size) return false;
            var digest = await SHA512.HashDataAsync(stream, ct);
            return CryptographicOperations.FixedTimeEquals(
                digest, Convert.FromBase64String(expected.Sha512));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
