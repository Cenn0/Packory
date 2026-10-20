using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packory;

/// <summary>
/// The published-only discovery profile follows the v0.1 Registry API envelope.
/// Publication, ownership verification and deleted-record synchronization remain
/// separate contracts. A cursor selects a position; it never grants access.
/// </summary>
public static class McpRegistryDiscovery
{
    public const string ApiVersion = "v0.1";
    public const string SpecificationVersion = "2025-12-01";
    public const string SpecificationCommit = "11b7b0e274d518b6f449c5ee3518a6151d9848b9";
    public const string MetadataNamespace = "io.github.cenn0.packory/registry";
    public const int DefaultPageSize = 30;
    public const int MaximumPageSize = 100;

    public static bool ValidSearch(string? search) =>
        search is null || (search.Length <= 100 && !search.Any(c =>
            char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format));

    public static object Response(McpRegistryEntry entry, bool latest = false)
    {
        using var document = JsonDocument.Parse(entry.Json);
        var metadata = new Dictionary<string, object>
        {
            ["status"] = "active", ["reviewState"] = entry.State,
            ["publishedAt"] = entry.UpdatedAt, ["updatedAt"] = entry.UpdatedAt,
            ["sha256"] = entry.Sha256, ["publisherVerified"] = false,
            ["safeToConnect"] = false
        };
        if (latest) metadata["isLatest"] = true;
        return new
        {
            server = document.RootElement.Clone(),
            // Official registry extensions are reserved for that registry.
            _meta = new Dictionary<string, object> { [MetadataNamespace] = metadata }
        };
    }

    public static object Response(McpRegistryPage page, bool latest = false)
    {
        var metadata = new Dictionary<string, object> { ["count"] = page.Items.Count };
        if (page.NextCursor is not null) metadata["nextCursor"] = page.NextCursor;
        return new { servers = page.Items.Select(x => Response(x, latest)), metadata };
    }

    // SemVer precedence compares arbitrary-size numeric identifiers without
    // converting them to a fixed-width integer. Build metadata does not affect
    // precedence; ordinal spelling is a stable tie-breaker for distinct releases.
    public static int CompareVersions(string left, string right)
    {
        static int Numeric(string a, string b) => a.Length != b.Length
            ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        var a = left.Split('+')[0].Split('-', 2);
        var b = right.Split('+')[0].Split('-', 2);
        var ac = a[0].Split('.');
        var bc = b[0].Split('.');
        for (var i = 0; i < 3; i++)
        {
            var result = Numeric(ac[i], bc[i]);
            if (result != 0) return result;
        }
        if (a.Length != b.Length) return a.Length == 1 ? 1 : -1;
        if (a.Length == 2)
        {
            var ap = a[1].Split('.');
            var bp = b[1].Split('.');
            for (var i = 0; i < Math.Min(ap.Length, bp.Length); i++)
            {
                var an = ap[i].All(char.IsAsciiDigit);
                var bn = bp[i].All(char.IsAsciiDigit);
                var result = an != bn ? (an ? -1 : 1) : an
                    ? Numeric(ap[i], bp[i]) : string.CompareOrdinal(ap[i], bp[i]);
                if (result != 0) return result;
            }
            if (ap.Length != bp.Length) return ap.Length.CompareTo(bp.Length);
        }
        return string.CompareOrdinal(left, right);
    }
}

public sealed record McpRegistryPage(IReadOnlyList<McpRegistryEntry> Items, string? NextCursor);

internal sealed record McpRegistryCursor(int Format, string Scope, string Name,
    string Version, string PublishedAt)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 3
    };

    public static string ScopeFor(string mode, string? name, string? search, string? version) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new[] { mode, name ?? "", search?.ToLowerInvariant() ?? "", version ?? "" })));

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Create(string scope, McpRegistryEntry entry) => Encode(
        JsonSerializer.SerializeToUtf8Bytes(new McpRegistryCursor(
            1, scope, entry.Name, entry.Version, entry.UpdatedAt), JsonOptions));

    public static McpRegistryCursor? Parse(string? value, string scope)
    {
        if (value is null) return null;
        try
        {
            if (value.Length is < 1 or > 1024 ||
                value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new FormatException();
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var bytes = Convert.FromBase64String(base64);
            var decoded = JsonSerializer.Deserialize<McpRegistryCursor>(bytes, JsonOptions);
            if (decoded is null || decoded.Format != 1 || decoded.Scope != scope ||
                !McpRegistryManifest.ValidName(decoded.Name) ||
                !McpRegistryManifest.ValidVersion(decoded.Version) ||
                !DateTimeOffset.TryParseExact(decoded.PublishedAt, "O",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                date.Offset != TimeSpan.Zero ||
                Encode(JsonSerializer.SerializeToUtf8Bytes(decoded, JsonOptions)) != value)
                throw new FormatException();
            return decoded;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("Invalid MCP registry cursor or cursor filters.", nameof(value));
        }
    }
}
