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

    public void Dispose() => _client.Dispose();
}
