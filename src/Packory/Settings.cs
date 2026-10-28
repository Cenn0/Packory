using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using Npgsql;

namespace Packory;

public sealed class Settings
{
    public string DataDirectory { get; set; } = "./data";
    public string MetadataProvider { get; set; } = "sqlite";
    // writer (exclusive) or reader (no mutation, no registry mirror caching).
    public string InstanceRole { get; set; } = "writer";
    public string BlobProvider { get; set; } = "filesystem";
    public string? S3Endpoint { get; set; }
    public string? S3Bucket { get; set; }
    public string S3Region { get; set; } = "us-east-1";
    public string S3KeyPrefix { get; set; } = "packory";
    public string? S3AccessKey { get; set; }
    public string? S3SecretKey { get; set; }
    // HTTP is deliberately restricted to loopback integration testing.
    public bool S3AllowLoopbackHttp { get; set; }
    public string? PostgresConnectionString { get; set; }
    public string PublicBaseUrl { get; set; } = "http://localhost:8080";
    public string NugetUpstreamUrl { get; set; } = "https://api.nuget.org/v3-flatcontainer/";
    public string NpmUpstreamUrl { get; set; } = "https://registry.npmjs.org/";
    public string NugetRegistrationUrl { get; set; } = "https://api.nuget.org/v3/registration5-semver2/";
    public bool AnonymousRead { get; set; } = true;
    // Explicit offline maintenance for controlled PostgreSQL/S3 quarantine operations.
    public bool RecoveryMaintenance { get; set; }
    // Opt-in deployment security checks only. Passing is not production certification.
    public bool StrictDeployment { get; set; }
    // Opt-in Docker authentication handshake even when generic anonymous reads are enabled.
    public bool OciAuthenticatedPing { get; set; }
    // Security enforcement never trusts the mere existence of a staged ZIP.
    public bool SkillSecurityEnforced { get; set; } = true;
    public bool McpEnabled { get; set; } = true;
    // Explicit opt-in: managed PostgreSQL connector gateway (never anonymous).
    public bool McpConnectorsEnabled { get; set; }
    // Independent, opt-in validation evidence: never on by default.
    public bool ValidationEnabled { get; set; }
    public string? ValidationExportDirectory { get; set; }
    public string? ValidationVerifierPublicKeyPath { get; set; }
    public string? ValidationVerifierKeyId { get; set; }
    public string ValidationAudience { get; set; } = "packory-validation";
    public string ValidationTenant { get; set; } = "local";
    public string[] ValidationApprovedAliases { get; set; } = [];
    // Operator pins each alias to one exact signed export byte digest.
    // Prevents replacement with an older but still validly signed export.
    public string[] ValidationApprovedExports { get; set; } = [];

    public string[] ValidationRevokedKeyIds { get; set; } = [];

    public long MaxArtifactBytes { get; set; } = 100 * 1024 * 1024;
    // Optional single-node filesystem capacity guard. Zero disables it.
    // This does not provide a distributed S3 usage quota.
    public long MaxBlobStorageBytes { get; set; }

    // Logical committed bytes/count independently enforced for every private Raw feed.
    // Zero disables each limit; not a physical bucket or multi-writer quota.
    public long MaxRawFeedBytes { get; set; }
    public long MaxRawFeedArtifacts { get; set; }

