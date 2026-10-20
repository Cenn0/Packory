namespace Packory;

/// <summary>
/// Immutable identity of one private hosted artifact. The canonical source
/// coordinate is distinct from the global catalog and must never be inferred
/// from an HTTP-supplied storage key.
/// </summary>
public sealed record FeedAssetEntry(string FeedId, string Kind, string Path,
    string Name, string Version, string MediaType, string Sha256, long Size,
    string CreatedAt)
{
    public static string BlobKey(FeedAssetEntry entry) => entry.Kind switch
    {
        "raw" => $"feeds/{entry.FeedId}/raw/{entry.Name}/{entry.Version}/payload.blob",
        "maven" => $"feeds/{entry.FeedId}/maven/{entry.Path}",
        _ => throw new ArgumentException("Unsupported private artifact kind.")
    };

    public static string RawPath(string name, string version) => name + "/" + version;

    public static bool Valid(FeedAssetEntry? item)
    {
        if (item is null || !FeedDefinition.ValidId(item.FeedId) ||
            !ArtifactDigests.ValidSha256(item.Sha256) || item.Size <= 0 ||
            !DateTimeOffset.TryParse(item.CreatedAt, out _))
            return false;

        if (item.Kind == "raw")
            return RawEndpoints.ValidName(item.Name) &&
                RawEndpoints.ValidVersion(item.Version) &&
                RawEndpoints.ValidMediaType(item.MediaType) &&
                item.Path == RawPath(item.Name, item.Version);

        if (item.Kind == "maven" &&
            MavenEndpoints.ValidAssetPath(item.Path, out var coordinates) &&
            coordinates is not null)
            return item.Name == coordinates.GroupId + ":" + coordinates.ArtifactId &&
                item.Version == coordinates.Version &&
                item.MediaType == MavenEndpoints.AssetMediaType(coordinates.Extension);

        return false;
    }
}
