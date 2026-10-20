using System.IO.Compression;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Packory;

/// <summary>
/// Immutable, single-object module revision: the three required GOPROXY resources
/// share one atomic blob and one database publication record. No package execution.
/// The accepted Go archive format is deliberately bounded by GoModuleZipInspector.
/// </summary>
public static class GoModuleBundle
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public const int MaxModBytes = 1024 * 1024;
    public const int MaxInfoBytes = 2048;
    public const long MaxBundleBytes = GoModuleZipInspector.MaxArchiveBytes + MaxModBytes + 32768;

    public static string Key(string name, string version) =>
        "go/modules/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name))) +
        "/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(version))) + ".bundle";

    public static string EncodedVersion(string version)
    {
        var builder = new StringBuilder(version.Length * 2);
        foreach (var c in version)
        {
            if (c is >= 'A' and <= 'Z')
                builder.Append('!').Append(char.ToLowerInvariant(c));
            else builder.Append(c);
        }
        return builder.ToString();
    }

    public static bool ValidCoordinates(string name, string version)
    {
        if (!GoProxyCoordinates.ValidModulePath(name) ||
            !GoProxyCoordinates.ValidCanonicalVersion(version))
            return false;
        var wire = GoProxyCoordinates.EncodeModulePath(name) + "/@v/" +
            EncodedVersion(version) + ".zip";
        return GoProxyCoordinates.TryParseRequest(wire, out var parsed) &&
            parsed?.ModulePath == name && parsed.Version == version;
    }

    public static bool ValidRecord(GoModuleRevision record) =>
        ValidCoordinates(record.Name, record.Version) &&
        ArtifactDigests.ValidSha256(record.BundleSha256) &&
        ArtifactDigests.ValidSha256(record.ZipSha256) &&
        ArtifactDigests.ValidSha256(record.ModSha256) &&
        record.BundleSize is > 0 and <= MaxBundleBytes &&
        record.ZipSize is > 0 and <= GoModuleZipInspector.MaxArchiveBytes &&
        record.ModSize is > 0 and <= MaxModBytes &&
        DateTimeOffset.TryParse(record.PublishedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out _);

    public static string Info(string version, string timestamp) =>
        JsonSerializer.Serialize(new { Version = version, Time = timestamp }) + "\n";

    public static string CanonicalTime(DateTimeOffset timestamp) =>
        timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture);

    public static void VerifyGoMod(byte[] mod, string module)
    {
        if (mod.Length is 0 or > MaxModBytes)
            throw new InvalidDataException("Invalid go.mod size.");
        string text;
        try { text = StrictUtf8.GetString(mod); }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("go.mod must be valid UTF-8.");
        }
        if (text.Contains('\0'))
            throw new InvalidDataException("go.mod has embedded NUL.");
        var directives = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(x => x.Trim())
            .Where(x => x.StartsWith("module ", StringComparison.Ordinal) ||
                        x.StartsWith("module\t", StringComparison.Ordinal))
            .ToArray();
        var parts = directives.Length == 1
            ? directives[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        if (parts.Length != 2 || parts[1] != module)
            throw new InvalidDataException("go.mod module directive does not match its coordinate.");
    }

    public sealed record Package(byte[] Bundle, string BundleSha256, string ZipSha256,
        long ZipSize, string ModSha256, long ModSize, string PublishedAt);

    public static Package Create(string name, string version, byte[] mod, byte[] zip,
        DateTimeOffset timestamp)
    {
        if (!ValidCoordinates(name, version))
            throw new ArgumentException("Invalid Go module coordinates.");
        VerifyGoMod(mod, name);
        using (var input = new MemoryStream(zip, writable: false))
        {
            var inspected = GoModuleZipInspector.Inspect(input,
                GoProxyCoordinates.EncodeModulePath(name) + "/@v/" +
                EncodedVersion(version) + ".zip");
            if (inspected.HasGoMod)
            {
                using var nested = new ZipArchive(new MemoryStream(zip, writable: false),
                    ZipArchiveMode.Read);
                var original = nested.GetEntry(name + "@" + version + "/go.mod") ??
                    throw new InvalidDataException("Go ZIP root go.mod is not readable.");
                using var originalStream = original.Open();
                using var contents = new MemoryStream();
                originalStream.CopyTo(contents);
                if (!contents.ToArray().AsSpan().SequenceEqual(mod))
                    throw new InvalidDataException(
                        "Go ZIP root go.mod differs from the separately published .mod file.");
            }
        }

        var iso = CanonicalTime(timestamp);
        var info = Encoding.UTF8.GetBytes(Info(version, iso));
        using var buffer = new MemoryStream();
        using (var outer = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Put(outer, "info", info);
            Put(outer, "mod", mod);
            Put(outer, "zip", zip);
        }
        var bytes = buffer.ToArray();
        if (bytes.LongLength > MaxBundleBytes)
            throw new PayloadTooLargeException();
        return new Package(bytes,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Convert.ToHexStringLower(SHA256.HashData(zip)), zip.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(mod)), mod.LongLength, iso);
    }

    private static void Put(ZipArchive outer, string name, byte[] bytes)
    {
        var entry = outer.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    /// <summary>
    /// Fail closed: check DB ledger against immutable bundle and each extracted part.
    /// Always return a defensive array, never a streaming view of untrusted ZIP metadata.
    /// </summary>
    public static byte[] Extract(byte[] bundle, GoModuleRevision record, string part)
    {
        if (!ValidRecord(record) || bundle.LongLength != record.BundleSize ||
            !Convert.ToHexStringLower(SHA256.HashData(bundle)).Equals(
                record.BundleSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Go module bundle integrity mismatch.");
        if (part is not ("info" or "mod" or "zip"))
            throw new ArgumentException("Unknown Go module bundle part.", nameof(part));
        using var zipped = new ZipArchive(new MemoryStream(bundle, writable: false),
            ZipArchiveMode.Read);
        if (zipped.Entries.Count != 3 || zipped.Entries.Any(x =>
            x.FullName is not ("info" or "mod" or "zip")) ||
            zipped.Entries.Select(x => x.FullName).Distinct(StringComparer.Ordinal).Count() != 3)
            throw new InvalidDataException("Go module bundle structure changed.");
        var entry = zipped.GetEntry(part) ??
            throw new InvalidDataException("Go module resource is absent.");
        var limit = part switch
        {
            "mod" => record.ModSize,
            "zip" => record.ZipSize,
            _ => MaxInfoBytes
        };
        if (entry.Length < 0 || entry.Length > limit)
            throw new InvalidDataException("Go module resource exceeds declared size.");
        using var stream = entry.Open();
        using var target = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (target.Length + count > limit)
                throw new InvalidDataException("Go module resource expanded beyond its limit.");
            target.Write(buffer, 0, count);
        }
        var bytes = target.ToArray();
        if (bytes.LongLength != entry.Length)
            throw new InvalidDataException("Go module resource length changed.");
        if (part == "info")
        {
            if (!bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(
                    Info(record.Version, record.PublishedAt))))
                throw new InvalidDataException("Go .info metadata differs from its immutable ledger.");
        }
        else
        {
            var expectedHash = part == "mod" ? record.ModSha256 : record.ZipSha256;
            var expectedSize = part == "mod" ? record.ModSize : record.ZipSize;
            if (bytes.LongLength != expectedSize ||
                Convert.ToHexStringLower(SHA256.HashData(bytes)) != expectedHash)
                throw new InvalidDataException("Go module resource checksum mismatch.");
        }
        return bytes;
    }

    // Stable SemVer comparison for @latest and version lists. BigInteger avoids
    // silently overflowing intentionally long but syntactically valid version numbers.
    public static int CompareVersions(string x, string y)
    {
        static (string[] Core, string? Pre) Parse(string value)
        {
            var split = value[1..].Split('+', 2)[0].Split('-', 2);
            return (split[0].Split('.'), split.Length == 2 ? split[1].Split('+')[0] : null);
        }
        var a = Parse(x);
        var b = Parse(y);
        for (var n = 0; n < 3; n++)
        {
            var compare = BigInteger.Parse(a.Core[n], CultureInfo.InvariantCulture)
                .CompareTo(BigInteger.Parse(b.Core[n], CultureInfo.InvariantCulture));
            if (compare != 0) return compare;
        }
        if (a.Pre == null && b.Pre == null) return 0;
        if (a.Pre == null) return 1;
        if (b.Pre == null) return -1;
        var left = a.Pre.Split('.');
        var right = b.Pre.Split('.');
        for (var n = 0; n < Math.Min(left.Length, right.Length); n++)
        {
            var numericLeft = BigInteger.TryParse(left[n], CultureInfo.InvariantCulture, out var an);
            var numericRight = BigInteger.TryParse(right[n], CultureInfo.InvariantCulture, out var bn);
            var compare = numericLeft && numericRight ? an.CompareTo(bn) :
                numericLeft ? -1 : numericRight ? 1 :
                string.CompareOrdinal(left[n], right[n]);
            if (compare != 0) return compare;
        }
        return left.Length.CompareTo(right.Length);
    }
}
