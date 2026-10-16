using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Packory;

public sealed class Settings
{
    public string DataDirectory { get; set; } = "./data";
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";
    public string NugetUpstreamUrl { get; set; } = "https://api.nuget.org/v3-flatcontainer/";
    public string NpmUpstreamUrl { get; set; } = "https://registry.npmjs.org/";
    public string NugetRegistrationUrl { get; set; } = "https://api.nuget.org/v3/registration5-semver2/";
    public bool AnonymousRead { get; set; } = true;
    public long MaxArtifactBytes { get; set; } = 100 * 1024 * 1024;
    public int MaxMetadataBytes { get; set; } = 16 * 1024 * 1024;
    public string? AdminToken { get; set; }
    public string? ReadToken { get; set; }
    public string? WriteToken { get; set; }

    public void Validate()
    {
        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(origin.UserInfo) ||
            origin.AbsolutePath.Trim('/') != "" || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            throw new InvalidOperationException("Packory:PublicBaseUrl must be an HTTP(S) origin without path, query, or credentials.");
        foreach (var value in new[] { NugetUpstreamUrl, NugetRegistrationUrl, NpmUpstreamUrl })
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var upstream) || upstream.Scheme != "https" ||
                !string.IsNullOrEmpty(upstream.UserInfo) || !string.IsNullOrEmpty(upstream.Query) || !string.IsNullOrEmpty(upstream.Fragment))
                throw new InvalidOperationException("Upstream registry URL must use HTTPS and contain no credentials, query, or fragment.");
        }
        if (MaxArtifactBytes is < 1024 or > 536870912 || MaxMetadataBytes is < 1024 or > 67108864)
            throw new InvalidOperationException("Configured upload limits are outside supported bounds.");
        foreach (var token in new[] { AdminToken, ReadToken, WriteToken }.Where(t => t is not null))
        {
            if (token!.Length < 32) throw new InvalidOperationException("API tokens must contain at least 32 characters.");
        }
        if (new[] { AdminToken, ReadToken, WriteToken }.Where(t => t is not null).Distinct().Count() !=
            new[] { AdminToken, ReadToken, WriteToken }.Count(t => t is not null))
            throw new InvalidOperationException("API tokens must be distinct.");
    }

    public string Base => PublicBaseUrl.TrimEnd('/');
}

public static partial class Names
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex NugetRegex();
    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.+-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
    [GeneratedRegex("^(?:@[a-z0-9][a-z0-9._-]{0,100}/)?[a-z0-9][a-z0-9._-]{0,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex NpmRegex();
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillRegex();

    public static bool Nuget(string id) => NugetRegex().IsMatch(id);
    public static bool Version(string version) => VersionRegex().IsMatch(version);
    public static bool Npm(string name) => NpmRegex().IsMatch(name) && name.Length <= 214;
    public static bool Skill(string name) => name.Length <= 64 && SkillRegex().IsMatch(name);
    public static string EscapedNpm(string name) => Uri.EscapeDataString(name);
    public static string HashName(string name) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
}
