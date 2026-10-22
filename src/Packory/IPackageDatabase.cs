namespace Packory;

/// <summary>
/// Metadata provider contract. Artifact bytes are persisted separately by the blob store.
/// Both implementations expose the same single-node logical catalog and immutable version API.
/// </summary>
public interface IPackageDatabase
{
    void Initialize();
    GoModuleRevision? GetGoModule(string name, string version);
    IReadOnlyList<GoModuleRevision> GoModules();
    IReadOnlyList<string> GoModuleVersions(string name);
    bool PublishGoModule(GoModuleRevision revision, string actor);
    RawArtifactEntry? GetRawArtifact(string name, string version);
    MavenAssetEntry? GetMavenAsset(string path);
    IReadOnlyList<MavenAssetEntry> MavenAssets();
    IReadOnlyList<string> MavenVersions(string groupId, string artifactId);
    bool PublishMavenAsset(MavenAssetEntry asset, string actor);
    MavenMetadataEntry? GetMavenMetadata(string scope, string path);
    IReadOnlyList<MavenMetadataEntry> MavenMetadata();
    // Null expected digest creates only; an existing digest replaces exactly that revision.
    bool PutMavenMetadata(MavenMetadataEntry entry, string actor, string? expectedSha256);
    IReadOnlyList<RawArtifactEntry> RawArtifacts();
    bool PublishRaw(string name, string version, string mediaType,
        string sha256, long size, string actor);
    FeedAssetEntry? GetFeedAsset(string feedId, string kind, string path);
    IReadOnlyList<FeedAssetEntry> FeedAssets();
    IReadOnlyList<FeedAssetEntry> FeedPackageAssets(string feedId, string kind, string? name = null);
    // Mutable private Cargo sparse-index state; archive bytes remain immutable.
    bool? FeedCargoYanked(string feedId, string name, string version);
    bool SetFeedCargoYanked(string feedId, string name, string version, bool yanked, string actor);
    IReadOnlyList<FeedNpmTag> FeedNpmTags(string feedId, string name);
    bool SetFeedNpmTag(string feedId, string name, string tag, string version, bool deleted, string actor);
    string? GetFeedOciTag(string feedId, string name, string tag);
    IReadOnlyList<string> FeedOciTags(string feedId, string name);
    void SetFeedOciTag(string feedId, string name, string tag, string digest, string actor);
    IReadOnlyList<string> FeedMavenVersions(string feedId, string groupId, string artifactId);
    IReadOnlyList<string> FeedGoVersions(string feedId, string module);
    bool PublishFeedAsset(FeedAssetEntry asset, string actor);
    bool TrySetHostedFeedEnabled(string id, string kind, bool enabled, string actor);
    IReadOnlyList<FeedDefinition> Feeds(string? after, int limit);
    FeedDefinition? Feed(string id);
    bool TryCreateDraftFeed(FeedDefinition feed, string actor);
    bool TryDeleteDraftFeed(string id, string actor);
    bool TryActivateHostedNugetFeed(string id, string actor);
    bool TryDeactivateHostedNugetFeed(string id, string actor);
    IReadOnlyList<string> FeedNugetVersions(string feedId, string name);
    FeedNugetRevision? GetFeedNugetRevision(string feedId, string name, string version);
    IReadOnlyList<FeedNugetRevision> FeedNugetRevisions();
    IReadOnlyList<FeedNugetRevision> FeedNugetRevisions(string feedId);
    bool PublishFeedNuget(string feedId, string name, string version,
        string description, string sha512, long size, string actor);
    void BeginPublication(string key, string kind, string name, string version);
    void CompletePublication(string key);
    IReadOnlyList<PendingPublication> PendingPublications();
    QuarantineEvidence? FindQuarantine(string key);
    void PrepareQuarantine(QuarantineEvidence evidence);
    void FinishQuarantine(string key);
    void CreateManagedToken(ManagedTokenRecord token);
    ManagedTokenRecord? FindManagedToken(string sha256);
    bool RevokeManagedToken(string id);
    bool IsHealthy();
    void UpsertPackage(string kind, string name, string version, string description, string origin);
    bool PublishNuget(string name, string version, string description, string actor,
        string? sha512 = null, long? size = null);
    // Atomic metadata + checksum pair for verified NuGet mirrors.
    void UpsertNugetMirror(string name, string version, string description,
        string sha512, long size);
    NugetDigest? GetNugetDigest(string name, string version);
    IReadOnlyList<string> Versions(string kind, string name);
    IReadOnlyList<CatalogEntry> Search(string query, string? kind, int limit);
    bool OciHasBlob(string repository, string digest);
    void OciAddBlob(string repository, string digest);
    OciManifestEntry? OciManifest(string repository, string reference);
    bool OciPutManifest(string repository, string reference, OciManifestEntry manifest,
        string? subject, string? artifactType, string actor);
    IReadOnlyList<string> OciTags(string repository);
    IReadOnlyList<OciReferrerEntry> OciReferrers(string repository, string subject);
    IReadOnlyList<string> OciAllBlobDigests();
    IReadOnlyList<string> OciAllManifestDigests();
    IReadOnlyList<string> CargoIndex(string name);
    bool SetCargoYanked(string name, string version, bool yanked, string actor);
    bool PublishCargo(string name, string version, string json, string description, string actor);
    IReadOnlyList<string> PyProjects();
    IReadOnlyList<PyFileEntry> PyFiles(string? name = null);
    bool PublishPyFile(PyFileEntry file, string description, string actor);
    IReadOnlyList<StoredArtifact> StoredArtifacts();
    string? PackageOrigin(string kind, string name, string version);
    bool HasPackage(string kind, string name, string version);
    bool PutNpmVersion(string name, string version, string json, string description,
        IReadOnlyDictionary<string, string> tags, string actor);
    string? NpmVersion(string name, string version);
    (Dictionary<string, string> Versions, Dictionary<string, string> Tags) NpmHosted(string name);
    bool AddSkill(string name, string version, string description, string actor);
    IReadOnlyList<AuditEntry> AuditEvents(long? before, int limit);
    IReadOnlyList<SkillEntry> Skills(string? name = null);
}