    public int PublishRequestsPerMinute { get; set; } = 60;
    public int MaxMetadataBytes { get; set; } = 16 * 1024 * 1024;
    // Optional OIDC resource-server authentication; configure both fields or neither.
    public string? OidcAuthority { get; set; }
    public string? OidcAudience { get; set; }
    public bool OidcEnabled => !string.IsNullOrWhiteSpace(OidcAuthority);
    // A confidential interactive web client is separate from the JWT API audience.
    public string? OidcClientId { get; set; }
    public string? OidcClientSecret { get; set; }
    // Optional custom OAuth scope requested by the browser client for role claims.
    public string? OidcWebScope { get; set; }
    // Opt in only after the browser/native-publish contract has been qualified.
    public bool BrowserNativePublishEnabled { get; set; }
    // Only this reverse-proxy IP may supply X-Forwarded-Proto for browser SSO.
    public string? TrustedProxyIp { get; set; }
    public bool OidcWebEnabled => !string.IsNullOrWhiteSpace(OidcClientId) &&
                                  !string.IsNullOrWhiteSpace(OidcClientSecret);
    public string? AdminToken { get; set; }
    public string? ReadToken { get; set; }
    public string? WriteToken { get; set; }
    // Optional namespace policy for the shared write-token.
    // Entries are "kind:name" or "kind:prefix*" (e.g. "nuget:internal.*").
    public string[] WriteNamespaces { get; set; } = [];
    // Opt-in: a signed OIDC packory.write token must carry matching packory_namespace claim.
    public bool RequireOidcNamespaceGrants { get; set; }

    // Explicit reservations prevent upstream fallback for private namespaces.
    public string[] ReservedNugetPrefixes { get; set; } = [];
    public string[] ReservedNpmScopes { get; set; } = [];
    // Registry publication/use policy; empty by default to avoid surprising existing users.
    public string[] DeniedNugetPrefixes { get; set; } = [];
    public string[] DeniedNpmScopes { get; set; } = [];

