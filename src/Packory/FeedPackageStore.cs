using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Packory;

public sealed record FeedPackageContent(JsonElement Metadata, byte[] Payload);

/// <summary>
/// One immutable object commits protocol metadata and original package bytes
/// together. The outer ledger digest and the inner identity are both checked
/// before either metadata or bytes become visible to a private adapter.
/// </summary>
public static class FeedPackageStore
{
    public const long MaxMetadataBytes = 4L * 1024 * 1024;
    private const long ZipOverhead = 65536;

    public static long MaxStoredBytes(long maxPayloadBytes) => checked(maxPayloadBytes + MaxMetadataBytes + ZipOverhead);

    public static string BlobKey(string feedId, string kind, string path)
    {
        if (!FeedDefinition.ValidId(feedId) || kind is not ("npm" or "cargo" or "pypi") ||
            string.IsNullOrEmpty(path) || path.Length > 2048)
            throw new ArgumentException("Invalid private package identity.");
        return "feeds/" + feedId + "/" + kind + "/" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + ".bundle";
    }

    public static async Task<bool> PublishAsync(string feedId, string kind, string path,
        string name, string version, JsonElement metadata, byte[] payload,
        IPackageDatabase db, BlobStore blobs, ArtifactLocks locks,
        long maxPayloadBytes, string actor, CancellationToken ct)
    {
        if (payload.LongLength == 0 || payload.LongLength > maxPayloadBytes)
            throw new PayloadTooLargeException();
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Package metadata must be an object.");
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, feedId, kind, path, name, version,
            payloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
            payloadSize = payload.LongLength, metadata
        });
        if (envelope.LongLength > MaxMetadataBytes) throw new PayloadTooLargeException();
        byte[] bytes;
        using (var memory = new MemoryStream())
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var output = zip.CreateEntry("metadata.json", CompressionLevel.NoCompression).Open())
                    output.Write(envelope);
                using (var output = zip.CreateEntry("payload", CompressionLevel.NoCompression).Open())
                    output.Write(payload);
            }
            bytes = memory.ToArray();
        }
        var entry = new FeedAssetEntry(feedId, kind, path, name, version,
            "application/vnd.packory." + kind + "-package+zip",
            Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength,
            DateTimeOffset.UtcNow.ToString("O"));
        if (!FeedAssetEntry.Valid(entry)) throw new InvalidDataException("Invalid private package coordinate.");
        var key = BlobKey(feedId, kind, path);
        using var lease = await locks.AcquireAsync("feed-" + kind, feedId + "/" + name,
            path, ct);
        if (db.GetFeedAsset(feedId, kind, path) is not null || blobs.Contains(key)) return false;
        db.BeginPublication(key, "feed-" + kind, feedId + "|" + path, version);
        using var input = new MemoryStream(bytes, writable: false);
        if (!await blobs.PutAsync(key, input, MaxStoredBytes(maxPayloadBytes), ct))
            return false;
        // A failed metadata commit intentionally keeps the intent and object;
        // recovery reports the pending publication without inventing a serving row.
        if (!db.PublishFeedAsset(entry, actor)) return false;
        db.CompletePublication(key);
        return true;
    }

    public static async Task<FeedPackageContent?> ReadAsync(string feedId, string kind,
        string path, IPackageDatabase db, BlobStore blobs, long maxPayloadBytes,
        CancellationToken ct)
    {
        var entry = db.GetFeedAsset(feedId, kind, path);
        if (entry is null) return null;
        var maxBundle = MaxStoredBytes(maxPayloadBytes);
        if (!FeedAssetEntry.Valid(entry) || entry.FeedId != feedId ||
            entry.Kind != kind || entry.Path != path || entry.Size > maxBundle)
            throw new InvalidDataException("Invalid committed private package metadata.");
        var key = BlobKey(feedId, kind, path);
        if (blobs is S3BlobStore remote)
        {
            var probe = await remote.InspectRemoteAsync(key, "sha256", maxBundle, ct);
            if (!probe.Exists || probe.ExceedsLimit || probe.Size != entry.Size ||
                probe.Digest is null || Convert.ToHexStringLower(probe.Digest) != entry.Sha256)
                throw new InvalidDataException("Remote private package digest mismatch.");
        }
        var stored = blobs.Existing(key);
        if (stored is null) throw new InvalidDataException("Private package bytes unavailable.");
        byte[] bytes;
        try
        {
            await using var file = new FileStream(stored, FileMode.Open, FileAccess.Read,
                FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            bytes = await BlobStore.ReadBoundedAsync(file, maxBundle, ct);
        }
        catch (PayloadTooLargeException error)
        { throw new InvalidDataException("Stored private package exceeds its limit.", error); }
        if (bytes.LongLength != entry.Size ||
            Convert.ToHexStringLower(SHA256.HashData(bytes)) != entry.Sha256)
            throw new InvalidDataException("Private package digest mismatch.");
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count != 2 || zip.GetEntry("metadata.json") is not { } envelopeEntry ||
                zip.GetEntry("payload") is not { } payloadEntry ||
                envelopeEntry.Length is <= 0 or > MaxMetadataBytes ||
                payloadEntry.Length <= 0 || payloadEntry.Length > maxPayloadBytes)
                throw new InvalidDataException("Unexpected private package bundle shape.");
            byte[] envelopeBytes;
            using (var stream = envelopeEntry.Open())
                envelopeBytes = await BlobStore.ReadBoundedAsync(stream, MaxMetadataBytes, ct);
            using var document = JsonDocument.Parse(envelopeBytes, new JsonDocumentOptions { MaxDepth = 80 });
            var envelope = document.RootElement;
            if (envelope.ValueKind != JsonValueKind.Object ||
                envelope.GetProperty("schemaVersion").GetInt32() != 1 ||
                envelope.GetProperty("feedId").GetString() != feedId ||
                envelope.GetProperty("kind").GetString() != kind ||
                envelope.GetProperty("path").GetString() != path ||
                envelope.GetProperty("name").GetString() != entry.Name ||
                envelope.GetProperty("version").GetString() != entry.Version ||
                envelope.GetProperty("payloadSize").GetInt64() != payloadEntry.Length ||
                envelope.GetProperty("metadata").ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Private package bundle identity mismatch.");
            byte[] payload;
            using (var stream = payloadEntry.Open())
                payload = await BlobStore.ReadBoundedAsync(stream, maxPayloadBytes, ct);
            if (payload.LongLength != payloadEntry.Length ||
                Convert.ToHexStringLower(SHA256.HashData(payload)) != envelope.GetProperty("payloadSha256").GetString())
                throw new InvalidDataException("Private package payload integrity mismatch.");
            return new FeedPackageContent(envelope.GetProperty("metadata").Clone(), payload);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or
            InvalidOperationException or FormatException or PayloadTooLargeException)
        {
            throw new InvalidDataException("Invalid private package bundle.", error);
        }
    }
}
