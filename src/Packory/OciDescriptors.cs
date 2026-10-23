using System.Security.Cryptography;
using System.Text.Json;

namespace Packory;

/// <summary>Shared hosted OCI descriptor admission; no alternate download origins.</summary>
internal static class OciDescriptors
{
    public static bool TryRead(JsonElement node, out string? digest, out long size, out string? media)
    {
        digest = media = null;
        size = 0;
        if (node.ValueKind != JsonValueKind.Object ||
            // Native OCI clients may try these URLs before their registry.
            // This hosted profile never publishes alternate download origins.
            node.TryGetProperty("urls", out _) ||
            !node.TryGetProperty("digest", out var dg) || dg.ValueKind != JsonValueKind.String ||
            !OciEndpoints.ValidDigest(dg.GetString() ?? "") ||
            !node.TryGetProperty("size", out var length) || length.ValueKind != JsonValueKind.Number ||
            !length.TryGetInt64(out size) || size < 0 ||
            !node.TryGetProperty("mediaType", out var mt) || mt.ValueKind != JsonValueKind.String ||
            !RawEndpoints.ValidMediaType(mt.GetString())) return false;
        digest = dg.GetString();
        media = mt.GetString();
        if (node.TryGetProperty("data", out var embedded))
        {
            if (embedded.ValueKind != JsonValueKind.String) return false;
            var encoded = embedded.GetString()!;
            // The enclosing manifest is metadata-bounded. Reject impossible
            // lengths before allocating the inline content, then verify its
            // identity. Inline bytes never replace the same-feed ledger check.
            var maximumDecoded = encoded.Length / 4L * 3;
            if (encoded.Length % 4 != 0 || size > maximumDecoded || maximumDecoded - size > 2)
                return false;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(encoded); }
            catch (FormatException) { return false; }
            if (bytes.LongLength != size || Convert.ToBase64String(bytes) != encoded ||
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)) != digest)
                return false;
        }
        return true;
    }

    public static bool UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueProperties(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (!UniqueProperties(item)) return false;
        return true;
    }

}