    public bool DeniesNuget(string name) => DeniedNugetPrefixes.Any(
        prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    public bool DeniesNpm(string name) => DeniedNpmScopes.Any(
        scope => name.StartsWith(scope + "/", StringComparison.Ordinal));

    public bool IsReservedNuget(string id) => ReservedNugetPrefixes.Any(
        prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public bool IsReservedNpm(string name) => ReservedNpmScopes.Any(
        scope => name.StartsWith(scope + "/", StringComparison.Ordinal));

    public void Validate()
    {
        if (McpConnectorsEnabled && (!McpEnabled || !OidcEnabled ||
            InstanceRole != "writer" || RecoveryMaintenance))
            throw new InvalidOperationException("Managed MCP connectors require OIDC, MCP and an active writer.");
        if (MaxRawFeedBytes is < 0 or > 1_099_511_627_776L ||
            MaxRawFeedArtifacts is < 0 or > 100_000_000)
            throw new InvalidOperationException("Invalid private Raw feed quota.");
        if (ValidationEnabled)
        {
            // Validate all operator-provided selectors before an MCP tool can be registered.
            // The verifier key is never provisioned from an agent request.
            if (!McpEnabled || !OidcEnabled ||
                string.IsNullOrWhiteSpace(ValidationExportDirectory) ||
                string.IsNullOrWhiteSpace(ValidationVerifierPublicKeyPath) ||
                !Path.IsPathFullyQualified(ValidationExportDirectory) ||
                !Path.IsPathFullyQualified(ValidationVerifierPublicKeyPath) ||
                !System.Text.RegularExpressions.Regex.IsMatch(ValidationVerifierKeyId ?? "",
                    @"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant) ||
                !System.Text.RegularExpressions.Regex.IsMatch(ValidationAudience,
                    @"\A[a-z][a-z0-9.-]{0,63}\z", RegexOptions.CultureInvariant) ||
                !System.Text.RegularExpressions.Regex.IsMatch(ValidationTenant,
                    @"\A[a-z][a-z0-9.-]{0,63}\z", RegexOptions.CultureInvariant) ||
                ValidationApprovedAliases.Length is < 1 or > 100 ||
                ValidationApprovedAliases.Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x,
                    @"\A[a-z][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant)) ||
                ValidationApprovedAliases.Distinct(StringComparer.Ordinal).Count() != ValidationApprovedAliases.Length ||
                ValidationApprovedExports.Length != ValidationApprovedAliases.Length ||
                ValidationApprovedExports.Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x,
                    @"\A[a-z][a-z0-9-]{0,63}=sha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant)) ||
                ValidationApprovedExports.Select(x => x.Split('=')[0]).Distinct(StringComparer.Ordinal).Count()
                    != ValidationApprovedExports.Length ||
                !ValidationApprovedAliases.Order(StringComparer.Ordinal).SequenceEqual(
                    ValidationApprovedExports.Select(x => x.Split('=')[0]).Order(StringComparer.Ordinal)) ||

                ValidationRevokedKeyIds.Any(x => !System.Text.RegularExpressions.Regex.IsMatch(x,
                    @"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)) ||
                ValidationRevokedKeyIds.Contains(ValidationVerifierKeyId, StringComparer.Ordinal))
                throw new InvalidOperationException("Invalid opt-in validation configuration.");
            // Refuse startup with a missing/unsafe trust anchor or filesystem root.
            // In particular, a private signing key must never be mounted into Packory.
            try
            {
                var root = new DirectoryInfo(ValidationExportDirectory!);
                var key = new FileInfo(ValidationVerifierPublicKeyPath!);
                if (!root.Exists || (root.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    !key.Exists || (key.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    key.Length is < 100 or > 8192)
                    throw new InvalidOperationException("Invalid validation trust files.");
                var pem = File.ReadAllText(key.FullName);
                if (!pem.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) ||
                    pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
                    throw new InvalidOperationException("Validation requires a PUBLIC verifier key only.");
                using var rsa = RSA.Create();
                rsa.ImportFromPem(pem);
                if (rsa.KeySize < 2048)
                    throw new InvalidOperationException("Validation verifier RSA key is too small.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or
                CryptographicException or ArgumentException)
            {
                throw new InvalidOperationException("Validation trust anchor is not readable or valid.");
            }
        }
        if (BlobProvider is not ("filesystem" or "s3"))
            throw new InvalidOperationException("BlobProvider must be filesystem or s3.");
        if (BlobProvider == "s3")
        {
            if (string.IsNullOrWhiteSpace(S3Bucket) || S3Bucket.Length is < 3 or > 63 ||
                !Regex.IsMatch(S3Bucket, "\\A[a-z0-9][a-z0-9.-]*[a-z0-9]\\z", RegexOptions.CultureInvariant) ||
                S3Bucket.Contains("..", StringComparison.Ordinal))
                throw new InvalidOperationException("S3 requires a valid bucket name.");
            if (string.IsNullOrWhiteSpace(S3Region) || S3Region.Length > 64 ||
                !Regex.IsMatch(S3Region, "\\A[a-z0-9-]+\\z", RegexOptions.CultureInvariant) ||
                string.IsNullOrEmpty(S3KeyPrefix) || S3KeyPrefix.Length > 100 ||
                !Regex.IsMatch(S3KeyPrefix, "\\A[a-z0-9][a-z0-9/_-]*\\z", RegexOptions.CultureInvariant) ||
                S3KeyPrefix.Split('/').Any(x => x.Length == 0))
                throw new InvalidOperationException("S3 region or key prefix is invalid.");
            if (string.IsNullOrWhiteSpace(S3AccessKey) != string.IsNullOrWhiteSpace(S3SecretKey))
                throw new InvalidOperationException("S3 static credentials must be set together.");
            if (S3Endpoint is not null)
            {
                if (!Uri.TryCreate(S3Endpoint, UriKind.Absolute, out var endpoint) ||
                    (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback && S3AllowLoopbackHttp)) ||
                    !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.AbsolutePath != "/" ||
                    !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
                    throw new InvalidOperationException("S3 endpoint requires HTTPS (HTTP allowed for explicitly enabled loopback tests only).");
            }
        }
        if (MetadataProvider is not ("sqlite" or "postgresql"))
            throw new InvalidOperationException("MetadataProvider must be sqlite or postgresql.");
        if (InstanceRole is not ("writer" or "reader"))
            throw new InvalidOperationException("InstanceRole must be writer or reader.");
        if (InstanceRole == "reader" &&
            (MetadataProvider != "postgresql" || BlobProvider != "s3" || RecoveryMaintenance))
            throw new InvalidOperationException("Read-only replicas require PostgreSQL/S3 and no maintenance mode.");
        if (RecoveryMaintenance && (MetadataProvider != "postgresql" || BlobProvider != "s3"))
            throw new InvalidOperationException("Recovery maintenance requires PostgreSQL metadata and S3 blobs.");
        if (MetadataProvider == "postgresql" && string.IsNullOrWhiteSpace(PostgresConnectionString))
            throw new InvalidOperationException("PostgreSQL connection string is required.");
        if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(origin.UserInfo) ||
            origin.AbsolutePath.Trim('/') != "" || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            throw new InvalidOperationException("Packory:PublicBaseUrl must be an HTTP(S) origin without path, query, or credentials.");
        foreach (var value in new[] { NugetUpstreamUrl, NugetRegistrationUrl, NpmUpstreamUrl })
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var upstream) || upstream.Scheme != "https" ||
                !string.IsNullOrEmpty(upstream.UserInfo) || !string.IsNullOrEmpty(upstream.Query) || !string.IsNullOrEmpty(upstream.Fragment))
                throw new InvalidOperationException("Upstream registry URL must use HTTPS and contain no credentials, query, or fragment.");
        }
        if (OidcEnabled != !string.IsNullOrWhiteSpace(OidcAudience))
            throw new InvalidOperationException("OIDC authority and audience must be configured together.");
        if (OidcEnabled && (!Uri.TryCreate(OidcAuthority, UriKind.Absolute, out var oidc) ||
            oidc.Scheme != "https" || !string.IsNullOrEmpty(oidc.UserInfo) ||
            !string.IsNullOrEmpty(oidc.Query) || !string.IsNullOrEmpty(oidc.Fragment) ||
            string.IsNullOrWhiteSpace(OidcAudience) || OidcAudience.Length > 256))
            throw new InvalidOperationException("OIDC requires an HTTPS authority and nonempty audience.");
        if ((!string.IsNullOrWhiteSpace(OidcClientId) ||
             !string.IsNullOrWhiteSpace(OidcClientSecret)) && !OidcWebEnabled)
            throw new InvalidOperationException("OIDC browser client ID and secret must be configured together.");
        if (OidcWebEnabled && (!OidcEnabled || OidcClientId!.Length > 256 ||
            OidcClientSecret!.Length < 16 ||
            (!string.Equals(origin.Scheme, "https", StringComparison.Ordinal) && !origin.IsLoopback)))
            throw new InvalidOperationException("Browser SSO requires an OIDC issuer, confidential client, and HTTPS public origin (HTTP only on loopback).");
        if (OidcWebScope is { Length: > 0 } webScope &&
            (!OidcWebEnabled || webScope.Length > 128 ||
             webScope.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-' and not '.')))
            throw new InvalidOperationException("OIDC web scope must be a simple scope name and requires browser SSO.");
        if (BrowserNativePublishEnabled && !OidcWebEnabled)
            throw new InvalidOperationException("Browser-native publishing requires confidential browser SSO.");
        if (TrustedProxyIp is { Length: > 0 } proxyIp &&
            (!IPAddress.TryParse(proxyIp, out var parsedProxy) ||
             IPAddress.Any.Equals(parsedProxy) || IPAddress.IPv6Any.Equals(parsedProxy)))
            throw new InvalidOperationException("Trusted reverse proxy must be one specific IP address.");
        if (WriteNamespaces.Any(x => !Access.ValidNamespaceGrant(x)))
            throw new InvalidOperationException("Invalid WriteNamespaces grant.");
        if (MaxBlobStorageBytes < 0 || MaxBlobStorageBytes > 1_099_511_627_776L ||
            (MaxBlobStorageBytes != 0 && BlobProvider != "filesystem"))
            throw new InvalidOperationException("Filesystem blob quota must be 0..1 TiB and requires filesystem provider.");
        if (StrictDeployment)
        {
            if (MetadataProvider != "postgresql" || BlobProvider != "s3")
                throw new InvalidOperationException("StrictDeployment requires PostgreSQL metadata and S3 object storage.");
            if (!Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var publicUri) ||
                publicUri.Scheme != "https" || publicUri.IsLoopback)
                throw new InvalidOperationException("StrictDeployment requires an externally reachable HTTPS public origin.");
            if (AnonymousRead)
                throw new InvalidOperationException("StrictDeployment requires authenticated package reads.");
            if (string.IsNullOrWhiteSpace(AdminToken) && !OidcEnabled)
                throw new InvalidOperationException("StrictDeployment requires an administrator or validated OIDC resource-server.");
            if (S3AllowLoopbackHttp)
                throw new InvalidOperationException("StrictDeployment prohibits insecure S3 emulator fallback.");
            NpgsqlConnectionStringBuilder postgres;
            try { postgres = new NpgsqlConnectionStringBuilder(PostgresConnectionString); }
            catch (ArgumentException)
            {
                throw new InvalidOperationException("StrictDeployment requires a valid PostgreSQL connection string.");
            }
            if (postgres.SslMode != SslMode.VerifyFull)
                throw new InvalidOperationException("StrictDeployment requires PostgreSQL SSL Mode=VerifyFull and a trusted hostname-checked certificate.");
        }
        if (PublishRequestsPerMinute is < 1 or > 10000)
            throw new InvalidOperationException("PublishRequestsPerMinute must be between 1 and 10000.");
        if (MaxArtifactBytes is < 1024 or > 536870912 || MaxMetadataBytes is < 1024 or > 67108864)
            throw new InvalidOperationException("Configured upload limits are outside supported bounds.");
        if (ReservedNugetPrefixes.Concat(DeniedNugetPrefixes).Any(prefix => string.IsNullOrEmpty(prefix) || !Names.Nuget(prefix)) ||
            ReservedNpmScopes.Concat(DeniedNpmScopes).Any(scope => string.IsNullOrEmpty(scope) || !scope.StartsWith('@') ||
                                           !Names.Npm(scope + "/test")))
            throw new InvalidOperationException("Invalid reserved NuGet prefix or npm scope.");
        foreach (var token in new[] { AdminToken, ReadToken, WriteToken }.Where(t => t is not null))
        {
            if (token!.Length < 32) throw new InvalidOperationException("API tokens must contain at least 32 characters.");
        }
        if (new[] { AdminToken, ReadToken, WriteToken }.Where(t => t is not null).Distinct().Count() !=
            new[] { AdminToken, ReadToken, WriteToken }.Count(t => t is not null))
            throw new InvalidOperationException("API tokens must be distinct.");
    }

    public string Base => PublicBaseUrl.TrimEnd('/');
}

public static partial class Names
{
    // Full-string anchors reject the final newline accepted by the dollar anchor.
    // Never trim invalid identifiers: doing so aliases different native requests.
    [GeneratedRegex("\\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NugetRegex();
    [GeneratedRegex("\\A[0-9A-Za-z][0-9A-Za-z.+-]{0,127}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();
    [GeneratedRegex("\\A(?:@[a-z0-9][a-z0-9._-]{0,100}/)?[a-z0-9][a-z0-9._-]{0,100}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NpmRegex();
    [GeneratedRegex("\\A[a-z0-9]+(?:-[a-z0-9]+)*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SkillRegex();

    public static bool Nuget(string? id) => id is not null && NugetRegex().IsMatch(id);
    public static bool Version(string? version) => version is not null && VersionRegex().IsMatch(version);
    public static bool Npm(string? name) => name is not null && name.Length <= 214 && NpmRegex().IsMatch(name);
    public static bool Skill(string? name) => name is not null && name.Length <= 64 && SkillRegex().IsMatch(name);
    public static string EscapedNpm(string name) => Uri.EscapeDataString(name);
    public static string HashName(string name) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)));
}
