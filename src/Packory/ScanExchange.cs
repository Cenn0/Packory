using System.Text;

namespace Packory;

/// <summary>Reports from isolated scanners remain untrusted, bounded UTF-8 files.</summary>
internal static class ScanExchange
{
    internal static string ReadText(string path, int maxBytes)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LinkTarget is not null || info.Length > maxBytes)
            throw new InvalidDataException("Invalid scanner exchange file.");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = new byte[maxBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = input.Read(bytes, length, bytes.Length - length);
            if (count == 0) break;
            length += count;
        }
        if (length > maxBytes) throw new InvalidDataException("Oversized scanner exchange file.");
        return new UTF8Encoding(false, true).GetString(bytes, 0, length);
    }
}
