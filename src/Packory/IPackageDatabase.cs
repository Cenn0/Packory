namespace Packory;

/// <summary>
/// Metadata provider contract. Artifact bytes are persisted separately by the blob store.
/// Both implementations expose the same single-node logical catalog and immutable version API.
/// </summary>
public interface IPackageDatabase
{
    void Initialize();
    void CreateManagedToken(ManagedTokenRecord token);
    ManagedTokenRecord? FindManagedToken(string sha256);
    bool RevokeManagedToken(string id);
    bool IsHealthy();
    void UpsertPackage(string kind, string name, string version, string description, string origin);
    bool PublishNuget(string name, string version, string description, string actor);
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
