using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Packory;

/// <summary>
/// Validates the admitted, inert 2025-12-11 server.json shapes. This is a
/// registry-side input policy, not an installer, JSON Schema implementation,
/// ownership proof, or endorsement of remote/package content.
/// </summary>
internal static class McpRegistryManifestShapes
{
    private static readonly Regex VariableName = new(
        @"\A[A-Za-z_][A-Za-z0-9_]{0,63}\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Template = new(
        @"\{([A-Za-z_][A-Za-z0-9_]{0,63})\}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex PackageVersion = new(
        @"\A[A-Za-z0-9][A-Za-z0-9.+_-]{0,127}\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HeaderName = new(
        @"\A[A-Za-z0-9!#$%&'*+.^_`|~-]{1,100}\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex IconSize = new(
        @"\A(?:any|[1-9][0-9]{0,3}x[1-9][0-9]{0,3})\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ExtensionNamespace = new(
        @"\A(?:[a-z][a-z0-9-]*\.)+[a-z][a-z0-9-]*(?:/[a-z0-9][a-z0-9._-]*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static void Fail(string reason) => throw new InvalidDataException(reason);

    private static void Only(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal)))
            Fail("Unsupported manifest object field or type.");
    }

    private static string Text(JsonElement value, int max)
    {
        if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Expected string.");
        var text = value.GetString()!;
        if (text.Length == 0 || text.Length > max ||
            text.Any(c => char.IsControl(c) ||
                char.GetUnicodeCategory(c) == UnicodeCategory.Format))
            throw new InvalidDataException("Unsafe or oversized manifest text.");
        return text;
    }

    private static string Field(JsonElement value, string name, int max) =>
        value.TryGetProperty(name, out var field)
            ? Text(field, max)
            : throw new InvalidDataException("Required manifest field missing.");

    private static bool Flag(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var flag)) return false;
        if (flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("Expected manifest boolean.");
        return flag.GetBoolean();
    }

    private static JsonElement[] Entries(JsonElement obj, string name, int max)
    {
        if (!obj.TryGetProperty(name, out var entries)) return [];
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > max)
            throw new InvalidDataException("Invalid manifest array or limit.");
        return entries.EnumerateArray().ToArray();
    }

    private static bool SafeHttps(string url) => McpRegistryManifest.SafeRemote(url);

    private static bool SensitiveName(string name) =>
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("api-key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("cookie", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> Variables(JsonElement obj)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (!obj.TryGetProperty("variables", out var variables)) return names;
        if (variables.ValueKind != JsonValueKind.Object ||
            variables.EnumerateObject().Count() > 32)
            throw new InvalidDataException("Invalid transport variables.");
        foreach (var field in variables.EnumerateObject())
        {
            if (!VariableName.IsMatch(field.Name)) throw new InvalidDataException("Invalid variable identifier.");
            ValidateInput(field.Value);
            names.Add(field.Name);
        }
        return names;
    }

    private static void ValidateInput(JsonElement input, bool named = false,
        bool argument = false)
    {
        if (argument)
            Only(input, "type", "name", "valueHint", "value", "description", "placeholder",
                "format", "default", "choices", "isRequired", "isSecret", "variables", "isRepeated");
        else if (named)
            Only(input, "name", "value", "description", "placeholder", "format",
                "default", "choices", "isRequired", "isSecret", "variables");
        else
            Only(input, "value", "description", "placeholder", "format",
                "default", "choices", "isRequired", "isSecret");
        foreach (var key in new[] { "description", "placeholder", "default", "value" })
            if (input.TryGetProperty(key, out var value)) Text(value, 512);
        if (input.TryGetProperty("format", out var format) &&
            Text(format, 16) is not ("string" or "number" or "boolean" or "filepath"))
            Fail("Unknown input format.");
        Flag(input, "isRequired");
        var secret = Flag(input, "isSecret");
        if (input.TryGetProperty("choices", out var choices))
        {
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() > 32)
                Fail("Invalid input choices.");
            foreach (var value in choices.EnumerateArray()) Text(value, 256);
        }
        if (secret && (input.TryGetProperty("default", out _) ||
            (input.TryGetProperty("value", out var secretValue) &&
             !Template.IsMatch(Text(secretValue, 512)))))
            Fail("Inline secret values are not admitted.");
        if (named)
        {
            var name = Field(input, "name", 100);
            if (!HeaderName.IsMatch(name)) Fail("Invalid key or header name.");
            if (SensitiveName(name) && input.TryGetProperty("value", out var fixedValue) &&
                !Template.IsMatch(Text(fixedValue, 512)))
                Fail("Fixed sensitive values are not admitted.");
            Variables(input);
        }
        if (argument)
        {
            var kind = Field(input, "type", 16);
            Flag(input, "isRepeated");
            if (kind == "named")
            {
                var name = Field(input, "name", 100);
                if (!name.StartsWith("-", StringComparison.Ordinal) ||
                    name.Any(char.IsWhiteSpace))
                    Fail("Invalid named argument.");
                if (input.TryGetProperty("valueHint", out _))
                    Fail("Named arguments cannot have positional valueHint.");
            }
            else if (kind == "positional")
            {
                if (input.TryGetProperty("name", out _))
                    Fail("Positional arguments cannot have a flag name.");
                if (!input.TryGetProperty("valueHint", out _) &&
                    !input.TryGetProperty("value", out _))
                    Fail("A positional argument needs a value or valueHint.");
                if (input.TryGetProperty("valueHint", out var hint))
                    Text(hint, 100);
            }
            else Fail("Unknown argument type.");
            Variables(input);
        }
    }

    private static void ValidateKeyValue(JsonElement item)
    {
        ValidateInput(item, named: true);
        var name = Field(item, "name", 100);
        if (name.Any(c => c == '\r' || c == '\n')) Fail("Invalid key name.");
        if (SensitiveName(name))
        {
            if (item.TryGetProperty("default", out _))
                Fail("Cannot publish an embedded credential default.");
            if (item.TryGetProperty("value", out var value))
            {
                var raw = Text(value, 512);
                var references = Template.Matches(raw).Select(m => m.Groups[1].Value).ToArray();
                if (references.Length == 0 ||
                    !item.TryGetProperty("variables", out var defs) ||
                    references.Any(reference => !defs.TryGetProperty(reference, out var def) ||
                        !Flag(def, "isSecret")))
                    Fail("Sensitive header/environment values require secret variables.");
            }
        }
    }

    private static bool SafeRemoteTemplate(string url, HashSet<string> vars)
    {
        if (url.Length > 512 || url.Contains('#') || url.Contains('\\'))
            return false;
        var matches = Template.Matches(url);
        if (matches.Any(match => !vars.Contains(match.Groups[1].Value)))
            return false;
        var substituted = Template.Replace(url, "value");
        if (substituted.Contains('{') || substituted.Contains('}')) return false;
        var question = url.IndexOf('?');
        if (question >= 0)
        {
            var parameters = url[(question + 1)..].Split('&');
            if (parameters.Length is < 1 or > 8) return false;
            foreach (var parameter in parameters)
            {
                var parts = parameter.Split('=', 2);
                if (parts.Length != 2 || parts[0].Length == 0 ||
                    parts[0].Any(ch => !char.IsAsciiLetterOrDigit(ch) &&
                        ch is not ('_' or '-' or '.')) ||
                    !Template.IsMatch(parts[1]) ||
                    !Template.Match(parts[1]).Value.Equals(parts[1], StringComparison.Ordinal))
                    return false;
            }
            substituted = substituted[..substituted.IndexOf('?')];
        }
        return SafeHttps(substituted);
    }

    private static void Transport(JsonElement value, bool remote)
    {
        Only(value, "type", "url", "headers", "variables");
        var type = Field(value, "type", 32);
        if (type is not ("stdio" or "sse" or "streamable-http") ||
            (remote && type == "stdio")) Fail("Unknown or invalid transport.");
        if (value.TryGetProperty("variables", out _) && !remote)
            Fail("Package transports do not accept remote variable mappings.");
        var variables = remote ? Variables(value) : new HashSet<string>(StringComparer.Ordinal);
        var hasUrl = value.TryGetProperty("url", out var endpoint);
        if (type == "stdio")
        {
            if (hasUrl || value.TryGetProperty("headers", out _))
                Fail("stdio cannot declare HTTP fields.");
        }
        else if (!hasUrl || !SafeRemoteTemplate(Text(endpoint, 512), variables))
            Fail("Invalid HTTP transport URL or variables.");
        foreach (var header in Entries(value, "headers", 16)) ValidateKeyValue(header);
    }

    private static void Package(JsonElement value)
    {
        Only(value, "registryType", "identifier", "version", "registryBaseUrl",
            "fileSha256", "transport", "environmentVariables", "runtimeArguments",
            "packageArguments", "runtimeHint");
        var registry = Field(value, "registryType", 32);
        if (registry.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            Fail("Invalid registry type.");
        var identifier = Field(value, "identifier", 512);
        if (identifier.Contains("..", StringComparison.Ordinal) || identifier.Contains('\\') ||
            (identifier.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
             !SafeHttps(identifier))) Fail("Unsafe package identifier.");
        if (value.TryGetProperty("version", out var version) &&
            (!PackageVersion.IsMatch(Text(version, 128)) ||
             Text(version, 128) == "latest"))
            Fail("Package versions must be exact.");
        if (value.TryGetProperty("registryBaseUrl", out var registryUrl) &&
            !SafeHttps(Text(registryUrl, 512))) Fail("Unsafe package registry URL.");
        if (value.TryGetProperty("fileSha256", out var checksum) &&
            !ArtifactDigests.ValidSha256(Text(checksum, 64)))
            Fail("Invalid package digest.");
        if (registry == "mcpb" && !value.TryGetProperty("fileSha256", out _))
            Fail("MCPB distributions require a SHA-256.");
        if (!value.TryGetProperty("transport", out var transport))
            Fail("Package transport is required.");
        Transport(transport, remote: false);
        foreach (var variable in Entries(value, "environmentVariables", 32))
            ValidateKeyValue(variable);
        foreach (var key in new[] { "runtimeArguments", "packageArguments" })
            foreach (var argument in Entries(value, key, 32))
                ValidateInput(argument, argument: true);
        if (value.TryGetProperty("runtimeHint", out var runtime))
            Text(runtime, 100);
        if (value.TryGetProperty("runtimeArguments", out _) &&
            !value.TryGetProperty("runtimeHint", out _))
            Fail("Runtime arguments require an explicit runtime hint.");
    }

    private static void Repository(JsonElement value)
    {
        Only(value, "source", "url", "id", "subfolder");
        var source = Field(value, "source", 32);
        if (source.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            Fail("Invalid source identifier.");
        var url = Field(value, "url", 512);
        if (!SafeHttps(url)) Fail("Unsafe repository URL.");
        if (source == "github")
        {
            var uri = new Uri(url);
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.Trim('/').Split('/').Length != 2)
                Fail("Invalid GitHub repository path.");
        }
        if (value.TryGetProperty("id", out var id)) Text(id, 128);
        if (value.TryGetProperty("subfolder", out var path))
        {
            var folder = Text(path, 256);
            if (folder.StartsWith('/') || folder.Contains('\\') || folder.Contains('%') ||
                folder.Split('/').Any(segment => segment is "" or "." or ".."))
                Fail("Invalid repository subfolder.");
        }
    }

    private static void Icon(JsonElement value)
    {
        Only(value, "src", "mimeType", "sizes", "theme");
        if (!SafeHttps(Field(value, "src", 255))) Fail("Unsafe icon source.");
        if (value.TryGetProperty("mimeType", out var mime) &&
            Text(mime, 32) is not ("image/png" or "image/jpeg" or "image/jpg" or
                "image/svg+xml" or "image/webp"))
            Fail("Unsupported icon media type.");
        if (value.TryGetProperty("theme", out var theme) &&
            Text(theme, 16) is not ("light" or "dark"))
            Fail("Invalid icon theme.");
        foreach (var size in Entries(value, "sizes", 12))
            if (!IconSize.IsMatch(Text(size, 16))) Fail("Invalid icon size.");
    }

    private static void ValidateMetadataValue(JsonElement value, int depth)
    {
        if (depth > 10) Fail("Metadata too deeply nested.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.EnumerateObject().Count() > 64) Fail("Metadata too wide.");
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name.Length > 120 || SensitiveName(property.Name))
                    Fail("Sensitive or oversized metadata key.");
                ValidateMetadataValue(property.Value, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() > 64) Fail("Metadata array too large.");
            foreach (var child in value.EnumerateArray()) ValidateMetadataValue(child, depth + 1);
        }
        else if (value.ValueKind == JsonValueKind.String) Text(value, 1024);
        else if (value.ValueKind is not (JsonValueKind.Number or JsonValueKind.True
            or JsonValueKind.False or JsonValueKind.Null))
            Fail("Invalid extension metadata.");
    }

    private static void Metadata(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Count() > 16)
            Fail("Invalid extension metadata.");
        foreach (var field in value.EnumerateObject())
        {
            if (!ExtensionNamespace.IsMatch(field.Name) ||
                field.Value.ValueKind != JsonValueKind.Object)
                Fail("Invalid namespaced metadata.");
            ValidateMetadataValue(field.Value, 0);
        }
    }

    public static string Validate(JsonElement root)
    {
        if (root.TryGetProperty("repository", out var repository)) Repository(repository);
        if (root.TryGetProperty("websiteUrl", out var website) &&
            !SafeHttps(Text(website, 512))) Fail("Unsafe website URL.");
        foreach (var icon in Entries(root, "icons", 16)) Icon(icon);
        if (root.TryGetProperty("_meta", out var metadata)) Metadata(metadata);
        var firstRemote = "";
        var remotes = Entries(root, "remotes", 16);
        foreach (var remote in remotes)
        {
            Transport(remote, remote: true);
            if (firstRemote.Length == 0)
                firstRemote = remote.GetProperty("url").GetString()!;
        }
        var packages = Entries(root, "packages", 16);
        foreach (var package in packages) Package(package);
        if (remotes.Length == 0 && packages.Length == 0)
            Fail("At least one remote or package distribution is required.");
        return firstRemote;
    }
}
