using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Packory;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DatabaseParameterDefinition(string Name, string Type, bool Required, int MaxLength);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DatabaseQueryDefinition(string Name, string Description, string Sql,
    DatabaseParameterDefinition[] Parameters, int MaxRows, int TimeoutSeconds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DatabaseConnectorDefinition(string Id, string Name, string Provider,
    string Host, int Port, string Database, string Username, string TlsServerName,
    string? OracleServerCertificateDn, bool Enabled, DatabaseQueryDefinition[] Queries, long Revision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DatabaseConnectorChange(DatabaseConnectorDefinition Definition,
    string? Password, bool ClearPassword);

public sealed record DatabaseConnectorRecord(DatabaseConnectorDefinition Definition,
    [property: JsonIgnore] string? SecretCiphertext, long UpdatedAt)
{
    public object Summary() => new { definition = Definition,
        hasPassword = SecretCiphertext is not null, updatedAt = UpdatedAt };
}

/// <summary>Operator-owned network policy, independent of model arguments and dashboard input.</summary>
public sealed class DatabaseConnectorPolicy
{
    public static readonly string[] Providers = ["postgresql", "mysql", "mariadb", "sqlserver", "oracle"];
    public static readonly string[] ParameterTypes = ["string", "int64", "decimal", "bool", "uuid", "date", "datetime"];
    private readonly HashSet<string> targets;
    private readonly string[] privateCidrs;
    public bool Enabled { get; }
    public IReadOnlyCollection<string> AllowedTargets => targets;

    public DatabaseConnectorPolicy(IEnumerable<string> allowedTargets,
        IEnumerable<string>? allowedPrivateCidrs = null, bool enabled = true)
    {
        Enabled = enabled;
        targets = allowedTargets.Select(NormalizeTarget).ToHashSet(StringComparer.Ordinal);
        privateCidrs = (allowedPrivateCidrs ?? []).ToArray();
        if (targets.Count > 64 || privateCidrs.Length > 16 ||
            privateCidrs.Any(c => !System.Net.IPNetwork.TryParse(c, out _)))
            throw new InvalidDataException("Invalid database connector network policy.");
    }

    public static DatabaseConnectorPolicy FromEnvironment()
    {
        static string[] Split(string key) => (Environment.GetEnvironmentVariable(key) ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var flag = Environment.GetEnvironmentVariable("PACKORY_DB_CONNECTORS_ENABLED");
        if (flag is not null && !bool.TryParse(flag, out _))
            throw new InvalidDataException("PACKORY_DB_CONNECTORS_ENABLED must be true or false.");
        return new(Split("PACKORY_DB_CONNECTOR_ALLOWED_TARGETS"),
            Split("PACKORY_DB_CONNECTOR_ALLOWED_PRIVATE_CIDRS"),
            string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase));
    }

    public static bool ValidName(string? value) => value is { Length: >= 1 and <= 64 } &&
        Regex.IsMatch(value, @"\A[a-zA-Z_][a-zA-Z0-9_]*\z", RegexOptions.CultureInvariant);

    private static bool ValidHost(string? host) => host is { Length: >= 1 and <= 253 } &&
        host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':') &&
        !host.EndsWith('.') && Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;

    private static string Target(string provider, string host, int port) =>
        provider + "://" + (host.Contains(':') ? "[" + host.ToLowerInvariant() + "]" : host.ToLowerInvariant()) + ":" + port;

    private static string NormalizeTarget(string raw)
    {
        if (raw.Length > 320 || raw.Any(char.IsControl) || raw.Contains('%') ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || !Providers.Contains(uri.Scheme) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath is not ("" or "/") || uri.Port is < 1 or > 65535 ||
            !ValidHost(uri.IdnHost))
            throw new InvalidDataException("Database targets must specify an exact provider, host and port.");
        return Target(uri.Scheme, uri.IdnHost, uri.Port);
    }

    public bool AddressAllowed(IPAddress address) => SkillAiTransport.AddressAllowed(
        new SkillAiEndpoint("database", "openai-compatible", "https://database.invalid", privateCidrs), address);

    public void ValidateTarget(DatabaseConnectorDefinition definition)
    {
        if (!Providers.Contains(definition.Provider) || !ValidHost(definition.Host) ||
            definition.Port is < 1 or > 65535 || !ValidHost(definition.TlsServerName) ||
            !targets.Contains(Target(definition.Provider, definition.Host, definition.Port)))
            throw new InvalidDataException("Database target is not approved by the operator.");
        if (IPAddress.TryParse(definition.Host, out var literal) && !AddressAllowed(literal))
            throw new InvalidDataException("Database address is not approved by the operator.");
    }

    public async Task<IPAddress> ResolveAsync(DatabaseConnectorDefinition definition, CancellationToken cancellationToken)
    {
        if (!Enabled) throw new UnauthorizedAccessException("Database connectors are disabled.");
        ValidateTarget(definition);
        var addresses = IPAddress.TryParse(definition.Host, out var literal)
            ? [literal] : await Dns.GetHostAddressesAsync(definition.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(a => !AddressAllowed(a)))
            throw new UnauthorizedAccessException("Database address is not approved by the operator.");
        // The executor connects to this numeric address; it must never re-resolve the hostname.
        return addresses[0].IsIPv4MappedToIPv6 ? addresses[0].MapToIPv4() : addresses[0];
    }

    public void Validate(DatabaseConnectorDefinition d)
    {
        if (!McpConnectorPolicy.ValidId(d.Id) || !Text(d.Name, 120) || d.Revision < 0 ||
            !Text(d.Database, 128) || !Text(d.Username, 128) || d.Queries is null || d.Queries.Length > 32 ||
            d.Queries.Any(q => q is null) ||
            d.Queries.Select(q => q.Name).Distinct(StringComparer.Ordinal).Count() != d.Queries.Length)
            throw new InvalidDataException("Invalid database connector definition.");
        ValidateTarget(d);
        if (d.Provider == "oracle")
        {
            if (!Regex.IsMatch(d.Database, @"\A[a-zA-Z0-9_.-]+\z", RegexOptions.CultureInvariant) ||
                !Text(d.OracleServerCertificateDn, 1024) ||
                d.OracleServerCertificateDn!.IndexOfAny(['(', ')', '\\', '\n', '\r', '"']) >= 0)
                throw new InvalidDataException("Oracle requires the expected TLS certificate distinguished name.");
        }
        else if (!string.IsNullOrEmpty(d.OracleServerCertificateDn))
            throw new InvalidDataException("Oracle TLS identity is only valid for Oracle connectors.");
        foreach (var query in d.Queries) ValidateQuery(query, d.Provider);
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(d, SkillAiJson.Options)) > 49152)
            throw new InvalidDataException("Database definition exceeds the size limit.");
    }

    private static bool Text(string? value, int limit) => value is { Length: >= 1 } &&
        value.Length <= limit && value == value.Trim() && !value.Any(char.IsControl);

    public static void ValidateQuery(DatabaseQueryDefinition q, string? provider = null)
    {
        if (!ValidName(q.Name) || !Text(q.Description, 512) || q.Sql is null ||
            q.Sql.Length is < 6 or > 8192 || q.Parameters is null || q.Parameters.Length > 32 ||
            q.MaxRows is < 1 or > 1000 || q.TimeoutSeconds is < 1 or > 30 ||
            q.Parameters.Any(p => p is null || !ValidName(p.Name) || !ParameterTypes.Contains(p.Type) ||
                p.MaxLength is < 1 or > 2048) ||
            q.Parameters.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != q.Parameters.Length)
            throw new InvalidDataException("Invalid database query contract.");
        // This conservative lexical gate prevents accidental batches/DDL. It is not a SQL sandbox:
        // trusted operators approve templates and database permissions remain the authorization boundary.
        // Disallow comments, dollar quoting and quoted identifiers to make this reviewable in every dialect.
        var sql = q.Sql.Trim();
        if (sql.Contains(';') || sql.Contains("--", StringComparison.Ordinal) ||
            sql.Contains("/*", StringComparison.Ordinal) || sql.Contains("*/", StringComparison.Ordinal) ||
            sql.Contains('`') || sql.Contains('"') || sql.Contains('[') || sql.Contains(']') || sql.Contains('$') ||
            sql.Contains('\\') || sql.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new InvalidDataException("Only a single approved SELECT template is allowed.");
        var tokens = new StringBuilder(sql.Length);
        var quoted = false;
        for (var i = 0; i < sql.Length; i++)
        {
            if (sql[i] == '\'')
            {
                if (quoted && i + 1 < sql.Length && sql[i + 1] == '\'') { i++; continue; }
                quoted = !quoted;
                tokens.Append(' ');
            }
            else tokens.Append(quoted ? ' ' : sql[i]);
        }
        var words = Regex.Matches(tokens.ToString(), @"[a-zA-Z_][a-zA-Z0-9_]*")
            .Select(m => m.Value.ToUpperInvariant()).ToArray();
        string[] rejected = ["INSERT", "UPDATE", "DELETE", "MERGE", "INTO", "CREATE", "ALTER", "DROP", "TRUNCATE",
            "GRANT", "REVOKE", "EXEC", "EXECUTE", "CALL", "COPY", "LOAD", "ATTACH", "DETACH", "PRAGMA",
            "SET", "BEGIN", "COMMIT", "ROLLBACK", "FOR", "LOCK", "OPENROWSET", "OPENDATASOURCE",
            "OPENQUERY", "OUTFILE", "DUMPFILE"];
        if (quoted || words.Length == 0 || words[0] != "SELECT" || words.Any(rejected.Contains))
            throw new InvalidDataException("Only a single approved SELECT template is allowed.");
        var matches = Regex.Matches(tokens.ToString(), @"(?<![@:])[@:]([a-zA-Z_][a-zA-Z0-9_]*)");
        if (provider is not null && matches.Any(m => m.Value[0] != (provider == "oracle" ? ':' : '@')))
            throw new InvalidDataException("The parameter placeholder prefix does not match this database provider.");
        var placeholders = matches.Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        if (!placeholders.SetEquals(q.Parameters.Select(p => p.Name)))
            throw new InvalidDataException("Query parameters must match the named placeholders exactly.");
    }

    public static string CredentialContext(DatabaseConnectorDefinition d) => "database-v1:" +
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { d.Provider, d.Host, d.Port, d.Database, d.Username, d.TlsServerName, d.OracleServerCertificateDn })));
}
