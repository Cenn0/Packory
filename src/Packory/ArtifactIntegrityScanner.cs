using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;

namespace Packory;

/// <summary>
/// Bounded, read-only inspection of *referenced* immutable artifacts.
/// This is not garbage collection, provider certification, or an atomic
/// multi-store snapshot. The only network reads are through the configured
/// BlobStore; S3 may populate its local cache without mutating remote objects.
/// </summary>
public sealed class ArtifactIntegrityScanner(
    IPackageDatabase database, BlobStore blobs, SkillSecurityStore security, Settings settings)
{
    public sealed record Check(string Kind, string Name, string Version, string? Filename,
        string Origin, string State, string DigestBasis);

    public sealed record Page(int Offset, int TotalReferences, int Checked,
        int? NextOffset, int PendingPublicationIntents, string Coverage,
        string StorageEvidence, IReadOnlyList<Check> Items);

    private sealed record Reference(string Kind, string Name, string Version,
        string? Filename, string Origin, string? Key,
        string? KnownSha256 = null, long? KnownSize = null);

    private sealed record Expected(string Algorithm, byte[] Bytes, long? Size = null);

    public async Task<Page> ScanAsync(int offset, int limit, CancellationToken ct)
    {
        if (offset < 0 || limit is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(limit), "Integrity page must contain 1..10 references.");
        ct.ThrowIfCancellationRequested();
        var refs = References();
        var page = refs.Skip(offset).Take(limit).ToArray();
        var items = new List<Check>(page.Length);
        foreach (var entry in page)
        {
            ct.ThrowIfCancellationRequested();
            items.Add(await InspectAsync(entry, ct));
        }
        var next = offset + page.Length < refs.Count ? offset + page.Length : (int?)null;
        return new Page(offset, refs.Count, items.Count, next,
            database.PendingPublications().Count,
            "catalog-references-only; no unreferenced-object sweep; no full snapshot guarantee",
            settings.BlobProvider == "s3"
                ? "configured-S3-provider-with-local-read-cache; not full provider certification"
                : "local-filesystem", items);
    }

    private List<Reference> References()
    {
        var refs = new List<Reference>();
        foreach (var artifact in database.StoredArtifacts())
        {
            var versionOk = Names.Version(artifact.Version);
            string? key = artifact.Kind switch
            {
                "nuget" when Names.Nuget(artifact.Name) && versionOk =>
                    NuGetEndpoints.NugetKey(artifact.Name, artifact.Version),
                "npm" when Names.Npm(artifact.Name) && versionOk =>
                    NpmEndpoints.NpmKey(artifact.Name, artifact.Version),
                "cargo" when CargoEndpoints.IsName(artifact.Name) && CargoEndpoints.IsVersion(artifact.Version) =>
                    CargoEndpoints.Key(artifact.Name, artifact.Version),
                "skill" when Names.Skill(artifact.Name) && versionOk =>
                    SkillEndpoints.SkillKey(artifact.Name, artifact.Version),
                _ => null
            };
            refs.Add(new Reference(artifact.Kind, artifact.Name, artifact.Version,
                null, artifact.Origin, key));
        }
        foreach (var file in database.PyFiles())
            refs.Add(new Reference("pypi", file.Name, file.Version, file.Filename,
                "hosted", PyPiEndpoints.Key(file.Name, file.Filename), file.Sha256, file.Size));
        foreach (var digest in database.OciAllBlobDigests())
        {
            var valid = ValidOciDigest(digest);
            refs.Add(new Reference("oci-blob", "blob", digest, null, "hosted",
                valid ? OciEndpoints.BlobKey(digest) : null,
                valid ? digest[7..] : null));
        }
        foreach (var digest in database.OciAllManifestDigests())
        {
            var valid = ValidOciDigest(digest);
            refs.Add(new Reference("oci-manifest", "manifest", digest, null, "hosted",
                valid ? OciEndpoints.ManifestKey(digest) : null,
                valid ? digest[7..] : null));
        }
        return refs.OrderBy(x => x.Kind, StringComparer.Ordinal)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Version, StringComparer.Ordinal)
            .ThenBy(x => x.Filename ?? "", StringComparer.Ordinal)
            .ToList();
    }

    private static bool ValidOciDigest(string digest) =>
        digest.StartsWith("sha256:", StringComparison.Ordinal) &&
        ArtifactDigests.ValidSha256(digest[7..]);

    private static Check With(Reference x, string state, string basis) =>
        new(x.Kind, x.Name, x.Version, x.Filename, x.Origin, state, basis);

    private async Task<Check> InspectAsync(Reference entry, CancellationToken ct)
    {
        if (entry.Key is null || entry.Origin is not ("hosted" or "mirror"))
            return With(entry, "invalid_reference", "none");
        try
        {
            // For S3 this can download a bounded local cache copy. Do not treat
            // a cache digest match as proof of remote bucket version/retention.
            var path = blobs.Existing(entry.Key);
            if (path is null) return With(entry, "missing", "none");
            var size = new FileInfo(path).Length;
            if (size > settings.MaxArtifactBytes)
                return With(entry, "size_limit", "none");
            var expected = await ExpectedAsync(entry, ct);
            if (expected is null) return With(entry, "unverifiable", "no-stored-digest");
            if (expected.Size is { } expectedSize && (expectedSize < 0 || size != expectedSize))
                return With(entry, "digest_mismatch", expected.Algorithm);

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = expected.Algorithm switch
            {
                "sha256" => await SHA256.HashDataAsync(stream, ct),
                "sha512" => await SHA512.HashDataAsync(stream, ct),
                "sha1" => await SHA1.HashDataAsync(stream, ct),
                _ => throw new InvalidDataException("Unsupported stored checksum algorithm")
            };
            return CryptographicOperations.FixedTimeEquals(actual, expected.Bytes)
                ? With(entry, expected.Algorithm == "sha1" ? "weak_digest_match" : "digest_match",
                    expected.Algorithm)
                : With(entry, "digest_mismatch", expected.Algorithm);
        }
        catch (ArgumentException) { return With(entry, "invalid_reference", "none"); }
        catch (InvalidDataException) { return With(entry, "digest_mismatch", "storage-integrity"); }
        catch (IOException) { return With(entry, "storage_unavailable", "none"); }
        catch (UnauthorizedAccessException) { return With(entry, "storage_unavailable", "none"); }
        catch (AmazonServiceException) { return With(entry, "storage_unavailable", "none"); }
        catch (JsonException) { return With(entry, "unverifiable", "invalid-metadata"); }
    }

    private async Task<Expected?> ExpectedAsync(Reference entry, CancellationToken ct)
    {
        if (entry.KnownSha256 is { } known && ArtifactDigests.ValidSha256(known))
            return new Expected("sha256", Convert.FromHexString(known), entry.KnownSize);
        if (entry.Kind is "oci-blob" or "oci-manifest" or "pypi")
            return null; // Invalid or missing metadata must never become a self-hash.
        if (entry.Kind == "cargo")
        {
            var hash = ArtifactDigests.CargoExpected(
                database.CargoIndex(entry.Name), entry.Name, entry.Version);
            return ArtifactDigests.ValidSha256(hash)
                ? new Expected("sha256", Convert.FromHexString(hash!)) : null;
        }
        if (entry.Kind == "skill")
        {
            var revision = security.Get(entry.Name, entry.Version);
            return revision is { State: "APPROVED" } &&
                   ArtifactDigests.ValidSha256(revision.Sha256)
                ? new Expected("sha256", Convert.FromHexString(revision.Sha256)) : null;
        }
        if (entry.Kind == "npm")
        {
            string? metadata = null;
            if (entry.Origin == "hosted")
                metadata = database.NpmVersion(entry.Name, entry.Version);
            else if (entry.Origin == "mirror")
            {
                var file = blobs.Existing("npm-metadata/" + Names.HashName(entry.Name) + ".json");
                if (file is not null && new FileInfo(file).Length <= settings.MaxMetadataBytes)
                    metadata = await File.ReadAllTextAsync(file, ct);
            }
            var expected = NpmTarballIntegrity.ParseExpected(metadata, entry.Name,
                entry.Version, packument: entry.Origin == "mirror");
            return expected is null ? null : new Expected(expected.Algorithm, expected.Bytes);
        }
        // NuGet currently has no durable publication-time digest in metadata.
        // A hash recomputed from the downloaded package would NOT verify integrity.
        return null;
    }
}
