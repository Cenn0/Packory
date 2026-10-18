using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Packory;

/// <summary>
/// Strict offline reader for operator-approved, independently signed sanitized exports.
/// The signing key must belong to an externally operated verifier; this code never runs tests.
/// </summary>
public sealed class ValidationEvidenceStore(Settings settings)
{
    // Capture the verifier trust anchor at service construction; rotating a
    // file on disk cannot silently rebind the authenticated signer mid-process.
    private readonly byte[] _pinnedVerifierKey = settings.ValidationEnabled
        ? ReadBounded(settings.ValidationVerifierPublicKeyPath!, 8192) : [];

    private static readonly Regex AliasPattern = new(@"\A[a-z][a-z0-9-]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly Regex IdentifierPattern = new(@"\A[a-z][a-z0-9.-]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly Regex KeyPattern = new(@"\A[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new(@"\Asha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    private static readonly Regex CodePattern = new(@"\A[A-Z][A-Z0-9_]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> EnvelopeFields = new(StringComparer.Ordinal)
    {
        "schema_version", "alias", "audience", "tenant", "source_digest", "run_binding_digest",
        "evidence_digest", "verifier_key_id", "issued_at", "expires_at", "status", "findings"
    };
    private static readonly HashSet<string> FindingFields = new(StringComparer.Ordinal)
    {
        "code", "severity"
    };

    public sealed record Finding(string Code, string Severity);
    public sealed record Run(string Alias, string Status, string ExpiresAt,
        string SourceDigest, string RunBindingDigest, string EvidenceDigest,
        string EvidenceIntegrity, string RunBinding, string SignatureAuthentication,
        IReadOnlyList<Finding> Findings);

    public IReadOnlyList<object> List()
    {
        if (!settings.ValidationEnabled) return [];
        var entries = new List<object>();
        foreach (var alias in settings.ValidationApprovedAliases.OrderBy(x => x, StringComparer.Ordinal).Take(100))
        {
            if (Get(alias) is { } run)
                entries.Add(new { run.Alias, run.Status, run.ExpiresAt });
        }
        return entries;
    }

    public Run? Get(string? alias)
    {
        if (!settings.ValidationEnabled || alias is null || !AliasPattern.IsMatch(alias) ||
            !settings.ValidationApprovedAliases.Contains(alias, StringComparer.Ordinal))
            return null;
        try
        {
            var root = settings.ValidationExportDirectory!;
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return null;
            var contentPath = Path.Combine(root, alias + ".json");
            var sigPath = Path.Combine(root, alias + ".sig");
            if (!RegularFile(contentPath, 32768) || !RegularFile(sigPath, 1024)) return null;
            var bytes = ReadBounded(contentPath, 32768);
            var signatureText = new UTF8Encoding(false, true).GetString(ReadBounded(sigPath, 1024)).Trim();
            // Detached signatures alone do not resist validly signed but stale
            // alias swaps. Operator approval must pin the EXACT signed bytes.
            var approval = settings.ValidationApprovedExports.FirstOrDefault(
                x => x.StartsWith(alias + "=", StringComparison.Ordinal));
            var expected = approval is null ? "" : approval[(alias.Length + 1)..];
            var actual = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (expected.Length != actual.Length ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual)))
                return null;
            var signature = Convert.FromBase64String(signatureText);
            using var rsa = RSA.Create();
            rsa.ImportFromPem(new UTF8Encoding(false, true).GetString(_pinnedVerifierKey));
            if (!rsa.VerifyData(bytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                return null;
            // Reject invalid UTF-8 rather than replacing malicious or ambiguous bytes.
            var json = new UTF8Encoding(false, true).GetString(bytes);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = 5, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow
            });
            var rootElement = doc.RootElement;
            if (!Exactly(rootElement, EnvelopeFields) ||
                rootElement.GetProperty("schema_version").ValueKind != JsonValueKind.Number ||
                rootElement.GetProperty("schema_version").GetInt32() != 1) return null;
            string Field(string name) => rootElement.GetProperty(name).GetString() ?? "";
            if (rootElement.GetProperty("alias").ValueKind != JsonValueKind.String ||
                Field("alias") != alias ||
                !IdentifierPattern.IsMatch(Field("audience")) || Field("audience") != settings.ValidationAudience ||
                !IdentifierPattern.IsMatch(Field("tenant")) || Field("tenant") != settings.ValidationTenant ||
                !KeyPattern.IsMatch(Field("verifier_key_id")) ||
                Field("verifier_key_id") != settings.ValidationVerifierKeyId ||
                settings.ValidationRevokedKeyIds.Contains(Field("verifier_key_id"), StringComparer.Ordinal) ||
                !DigestPattern.IsMatch(Field("source_digest")) ||
                !DigestPattern.IsMatch(Field("run_binding_digest")) ||
                !DigestPattern.IsMatch(Field("evidence_digest"))) return null;
            var status = Field("status");
            if (status is not ("passed" or "failed" or "inconclusive")) return null;
            if (!DateTimeOffset.TryParseExact(Field("issued_at"), "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var issued) ||
                !DateTimeOffset.TryParseExact(Field("expires_at"), "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires) ||
                issued > DateTimeOffset.UtcNow.AddMinutes(5) ||
                expires <= DateTimeOffset.UtcNow || expires <= issued ||
                expires - issued > TimeSpan.FromDays(30)) return null;
            var findingsJson = rootElement.GetProperty("findings");
            if (findingsJson.ValueKind != JsonValueKind.Array || findingsJson.GetArrayLength() > 50)
                return null;
            var findings = new List<Finding>();
            foreach (var finding in findingsJson.EnumerateArray())
            {
                if (!Exactly(finding, FindingFields)) return null;
                var code = finding.GetProperty("code").GetString() ?? "";
                var severity = finding.GetProperty("severity").GetString() ?? "";
                if (!CodePattern.IsMatch(code) || severity is not
                    ("info" or "low" or "medium" or "high" or "critical")) return null;
                findings.Add(new Finding(code, severity));
            }
            // These statuses describe authenticated export claims, not independent re-execution.
            return new Run(alias, status, Field("expires_at"), Field("source_digest"),
                Field("run_binding_digest"), Field("evidence_digest"),
                "signed_attestation", "signed_attestation", "rsa_pss_sha256_verified", findings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or FormatException or CryptographicException or InvalidOperationException
            or KeyNotFoundException)
        {
            // Invalid or absent evidence is indistinguishable from unavailable evidence.
            return null;
        }
    }

    // Bound reads themselves: file length checks alone permit a race where a
    // file grows between stat() and read(). This never buffers past the cap.
    private static byte[] ReadBounded(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.SequentialScan);
        using var buffer = new MemoryStream(maxBytes);
        var chunk = new byte[4096];
        int count;
        while ((count = stream.Read(chunk, 0, Math.Min(chunk.Length, maxBytes + 1 - (int)buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, count);
            if (buffer.Length > maxBytes) throw new IOException("Evidence file exceeds bounded limit.");
        }
        return buffer.ToArray();
    }

    private static bool RegularFile(string path, long maxBytes)
    {
        var file = new FileInfo(path);
        return file.Exists && (file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0 &&
            file.Length <= maxBytes;
    }

    private static bool Exactly(JsonElement element, HashSet<string> fields)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!fields.Contains(property.Name) || !observed.Add(property.Name)) return false;
        return observed.SetEquals(fields);
    }
}
