using System.Security.Cryptography;
using System.Text.Json;
using Amazon.Runtime;

namespace Packory;

/// <summary>
/// Bounded, read-only inspection of *referenced* immutable artifacts.
/// This is not garbage collection, provider certification, or an atomic
/// multi-store snapshot. S3 artifact evidence uses direct, cancellation-aware
/// HEAD/streaming GET, not a potentially stale local file cache.
/// </summary>
public sealed class ArtifactIntegrityScanner(
    IPackageDatabase database, BlobStore blobs, SkillSecurityStore security, Settings settings)
{
    public sealed record Check(string Kind, string Name, string Version, string? Filename,
        string Origin, string State, string DigestBasis, string? FeedId = null);

    public sealed record Page(int Offset, int TotalReferences, int Checked,
        int? NextOffset, int PendingPublicationIntents, string Coverage,
        string StorageEvidence, IReadOnlyList<Check> Items);

    private sealed record Reference(string Kind, string Name, string Version,
        string? Filename, string Origin, string? Key,
        string? KnownSha256 = null, long? KnownSize = null,
        string? FeedId = null);

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
                ? "direct remote S3 HEAD/streamed GET; not version-pinned or provider-certified"
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
                "raw" when RawEndpoints.ValidName(artifact.Name) && RawEndpoints.ValidVersion(artifact.Version) =>
                    RawEndpoints.Key(artifact.Name, artifact.Version),
                _ => null
            };
            refs.Add(new Reference(artifact.Kind, artifact.Name, artifact.Version,
                null, artifact.Origin, key));
        }
        foreach (var privateRevision in database.FeedNugetRevisions())
        {
            var valid = FeedDefinition.ValidId(privateRevision.FeedId) &&
                Names.Nuget(privateRevision.Name) && Names.Version(privateRevision.Version);
            refs.Add(new Reference("feed-nuget", privateRevision.Name,
                privateRevision.Version, null, "hosted",
                valid ? FeedNugetEndpoints.BlobKey(privateRevision.FeedId,
                    privateRevision.Name, privateRevision.Version) : null,
                FeedId: privateRevision.FeedId));
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
            .ThenBy(x => x.FeedId ?? "", StringComparer.Ordinal)
            .ToList();
    }

    private static bool ValidOciDigest(string digest) =>
        digest.StartsWith("sha256:", StringComparison.Ordinal) &&
        ArtifactDigests.ValidSha256(digest[7..]);

    private static Check With(Reference x, string state, string basis) =>
        new(x.Kind, x.Name, x.Version, x.Filename, x.Origin, state, basis,
            x.FeedId);

    private async Task<Check> InspectAsync(Reference entry, CancellationToken ct)
    {
        if (entry.Key is null || entry.Origin is not ("hosted" or "mirror"))
            return With(entry, "invalid_reference", "none");
        try
        {
            if (blobs is S3BlobStore remote)
            {
                // Always hash the actual remote object; a previously cached
                // local copy must not hide a changed S3 object.
                var expectedRemote = await ExpectedAsync(entry, ct);
                var probe = await remote.InspectRemoteAsync(entry.Key,
                    expectedRemote?.Algorithm, settings.MaxArtifactBytes, ct);
                if (!probe.Exists) return With(entry, "missing", "none");
                if (probe.ExceedsLimit) return With(entry, "size_limit", "none");
                if (expectedRemote is null)
                    return With(entry, "unverifiable", "no-stored-digest");
                if (expectedRemote.Size is { } requiredSize &&
                    (requiredSize < 0 || probe.Size != requiredSize))
                    return With(entry, "digest_mismatch", expectedRemote.Algorithm);
                if (probe.Digest is null)
                    return With(entry, "storage_unavailable", "none");
                return CryptographicOperations.FixedTimeEquals(
                    probe.Digest, expectedRemote.Bytes)
                    ? With(entry, expectedRemote.Algorithm == "sha1"
                        ? "weak_digest_match" : "digest_match", expectedRemote.Algorithm)
                    : With(entry, "digest_mismatch", expectedRemote.Algorithm);
            }

            // Filesystem storage uses the current file, not a self-computed
            // expected digest. Remote providers take the path above.
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
        if (entry.Kind == "raw")
        {
            var record = database.GetRawArtifact(entry.Name, entry.Version);
            return record is not null && ArtifactDigests.ValidSha256(record.Sha256) && record.Size >= 0
                ? new Expected("sha256", Convert.FromHexString(record.Sha256), record.Size)
                : null;
        }
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
        if (entry.Kind == "feed-nuget" && entry.FeedId is { } feed)
        {
            var revision = database.GetFeedNugetRevision(feed, entry.Name, entry.Version);
            return revision is not null && NugetDigest.Valid(revision.Sha512) &&
                   revision.Size >= 0
                ? new Expected("sha512", Convert.FromBase64String(revision.Sha512),
                    revision.Size)
                : null;
        }
        if (entry.Kind == "nuget")
        {
            var digest = database.GetNugetDigest(entry.Name, entry.Version);
            return digest is not null &&
                   string.Equals(digest.Origin, entry.Origin, StringComparison.Ordinal) &&
                   NugetDigest.Valid(digest.Sha512) && digest.Size >= 0
                ? new Expected("sha512", Convert.FromBase64String(digest.Sha512), digest.Size)
                : null; // Historical V1/V2 revisions are not retroactively trusted.
        }
        return null;
    }
}
