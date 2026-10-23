using System.Text;

namespace Packory;

/// <summary>
/// Resolves an operator-provided secret from a direct setting or one explicit mounted file.
/// No directory scan, implicit hot reload or secret-value diagnostics.
/// </summary>
public static class RuntimeSecretFiles
{
    private const int MaxSecretBytes = 16 * 1024;

    public static string? Resolve(string name, string? configuredValue,
        bool blankEnvironmentMeansUnset = false, Func<string, string?>? getEnvironment = null)
    {
        var readEnvironment = getEnvironment ?? Environment.GetEnvironmentVariable;
        var direct = readEnvironment(name);
        var file = readEnvironment(name + "_FILE");
        if (file is not null)
        {
            // An explicitly configured, even empty, direct environment setting conflicts.
            // This also guards secrets bound through Packory:... configuration.
            if (direct is not null || !string.IsNullOrEmpty(configuredValue))
                throw new InvalidOperationException($"Conflicting direct and file-backed configuration for {name}.");
            return ReadFile(name, file);
        }

        // Preserve the existing behavior for empty direct static API tokens.
        return blankEnvironmentMeansUnset && string.IsNullOrWhiteSpace(direct)
            ? configuredValue
            : direct ?? configuredValue;
    }

    private static string ReadFile(string name, string path)
    {
        const string prefix = "Invalid or unreadable file-backed configuration for ";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException(prefix + name + ".");

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                throw new InvalidOperationException(prefix + name + ".");

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, bufferSize: 4096, options: FileOptions.SequentialScan);
            if (stream.Length is < 1 or > MaxSecretBytes)
                throw new InvalidOperationException(prefix + name + ".");

            // Bounded even if the file grows between stat() and read().
            var buffer = new byte[MaxSecretBytes + 1];
            var length = 0;
            while (length < buffer.Length)
            {
                var count = stream.Read(buffer.AsSpan(length));
                if (count == 0) break;
                length += count;
            }
            if (length is < 1 or > MaxSecretBytes)
                throw new InvalidOperationException(prefix + name + ".");

            var value = new UTF8Encoding(false, true).GetString(buffer, 0, length);
            // Remove exactly one terminal line ending inserted by common secret writers.
            // No Trim(): spaces in passwords/connection strings must be preserved.
            if (value.EndsWith("\r\n", StringComparison.Ordinal)) value = value[..^2];
            else if (value.EndsWith('\n')) value = value[..^1];

            if (string.IsNullOrWhiteSpace(value) ||
                value.Any(c => char.IsControl(c) || c == '\uFEFF'))
                throw new InvalidOperationException(prefix + name + ".");
            return value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
               System.Security.SecurityException or ArgumentException or NotSupportedException or
               DecoderFallbackException)
        {
            // Never preserve the inner exception: OS messages may contain sensitive host paths.
            throw new InvalidOperationException(prefix + name + ".");
        }
    }
}
