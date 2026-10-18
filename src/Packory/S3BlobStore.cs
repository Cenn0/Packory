using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Packory;

/// <summary>
/// S3-backed artifact objects with a bounded local read-through file cache.
/// The repository is single-process; the local file layout remains an internal
/// protocol adapter detail, NOT the authoritative artifact store in this mode.
/// This implementation does not provide multi-node coordination or S3 multipart uploads.
/// </summary>
public sealed class S3BlobStore : BlobStore, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly Settings _settings;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly long _maxReadBytes;

    public S3BlobStore(Settings settings) : base(settings)
    {
        _settings = settings;
        _bucket = settings.S3Bucket ?? throw new InvalidOperationException("S3 bucket is not configured.");
        _prefix = settings.S3KeyPrefix.TrimEnd('/') + "/";
        _maxReadBytes = Math.Max(settings.MaxArtifactBytes, settings.MaxMetadataBytes);
        var config = new AmazonS3Config
        {
            ForcePathStyle = true,
            AuthenticationRegion = settings.S3Region
        };
        if (settings.S3Endpoint is { } endpoint)
            config.ServiceURL = endpoint;
        else
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.S3Region);

        _client = !string.IsNullOrWhiteSpace(settings.S3AccessKey) &&
                  !string.IsNullOrWhiteSpace(settings.S3SecretKey)
            ? new AmazonS3Client(new BasicAWSCredentials(settings.S3AccessKey, settings.S3SecretKey), config)
            : new AmazonS3Client(config); // AWS default credential chain: IAM role/instance profile.
    }

    private string ObjectKey(string key)
    {
        // Validate locally using the same path constraints as filesystem BlobStore.
        PathFor(key);
        return _prefix + key;
    }

    private async Task<bool> RemoteExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = ObjectKey(key)
            }, ct);
            return true;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// Read-only, cancellation-aware operator integrity probe. GETs the actual
    /// remote S3 object rather than hashing a possibly stale local cache.
    /// Never treats ETag as a cryptographic digest, updates metadata, or writes
    /// remote storage. Streaming bounds memory independently from artifact size.
    /// </summary>
    public sealed record RemoteIntegrityProbe(bool Exists, bool ExceedsLimit, long Size, byte[]? Digest);

    public async Task<RemoteIntegrityProbe> InspectRemoteAsync(
        string key, string? algorithm, long maxBytes, CancellationToken ct)
    {
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (algorithm is not (null or "sha256" or "sha512" or "sha1"))
            throw new ArgumentException("Unsupported integrity algorithm", nameof(algorithm));
        var objectKey = ObjectKey(key);
        try
        {
            if (algorithm is null)
            {
                // No expected checksum exists. HEAD is enough to establish
                // existence; never claim to have verified its contents.
                var head = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = _bucket, Key = objectKey
                }, ct);
                return new RemoteIntegrityProbe(true, head.ContentLength > maxBytes,
                    head.ContentLength, null);
            }

            using var response = await _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucket, Key = objectKey
            }, ct);
            using var hash = IncrementalHash.CreateHash(algorithm switch
            {
                "sha256" => HashAlgorithmName.SHA256,
                "sha512" => HashAlgorithmName.SHA512,
                _ => HashAlgorithmName.SHA1
            });
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            long size = 0;
            try
            {
                int read;
                while ((read = await response.ResponseStream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    size = checked(size + read);
                    if (size > maxBytes)
                        return new RemoteIntegrityProbe(true, true, size, null);
                    hash.AppendData(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return new RemoteIntegrityProbe(true, false, size, hash.GetHashAndReset());
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return new RemoteIntegrityProbe(false, false, 0, null);
        }
    }

    public override bool Contains(string key) =>
        RemoteExistsAsync(key, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();

    public override string? Existing(string key)
    {
        // The S3 bucket is authoritative, not a stale local file after remote deletion.
        if (!Contains(key))
        {
            DeleteCached(key);
            return null;
        }

        var path = base.Existing(key);
        if (path is null)
        {
            using var response = _client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucket, Key = ObjectKey(key)
            }).ConfigureAwait(false).GetAwaiter().GetResult();
            var completed = base.PutAsync(key, response.ResponseStream, _maxReadBytes,
                CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
            path = base.Existing(key);
            if (!completed && path is null)
                throw new IOException("S3 local materialization failed");
        }

        // Check cached OCI bytes too: previously downloaded data can be corrupted locally.
        if (key.StartsWith("oci/blobs/sha256/", StringComparison.Ordinal) &&
            key.EndsWith(".blob", StringComparison.Ordinal) ||
            key.StartsWith("oci/manifests/sha256/", StringComparison.Ordinal) &&
            key.EndsWith(".json", StringComparison.Ordinal))
        {
            var expected = Path.GetFileNameWithoutExtension(key);
            using var input = File.OpenRead(path!);
            var actual = Convert.ToHexStringLower(SHA256.HashData(input));
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                DeleteCached(key);
                throw new InvalidDataException("S3 object SHA256 does not match content-addressed key");
            }
        }
        return path;
    }

    public override async Task<bool> PutAsync(string key, Stream input, long maxBytes, CancellationToken ct)
    {
        if (await RemoteExistsAsync(key, ct)) return false;
        if (!await base.PutAsync(key, input, maxBytes, ct)) return false;

        var local = base.Existing(key) ?? throw new IOException("Temporary S3 source was not persisted.");
        try
        {
            await using var payload = File.OpenRead(local);
            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = ObjectKey(key),
                InputStream = payload,
                IfNoneMatch = "*",
                AutoCloseStream = false
            };
            var response = await _client.PutObjectAsync(request, ct);
            if (response.HttpStatusCode != HttpStatusCode.OK)
                throw new IOException("S3 immutable object write was not acknowledged.");
            return true;
        }
        catch (AmazonS3Exception e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            DeleteCached(key);
            return false;
        }
        catch
        {
            DeleteCached(key);
            throw;
        }
    }

    public override async Task ReplaceAsync(string key, Stream input, long maxBytes, CancellationToken ct)
    {
        // Mutable registry metadata is small and bounded. Never modify the cache
        // until the remote write succeeds. Upstream packages are immutable PutAsync.
        var bytes = await ReadBoundedAsync(input, maxBytes, ct);
        using (var payload = new MemoryStream(bytes, writable: false))
        {
            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = ObjectKey(key),
                InputStream = payload,
                AutoCloseStream = false
            };
            var result = await _client.PutObjectAsync(request, ct);
            if (result.HttpStatusCode != HttpStatusCode.OK)
                throw new IOException("S3 mutable metadata update was not acknowledged.");
        }
        try
        {
            await base.ReplaceAsync(key, new MemoryStream(bytes, writable: false), maxBytes, ct);
        }
        catch
        {
            DeleteCached(key);
            throw;
        }
    }

    /// <summary>
    /// Preserve an orphan as independently verified immutable evidence before any
    /// conditional deletion. A crash before DB PrepareQuarantine leaves the original
    /// untouched; its extra archive is intentionally not garbage-collected.
    /// </summary>
    public async Task<QuarantineEvidence> ArchiveOrphanAsync(string key, CancellationToken ct)
    {
        var original = ObjectKey(key);
        var info = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
        {
            BucketName = _bucket, Key = original
        }, ct);
        var etag = info.ETag;
        if (string.IsNullOrWhiteSpace(etag))
            throw new InvalidDataException("S3 orphan ETag is unavailable");
        using var response = await _client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _bucket, Key = original, EtagToMatch = etag
        }, ct);
        var payload = await ReadBoundedAsync(response.ResponseStream, _maxReadBytes, ct);
        var checksum = Convert.ToHexStringLower(SHA256.HashData(payload));
        var recovery = "recovery/quarantine/" + Guid.NewGuid().ToString("N") + "/" + key;
        using (var input = new MemoryStream(payload, writable: false))
        {
            var saved = await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucket, Key = ObjectKey(recovery),
                InputStream = input, IfNoneMatch = "*", AutoCloseStream = false
            }, ct);
            if (saved.HttpStatusCode != HttpStatusCode.OK)
                throw new IOException("Recovery copy was not acknowledged");
        }
        var evidence = new QuarantineEvidence(key, recovery, checksum, etag,
            DateTimeOffset.UtcNow.ToString("O"), false);
        await VerifyArchivedAsync(evidence, ct);
        return evidence;
    }

    public async Task VerifyArchivedAsync(QuarantineEvidence evidence, CancellationToken ct)
    {
        if (!evidence.RecoveryKey.StartsWith("recovery/quarantine/", StringComparison.Ordinal) ||
            !evidence.RecoveryKey.EndsWith("/" + evidence.Key, StringComparison.Ordinal))
            throw new InvalidDataException("Unexpected quarantine evidence location");
        using var recovered = await _client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = _bucket, Key = ObjectKey(evidence.RecoveryKey)
        }, ct);
        var bytes = await ReadBoundedAsync(recovered.ResponseStream, _maxReadBytes, ct);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != evidence.Sha256)
            throw new InvalidDataException("Quarantine evidence SHA256 mismatch");
    }

    /// <summary>Never delete without an already persisted and verified archive.</summary>
    public async Task DeleteOriginalConditionallyAsync(QuarantineEvidence evidence, CancellationToken ct)
    {
        await VerifyArchivedAsync(evidence, ct);
        try
        {
            var head = await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket, Key = ObjectKey(evidence.Key)
            }, ct);
            if (head.ETag != evidence.SourceEtag)
                throw new InvalidOperationException("Original S3 object changed since recovery copy");
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            DeleteCached(evidence.Key);
            return; // A prior conditional deletion succeeded before a crash.
        }
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _bucket, Key = ObjectKey(evidence.Key),
                IfMatch = evidence.SourceEtag
            }, ct);
        }
        catch (AmazonS3Exception error) when (
            error.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException("Conditional S3 deletion refused: object changed", error);
        }
        DeleteCached(evidence.Key);
    }

    public void Dispose() => _client.Dispose();
}
