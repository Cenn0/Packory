using System.Net;
using System.Text.RegularExpressions;

namespace Packory;

/// <summary>
/// Deployment-owned network boundaries. Admin UI configuration cannot add an egress origin
/// or grant access to private addresses; both require operator configuration outside Packory.
/// </summary>
public sealed class McpConnectorPolicy
{
    public IReadOnlyList<string> AllowedOrigins { get; }
    public string[] AllowedPrivateCidrs { get; }

    public McpConnectorPolicy(IEnumerable<string> allowedOrigins, IEnumerable<string>? allowedPrivateCidrs = null)
    {
        var origins = allowedOrigins.ToArray();
        var networks = (allowedPrivateCidrs ?? []).ToArray();
        if (origins.Length > 32 || networks.Length > 16 || origins.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Invalid MCP connector egress configuration.");
        foreach (var value in networks)
            if (!IPNetwork.TryParse(value, out _))
                throw new InvalidDataException("Invalid MCP connector private CIDR.");
        var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in origins)
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                uri.HostNameType == UriHostNameType.Unknown || raw.Length > 255 ||
                raw.Contains('%') || raw.Any(char.IsControl))
                throw new InvalidDataException("Allowed MCP origins must be HTTPS origins without paths or credentials.");
            normalized.Add(uri.GetLeftPart(UriPartial.Authority));
        }
        AllowedOrigins = normalized.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        AllowedPrivateCidrs = networks;
    }

    public static McpConnectorPolicy FromEnvironment()
    {
        static string[] Read(string name) =>
            (Environment.GetEnvironmentVariable(name) ?? "")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return new McpConnectorPolicy(Read("PACKORY_MCP_CONNECTOR_ALLOWED_ORIGINS"),
            Read("PACKORY_MCP_CONNECTOR_ALLOWED_PRIVATE_CIDRS"));
    }

    public Uri ValidateEndpoint(string? endpoint)
    {
        if (endpoint is null || endpoint.Length is < 12 or > 512 ||
            endpoint.Any(char.IsControl) || endpoint.Contains('%') ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.HostNameType == UriHostNameType.Unknown ||
            !AllowedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority),
                StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("MCP endpoint is not approved by deployment egress policy.");
        if (IPAddress.TryParse(uri.Host, out var literal) && !AddressAllowed(uri, literal))
            throw new InvalidDataException("MCP endpoint resolves to a forbidden address.");
        return uri;
    }

    public bool AddressAllowed(Uri endpoint, IPAddress address) =>
        // Reuse the vetted public/forbidden/private address classification from the
        // existing AI outbound transport. Pin the numeric address in ConnectCallback.
        SkillAiTransport.AddressAllowed(new SkillAiEndpoint("mcp", "openai-compatible",
            endpoint.GetLeftPart(UriPartial.Authority), AllowedPrivateCidrs), address);

    public static bool ValidId(string? value) =>
        value is not null && Regex.IsMatch(value, @"\A[a-z][a-z0-9-]{1,62}\z",
            RegexOptions.CultureInvariant);

    public static bool ValidTool(string? value) =>
        value is not null && Regex.IsMatch(value, @"\A[A-Za-z0-9_][A-Za-z0-9_./-]{0,127}\z",
            RegexOptions.CultureInvariant);
}
