using System.Text.RegularExpressions;

namespace Packory;

/// <summary>
/// Canonical Maven 2 repository path parser for future E2 hosted/proxy endpoints.
/// This does not expose routes, fetch upstream data or grant publication permissions.
/// </summary>
public static class MavenCoordinates
{
    private static readonly Regex Segment = new(
        @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public sealed record Artifact(string GroupId, string ArtifactId, string Version,
        string? Classifier, string Extension);

    public static bool ValidSegment(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        Segment.IsMatch(value) && value is not "." and not ".." &&
        !value.Contains("..", StringComparison.Ordinal);

    public static bool ValidGroup(string? value) =>
        value is { Length: > 0 and <= 255 } &&
        value.Split('.').All(ValidSegment);

    public static bool TryParseArtifactPath(string? path, out Artifact? artifact)
    {
        artifact = null;
        if (path is null || path.Length > 1024 || path.Contains('\\') ||
            path.Contains('%') || path.StartsWith('/') || path.EndsWith('/') ||
            path.Any(char.IsControl))
            return false;
        var parts = path.Split('/');
        if (parts.Length < 4 || parts.Length > 20 ||
            parts.Any(x => !ValidSegment(x)) ||
            parts[..^3].Any(x => x.Contains('.', StringComparison.Ordinal))) return false;
        var group = string.Join(".", parts[..^3]);
        var name = parts[^3];
        var version = parts[^2];
        var file = parts[^1];
        if (!ValidGroup(group) || !ValidSegment(name) || !ValidSegment(version) ||
            !file.StartsWith(name + "-" + version + ".", StringComparison.Ordinal) &&
            !file.StartsWith(name + "-" + version + "-", StringComparison.Ordinal))
            return false;
        var prefix = name + "-" + version;
        var suffix = file[prefix.Length..];
        string? classifier = null;
        string extension;
        if (suffix.StartsWith('.'))
            extension = suffix[1..];
        else if (suffix.StartsWith('-'))
        {
            var dot = suffix.IndexOf('.');
            if (dot <= 1) return false;
            classifier = suffix[1..dot];
            extension = suffix[(dot + 1)..];
        }
        else return false;
        if (!ValidSegment(extension) ||
            classifier is not null && !ValidSegment(classifier)) return false;
        artifact = new Artifact(group, name, version, classifier, extension);
        return true;
    }

    public static string RepositoryPath(Artifact artifact)
    {
        if (!ValidGroup(artifact.GroupId) || !ValidSegment(artifact.ArtifactId) ||
            !ValidSegment(artifact.Version) || !ValidSegment(artifact.Extension) ||
            artifact.Classifier is not null && !ValidSegment(artifact.Classifier))
            throw new ArgumentException("Invalid Maven coordinates.");
        return artifact.GroupId.Replace('.', '/') + "/" + artifact.ArtifactId +
            "/" + artifact.Version + "/" + artifact.ArtifactId + "-" + artifact.Version +
            (artifact.Classifier is null ? "" : "-" + artifact.Classifier) + "." + artifact.Extension;
    }
}
