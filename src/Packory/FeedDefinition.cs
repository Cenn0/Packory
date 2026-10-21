namespace Packory;

/// <summary>
/// Administrative inventory only. Legacy default feeds describe existing global
/// registry URLs; they DO NOT route requests, isolate packages or authorize reads.
/// </summary>
public sealed record FeedDefinition(
    string Id, string Kind, string Mode, string Visibility,
    string DisplayName, bool Enabled, string CreatedAt, string UpdatedAt)
{
    public static bool SupportsIsolatedHosted(string? kind) =>
        kind is "nuget" or "npm" or "cargo" or "pypi" or "oci" or "raw" or "maven" or "go";

    public static bool ValidId(string? id)
    {
        if (id is null || id.Length is < 3 or > 64 || !char.IsAsciiLetterLower(id[0]) ||
            !(char.IsAsciiLetterLower(id[^1]) || char.IsAsciiDigit(id[^1])))
            return false;
        return id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
    }
}
