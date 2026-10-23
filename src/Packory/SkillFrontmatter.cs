using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace Packory;

/// <summary>One bounded YAML contract for Skill admission and discovery.</summary>
public static class SkillFrontmatter
{
    public static JsonObject Parse(string markdown)
    {
        var normalized = markdown.Replace("\r\n", "\n");
        if (!normalized.EndsWith('\n')) normalized += "\n";
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            throw new InvalidDataException("Missing frontmatter.");
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0 || end > 256 * 1024)
            throw new InvalidDataException("Invalid frontmatter.");
        var yaml = new YamlStream();
        yaml.Load(new StringReader(normalized[4..end]));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode mapping)
            throw new InvalidDataException("Invalid YAML mapping.");
        var remainingNodes = 4096;
        return (JsonObject)ToNode(mapping, 0, ref remainingNodes)!;
    }

    private static JsonNode? ToNode(YamlNode node, int depth, ref int remainingNodes)
    {
        if (depth > 12 || --remainingNodes < 0)
            throw new InvalidDataException("Frontmatter exceeds structural limits.");
        if (node is YamlMappingNode map)
        {
            if (map.Children.Count > 100) throw new InvalidDataException("Oversized frontmatter.");
            var obj = new JsonObject();
            foreach (var (key, value) in map.Children)
            {
                if (key is not YamlScalarNode scalar || scalar.Value is not { } text ||
                    text.Length > 128 || obj.ContainsKey(text))
                    throw new InvalidDataException("Invalid or duplicate YAML key.");
                obj[text] = ToNode(value, depth + 1, ref remainingNodes);
            }
            return obj;
        }
        if (node is YamlSequenceNode seq)
        {
            if (seq.Children.Count > 100) throw new InvalidDataException("Oversized list.");
            var arr = new JsonArray();
            foreach (var child in seq.Children) arr.Add(ToNode(child, depth + 1, ref remainingNodes));
            return arr;
        }
        if (node is YamlScalarNode s)
        {
            if (s.Value?.Length > 65536) throw new InvalidDataException("Oversized scalar.");
            var value = s.Value ?? "";
            var tag = s.Tag.ToString();
            if (tag is "tag:yaml.org,2002:str" or "!") return JsonValue.Create(value);
            var isNull = value is "" or "~" or "null" or "Null" or "NULL";
            var isBoolean = value is "true" or "True" or "TRUE" or "false" or "False" or "FALSE";
            // Explicit tags constrain the value's type. An invalid !!int must
            // never fall through to bool/null/string inference, for example.
            if (tag == "tag:yaml.org,2002:null")
            {
                if (!isNull) throw new InvalidDataException("Invalid YAML null scalar.");
                return null;
            }
            if (tag == "tag:yaml.org,2002:bool")
            {
                if (!isBoolean) throw new InvalidDataException("Invalid YAML boolean scalar.");
                return JsonValue.Create(value[0] is 't' or 'T');
            }
            if (tag is "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float")
            {
                var integerOnly = tag == "tag:yaml.org,2002:int";
                var numeric = NormalizeYamlNumber(value, integerOnly, allowBasedInteger: integerOnly);
                if (numeric is null) throw new InvalidDataException("Invalid JSON-compatible YAML numeric scalar.");
                return JsonNode.Parse(numeric);
            }
            if (s.Style == YamlDotNet.Core.ScalarStyle.Plain)
            {
                if (isNull) return null;
                if (isBoolean) return JsonValue.Create(value[0] is 't' or 'T');
                if (NormalizeYamlNumber(value, false) is { } numeric) return JsonNode.Parse(numeric);
            }
            return JsonValue.Create(value)!;
        }
        throw new InvalidDataException("Unsupported YAML alias/tag.");
    }

    // Convert finite YAML Core numeric spellings to exact JSON lexemes. Never
    // round through decimal/double: +1e-100 and .000...001 must retain their value.
    private static string? NormalizeYamlNumber(string value, bool integerOnly, bool allowBasedInteger = true)
    {
        if (value.Length == 0) return null;
        if (value.StartsWith("0x", StringComparison.Ordinal) || value.StartsWith("0o", StringComparison.Ordinal))
        {
            if (!allowBasedInteger) return null;
            var bits = value[1] == 'x' ? 4 : 3;
            if (value.Length == 2) return null;
            // Pack the digits in linear time, including integers outside Int64.
            var bytes = new byte[((value.Length - 2) * bits + 7) / 8];
            var bit = 0;
            for (var i = value.Length - 1; i >= 2; i--, bit += bits)
            {
                var c = value[i];
                var digit = c is >= '0' and <= '9' ? c - '0' :
                    c is >= 'a' and <= 'f' ? c - 'a' + 10 :
                    c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;
                if (digit < 0 || digit >= (1 << bits)) return null;
                bytes[bit / 8] |= (byte)(digit << (bit % 8));
                if (bit % 8 + bits > 8) bytes[bit / 8 + 1] |= (byte)(digit >> (8 - bit % 8));
            }
            return new System.Numerics.BigInteger(bytes, isUnsigned: true).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }
        var index = value[0] is '+' or '-' ? 1 : 0;
        var integerStart = index;
        while (index < value.Length && char.IsAsciiDigit(value[index])) index++;
        var integerEnd = index;
        var fractionStart = -1;
        if (!integerOnly && index < value.Length && value[index] == '.')
        {
            fractionStart = ++index;
            while (index < value.Length && char.IsAsciiDigit(value[index])) index++;
        }
        var fractionEnd = index;
        if (integerStart == integerEnd && (fractionStart < 0 || fractionStart == fractionEnd)) return null;
        var exponentStart = index;
        if (!integerOnly && index < value.Length && value[index] is 'e' or 'E')
        {
            index++;
            if (index < value.Length && value[index] is '+' or '-') index++;
            var exponentDigits = index;
            while (index < value.Length && char.IsAsciiDigit(value[index])) index++;
            if (exponentDigits == index) return null;
        }
        if (index != value.Length) return null;
        while (integerStart < integerEnd && value[integerStart] == '0') integerStart++;
        var integer = integerStart == integerEnd ? "0" : value[integerStart..integerEnd];
        var fraction = fractionStart < 0 ? "" : "." +
            (fractionStart == fractionEnd ? "0" : value[fractionStart..fractionEnd]);
        return (value[0] == '-' ? "-" : "") + integer + fraction + value[exponentStart..];
    }

}
