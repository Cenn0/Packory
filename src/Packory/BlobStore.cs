using System.Buffers;
using System.Security.Cryptography;

namespace Packory;

public sealed class BlobStore
{
    private readonly string _root;
    public BlobStore(Settings settings)
    {
        _root = Path.Combine(Path.GetFullPath(settings.DataDirectory), "blobs");
        Directory.CreateDirectory(_root);
    }

    private string PathFor(string key)
    {
        var pieces = key.Split('/');
        if (pieces.Length == 0 || pieces.Any(x => x.Length is 0 or > 255 ||
            x is "." or ".." || x.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
            throw new ArgumentException("Invalid blob key", nameof(key));
        return Path.Combine(_root, Path.Combine(pieces));
    }

    public string? Existing(string key)
    {
        var path = PathFor(key);
        return File.Exists(path) ? path : null;
    }

    public async Task<bool> PutAsync(string key, Stream input, long maxBytes, CancellationToken ct)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = Path.Combine(_root, $".upload-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var writer = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                    {
                        total += read;
                        if (total > maxBytes) throw new PayloadTooLargeException();
                        await writer.WriteAsync(buffer.AsMemory(0, read), ct);
                    }
                    await writer.FlushAsync(ct);
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            try { File.Move(tmp, path); return true; }
            catch (IOException) when (File.Exists(path)) { return false; }
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    public async Task ReplaceAsync(string key, Stream input, long maxBytes, CancellationToken ct)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = Path.Combine(_root, $".meta-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = File.Create(tmp))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += count;
                    if (total > maxBytes) throw new PayloadTooLargeException();
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
            }
            File.Move(tmp, path, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    public static async Task<byte[]> ReadBoundedAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new PayloadTooLargeException();
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        return buffer.ToArray();
    }

    public static string Sha512Base64(byte[] data) => Convert.ToBase64String(SHA512.HashData(data));
}

public sealed class PayloadTooLargeException : Exception { }
