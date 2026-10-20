using System.Text.Json;
using ModelContextProtocol;

namespace Packory;

/// <summary>
/// Opaque, versioned markers for skill enumeration. A marker selects a position,
/// never grants access: every page independently checks the current release state.
/// Unlike offsets, removing an earlier row cannot skip a subsequent revision.
/// </summary>
internal static class McpSkillCursor
{
    private const string Prefix = "s1.";

    public static string Encode(string scope, string first, string second = "") =>
        Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
                new[] { scope, first, second }))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static (string First, string Second)? Decode(string? cursor, string scope)
    {
        if (cursor is null) return null;
        try
        {
            if (cursor.Length is < 4 or > 8192 ||
                !cursor.StartsWith(Prefix, StringComparison.Ordinal)) throw Invalid();
            var encoded = cursor[Prefix.Length..];
            if (encoded.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw Invalid();
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            using var document = JsonDocument.Parse(Convert.FromBase64String(padded),
                new JsonDocumentOptions { MaxDepth = 2 });
            var array = document.RootElement;
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != 3 ||
                array.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String) ||
                array[0].GetString() != scope) throw Invalid();
            var first = array[1].GetString()!;
            var second = array[2].GetString()!;
            if (first.Length is < 1 or > 512 || second.Length > 128 ||
                first.Any(char.IsControl) || second.Any(char.IsControl) ||
                Encode(scope, first, second) != cursor) throw Invalid();
            return (first, second);
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException)
        {
            throw Invalid();
        }
    }

    public static McpProtocolException Invalid() =>
        new("Invalid or incompatible pagination cursor.", McpErrorCode.InvalidParams);

    public static string? Parameter(System.Text.Json.Nodes.JsonNode? parameters,
        string name, bool required = false, int maximum = 8192)
    {
        if (parameters is not null && parameters is not System.Text.Json.Nodes.JsonObject)
            throw new McpProtocolException("Expected an object of request parameters.",
                McpErrorCode.InvalidParams);
        var value = parameters?[name];
        if (value is null && !required && (parameters is null ||
            parameters is System.Text.Json.Nodes.JsonObject absent && !absent.ContainsKey(name))) return null;
        if (value is not System.Text.Json.Nodes.JsonValue scalar ||
            !scalar.TryGetValue<string>(out var text) || text.Length > maximum)
            throw new McpProtocolException("Missing or invalid " + name + " parameter.",
                McpErrorCode.InvalidParams);
        return text;
    }
}
