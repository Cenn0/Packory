using System.Globalization;
using System.Text.RegularExpressions;

namespace Packory;

/// <summary>
/// Canonical Maven repository paths. A timestamped snapshot keeps its base version
/// in the directory and uses its resolved version only in the filename.
/// </summary>
public static class MavenCoordinates
{
    private static readonly Regex Segment = new(
        @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SnapshotSuffix = new(
        @"\A(?<timestamp>[0-9]{8}\.[0-9]{6})-(?<build>[1-9][0-9]{0,9})(?=[.-])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public sealed record Artifact(string GroupId, string ArtifactId, string Version,
        string? Classifier, string Extension, string? ResolvedVersion = null);

    public static bool IsSnapshot(string? version) => version is { Length: > 9 } &&
        ValidSegment(version) && version.EndsWith("-SNAPSHOT", StringComparison.Ordinal);

    public static bool TrySnapshotVersion(string baseVersion, string value,
        out string timestamp, out int build)
    {
        timestamp = "";
        build = 0;
        if (!IsSnapshot(baseVersion) || !ValidSegment(value)) return false;
        var prefix = baseVersion[..^9] + "-";
        if (!value.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var tail = value[prefix.Length..];
        if (tail.Length < 17 || tail[15] != '-' ||
            !DateTime.TryParseExact(tail[..15], "yyyyMMdd.HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            !int.TryParse(tail[16..], NumberStyles.None, CultureInfo.InvariantCulture, out build) ||
            build < 1 || tail[16..] != build.ToString(CultureInfo.InvariantCulture))
            return false;
        timestamp = tail[..15];
        return true;
    }

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
        if (!ValidGroup(group) || !ValidSegment(name) || !ValidSegment(version)) return false;
        string? resolved = null;
        var prefix = name + "-" + version;
        if (IsSnapshot(version))
        {
            var snapshotPrefix = name + "-" + version[..^9] + "-";
            if (!file.StartsWith(snapshotPrefix, StringComparison.Ordinal)) return false;
            var match = SnapshotSuffix.Match(file[snapshotPrefix.Length..]);
            if (!match.Success) return false;
            resolved = version[..^9] + "-" + match.Value;
            if (!TrySnapshotVersion(version, resolved, out _, out _)) return false;
            prefix = name + "-" + resolved;
        }
        if (!file.StartsWith(prefix + ".", StringComparison.Ordinal) &&
            !file.StartsWith(prefix + "-", StringComparison.Ordinal)) return false;
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
        artifact = new Artifact(group, name, version, classifier, extension, resolved);
        return true;
    }

    public static string RepositoryPath(Artifact artifact)
    {
        if (!ValidGroup(artifact.GroupId) || !ValidSegment(artifact.ArtifactId) ||
            !ValidSegment(artifact.Version) || !ValidSegment(artifact.Extension) ||
            artifact.Classifier is not null && !ValidSegment(artifact.Classifier) ||
            IsSnapshot(artifact.Version) && (artifact.ResolvedVersion is null ||
                !TrySnapshotVersion(artifact.Version, artifact.ResolvedVersion, out _, out _)) ||
            !IsSnapshot(artifact.Version) && artifact.ResolvedVersion is not null)
            throw new ArgumentException("Invalid Maven coordinates.");
        return artifact.GroupId.Replace('.', '/') + "/" + artifact.ArtifactId +
            "/" + artifact.Version + "/" + artifact.ArtifactId + "-" +
            (artifact.ResolvedVersion ?? artifact.Version) +
            (artifact.Classifier is null ? "" : "-" + artifact.Classifier) + "." + artifact.Extension;
    }
}
