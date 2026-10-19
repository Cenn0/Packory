using System.Text;
using System.Text.RegularExpressions;

namespace Packory;

/// <summary>
/// Strict, conservative GOPROXY request parser for the V4 E3 implementation sequence.
/// This class does NOT expose an HTTP endpoint, mirror remote packages, or validate module ZIPs.
/// Reference: https://go.dev/ref/mod#module-proxies
/// </summary>
public static class GoProxyCoordinates
{
    private static readonly Regex CanonicalVersion = new(
        @"\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(\+incompatible)?\z",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MajorPath = new(
        @"\Av([2-9]|[1-9][0-9]+)\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsName = new(
        @"\A(?:con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|\z)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ShortName = new(
        @"~[0-9]+\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public sealed record ProxyRequest(
        string ModulePath, string EncodedModulePath, string Operation,
        string? Version);

    public static string EncodeModulePath(string modulePath)
    {
        if (!ValidModulePath(modulePath))
            throw new ArgumentException("Invalid Go module path.", nameof(modulePath));
        var output = new StringBuilder(modulePath.Length * 2);
        foreach (var c in modulePath)
        {
            if (c is >= 'A' and <= 'Z')
                output.Append('!').Append(char.ToLowerInvariant(c));
            else
                output.Append(c);
        }
        return output.ToString();
    }

    public static bool TryDecodeModulePath(string? encoded, out string modulePath)
    {
        modulePath = "";
        if (encoded is null || encoded.Length == 0 || encoded.Length > 512 ||
            encoded.Contains('%') || encoded.Contains('\\') ||
            encoded.Contains('?') || encoded.Contains('#'))
            return false;
        var decoded = new StringBuilder(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            var c = encoded[i];
            if (c == '!')
            {
                if (++i >= encoded.Length || encoded[i] is not (>= 'a' and <= 'z'))
                    return false;
                decoded.Append(char.ToUpperInvariant(encoded[i]));
            }
            else
            {
                // Uppercase must ALWAYS be escaped; the spelling is part of the wire protocol.
                if (c is >= 'A' and <= 'Z') return false;
                decoded.Append(c);
            }
        }
        var result = decoded.ToString();
        if (!ValidModulePath(result) ||
            !string.Equals(EncodeModulePath(result), encoded, StringComparison.Ordinal))
            return false;
        modulePath = result;
        return true;
    }

    public static bool ValidModulePath(string? path)
    {
        if (path is null || path.Length is < 4 or > 255) return false;
        var parts = path.Split('/');
        if (parts.Length is < 2 or > 32) return false;
        foreach (var part in parts)
        {
            if (part is { Length: 0 or > 128 } || part.StartsWith('.') ||
                part.EndsWith('.') || part is "." or ".." ||
                part.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '~')) ||
                WindowsName.IsMatch(part) || ShortName.IsMatch(part.Split('.')[0]))
                return false;
        }
        var domain = parts[0];
        if (domain.Length > 253 || !domain.Contains('.') || domain.StartsWith('-') ||
            domain.Any(c => !(c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c is '.' or '-')) ||
            domain.Split('.').Any(x => x.Length == 0 || x.StartsWith('-') || x.EndsWith('-')))
            return false;
        // v2+ semantic import version: /v2, /v3 etc, never /v01 or /v1.
        if (parts[^1].StartsWith('v') && parts[^1].Length > 1 &&
            char.IsAsciiDigit(parts[^1][1]) && !MajorPath.IsMatch(parts[^1]) &&
            !string.Equals(domain, "gopkg.in", StringComparison.Ordinal))
            return false;
        return true;
    }

    public static bool ValidCanonicalVersion(string? version)
    {
        if (version is null || version.Length is < 6 or > 160 ||
            !CanonicalVersion.IsMatch(version)) return false;
        // Numeric prerelease identifiers must not have leading zeroes.
        var hyphen = version.IndexOf('-');
        if (hyphen >= 0)
        {
            var suffix = version[(hyphen + 1)..].Split('+')[0];
            if (suffix.Split('.').Any(part =>
                part.Length > 1 && part[0] == '0' && part.All(char.IsAsciiDigit)))
                return false;
        }
        return true;
    }

    public static bool TryDecodeVersion(string? encoded, out string version)
    {
        version = "";
        if (encoded is null || encoded.Length > 320 || encoded.Length == 0 ||
            encoded.Contains('/') || encoded.Contains('%') ||
            encoded.Contains('\\')) return false;
        var decoded = new StringBuilder(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '!')
            {
                if (++i >= encoded.Length || encoded[i] is not (>= 'a' and <= 'z'))
                    return false;
                decoded.Append(char.ToUpperInvariant(encoded[i]));
            }
            else
            {
                if (encoded[i] is >= 'A' and <= 'Z') return false;
                decoded.Append(encoded[i]);
            }
        }
        var normalized = decoded.ToString();
        if (!ValidCanonicalVersion(normalized)) return false;
        var canonicalEncoded = new StringBuilder(normalized.Length * 2);
        foreach (var c in normalized)
        {
            if (c is >= 'A' and <= 'Z')
                canonicalEncoded.Append('!').Append(char.ToLowerInvariant(c));
            else
                canonicalEncoded.Append(c);
        }
        if (!string.Equals(encoded, canonicalEncoded.ToString(), StringComparison.Ordinal))
            return false;
        version = normalized;
        return true;
    }

    public static bool TryParseRequest(string? path, out ProxyRequest? request)
    {
        request = null;
        if (path is null || path.Length > 1024 || path.StartsWith('/') ||
            path.Contains('\\') || path.Contains('%') || path.Contains('?') ||
            path.Contains('#') || path.Any(char.IsControl))
            return false;
        var index = path.LastIndexOf("/@v/", StringComparison.Ordinal);
        if (index < 1) return false;
        var escaped = path[..index];
        if (!TryDecodeModulePath(escaped, out var module)) return false;
        var remainder = path[(index + 4)..];
        if (remainder == "list")
        {
            request = new ProxyRequest(module, escaped, "list", null);
            return true;
        }
        var dot = remainder.LastIndexOf('.');
        if (dot < 1) return false;
        var encodedVersion = remainder[..dot];
        var extension = remainder[(dot + 1)..];
        if (!TryDecodeVersion(encodedVersion, out var version) ||
            extension is not ("info" or "mod" or "zip"))
            return false;
        var major = version[1..version.IndexOf('.')];
        var parts = module.Split('/');
        var suffix = parts[^1];
        if (MajorPath.IsMatch(suffix) && suffix[1..] != major)
            return false;
        request = new ProxyRequest(module, escaped, extension, version);
        return true;
    }
}
