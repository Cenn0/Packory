using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Packory;

/// <summary>
/// Offline, fail-closed ZIP preflight for the V4 Go module adapter. Does not publish,
/// execute, extract, serve or authenticate modules. Intentionally supports a smaller
/// ASCII-only file-name subset and lower resource ceilings than the Go specification.
/// </summary>
public static class GoModuleZipInspector
{
    // Intentionally smaller than Go's 500 MiB distribution limits; preflight only.
    public const long MaxArchiveBytes = 32L * 1024 * 1024;
    public const long MaxTotalUncompressedBytes = 64L * 1024 * 1024;
    public const long MaxEntryBytes = 16L * 1024 * 1024;
    public const long MaxGoModBytes = 1L * 1024 * 1024;
    public const int MaxEntries = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> ReservedNames = new(
        new[] { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4",
            "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2",
            "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9" },
        StringComparer.OrdinalIgnoreCase);

    public sealed record Inspection(
        string ModulePath, string Version, int Files,
        long UncompressedBytes, bool HasGoMod, string ZipSha256);

    /// <param name="archive">Bounded, readable, seekable ZIP stream. Position is restored.</param>
    /// <param name="requestPath">Canonical wire coordinate, e.g. example.com/mod/@v/v1.2.3.zip.</param>
    public static Inspection Inspect(Stream archive, string requestPath)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (!GoProxyCoordinates.TryParseRequest(requestPath, out var coordinate) ||
            coordinate is null || coordinate.Operation != "zip" || coordinate.Version is null)
            throw new ArgumentException("Expected a canonical Go module ZIP coordinate.",
                nameof(requestPath));
        if (!archive.CanRead || !archive.CanSeek)
            throw new InvalidDataException("Go ZIP inspection requires a readable, seekable stream.");
        if (archive.Length is <= 0 or > MaxArchiveBytes)
            throw new InvalidDataException("Go ZIP compressed size exceeds the preflight limit.");

        var savedPosition = archive.Position;
        try
        {
            archive.Position = 0;
            var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            archive.Position = 0;

            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count is 0 or > MaxEntries)
                throw new InvalidDataException("Go ZIP has an invalid number of entries.");

            var prefix = coordinate.ModulePath + "@" + coordinate.Version + "/";
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long uncompressed = 0;
            var foundGoMod = false;
            var buffer = new byte[8192];

            foreach (var entry in zip.Entries)
            {
                if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
                    throw new InvalidDataException("Go ZIP entry has a foreign module/version prefix.");
                var relative = entry.FullName[prefix.Length..];
                // Empty directory entries are valid in Go, but outside this strict subset.
                if (!ValidRelativeFileName(relative))
                    throw new InvalidDataException("Go ZIP contains an unsafe or unsupported filename.");

                // ZIP attributes may identify a UNIX symlink, device or Windows reparse point.
                var fileType = (entry.ExternalAttributes >> 16) & 0xF000;
                if ((fileType != 0 && fileType != 0x8000) ||
                    (entry.ExternalAttributes & 0x400) != 0)
                    throw new InvalidDataException("Go ZIP contains an irregular file.");

                if (files.Contains(relative) || directories.Contains(relative))
                    throw new InvalidDataException("Go ZIP has a case-colliding file or directory.");
                var slash = relative.IndexOf('/');
                while (slash >= 0)
                {
                    var parent = relative[..slash];
                    if (files.Contains(parent))
                        throw new InvalidDataException("Go ZIP file collides with a parent directory.");
                    directories.Add(parent);
                    slash = relative.IndexOf('/', slash + 1);
                }
                files.Add(relative);

                var isGoMod = relative == "go.mod";
                if (relative.Split('/').Last().Equals("go.mod", StringComparison.OrdinalIgnoreCase)
                    && !isGoMod)
                    throw new InvalidDataException("Only the root lowercase go.mod is accepted.");
                if (entry.Length < 0 || entry.Length > MaxEntryBytes ||
                    uncompressed > MaxTotalUncompressedBytes - entry.Length ||
                    (isGoMod && entry.Length > MaxGoModBytes))
                    throw new InvalidDataException("Go ZIP entry exceeds the preflight size budget.");

                using var data = entry.Open();
                using var goMod = isGoMod ? new MemoryStream() : null;
                long actual = 0;
                int n;
                while ((n = data.Read(buffer, 0, buffer.Length)) != 0)
                {
                    actual += n;
                    if (actual > entry.Length || actual > MaxEntryBytes ||
                        uncompressed > MaxTotalUncompressedBytes - actual ||
                        (isGoMod && actual > MaxGoModBytes))
                        throw new InvalidDataException("Go ZIP decompression exceeded declared limits.");
                    goMod?.Write(buffer, 0, n);
                }
                if (actual != entry.Length)
                    throw new InvalidDataException("Go ZIP entry length is inconsistent.");
                uncompressed += actual;
                if (isGoMod)
                {
                    foundGoMod = true;
                    VerifyModuleDirective(goMod!.ToArray(), coordinate.ModulePath);
                }
            }

            return new Inspection(coordinate.ModulePath, coordinate.Version,
                files.Count, uncompressed, foundGoMod, digest);
        }
        finally
        {
            archive.Position = savedPosition;
        }
    }

    private static bool ValidRelativeFileName(string relative)
    {
        if (relative.Length is 0 or > 1024 || relative.EndsWith('/'))
            return false;
        foreach (var part in relative.Split('/'))
        {
            if (part.Length is 0 or > 255 || part is "." or ".." ||
                part.EndsWith('.') || part.EndsWith(' ') ||
                ReservedNames.Contains(part.Split('.')[0]))
                return false;
            foreach (var c in part)
            {
                // Narrow ASCII subset: avoids cross-platform Unicode case-folding ambiguity.
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                    or ' ' or '!' or '#' or '$' or '%' or '&' or '(' or ')' or '+'
                    or ',' or '-' or '.' or '=' or '@' or '[' or ']' or '^' or '_'
                    or '{' or '}' or '~'))
                    return false;
            }
        }
        return true;
    }

    private static void VerifyModuleDirective(byte[] data, string modulePath)
    {
        string source;
        try { source = StrictUtf8.GetString(data); }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("Go ZIP go.mod contains invalid UTF-8.");
        }

        var directives = source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("module ", StringComparison.Ordinal) ||
                line.StartsWith("module\t", StringComparison.Ordinal))
            .ToArray();
        // This preflight deliberately accepts only an unquoted, single-line directive.
        var parts = directives.Length == 1
            ? directives[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        if (parts.Length != 2 || !string.Equals(parts[1], modulePath,
            StringComparison.Ordinal))
            throw new InvalidDataException("Go ZIP go.mod module directive differs from its coordinate.");
    }
}
