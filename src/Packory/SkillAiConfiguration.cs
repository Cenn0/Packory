using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Packory;

/// <summary>Deployment policy is intentionally not writable from the browser.</summary>
public sealed record SkillAiEndpoint(string Id, string Provider, string BaseUrl,
    string[]? AllowedPrivateCidrs = null)
{
    public Uri Uri => new(BaseUrl.TrimEnd('/') + "/");
    public bool External => Provider != "ollama";
}

public sealed class SkillAiDeployment
{
    public const string ScannerCommit = "091a509b3d6ba8ff5be528966e1f43c2e68cf214";
    public const string ScannerVersion = "2.13.0";
    public const string Profile = "packory.skill-ai/1";
    public SkillAiEndpoint[] Endpoints { get; init; } =
    [
        new("openai", "openai", "https://api.openai.com/v1"),
        new("anthropic", "anthropic", "https://api.anthropic.com")
    ];
    public string? KeyRingFile { get; init; }

    public static SkillAiDeployment FromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("PACKORY_SKILL_AI_ENDPOINTS_FILE");
        return new SkillAiDeployment
        {
            Endpoints = string.IsNullOrEmpty(path) ? new SkillAiDeployment().Endpoints
                : SkillAiJson.Read<SkillAiEndpoint[]>(File.ReadAllText(path), 32 * 1024),
            KeyRingFile = Environment.GetEnvironmentVariable("PACKORY_SKILL_AI_KEYRING_FILE")
        }.Validate();
    }

    public SkillAiDeployment Validate()
    {
        if (Endpoints is null || Endpoints.Any(e => e is null || e.Id is null) || Endpoints.Length > 20 || Endpoints.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() != Endpoints.Length)
            throw new InvalidDataException("Invalid AI endpoint inventory.");
        foreach (var endpoint in Endpoints)
        {
            if (!Regex.IsMatch(endpoint.Id, "^[a-z][a-z0-9-]{0,47}$", RegexOptions.CultureInvariant) ||
                endpoint.Provider is not ("openai" or "openai-compatible" or "ollama" or "anthropic" or "azure-openai") ||
                !System.Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0 ||
                uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.HostNameType == UriHostNameType.Unknown ||
                endpoint.BaseUrl.Length > 512 || endpoint.BaseUrl.Contains('%') ||
                (uri.Scheme != "https" && endpoint.Provider != "ollama"))
                throw new InvalidDataException("Invalid AI endpoint policy.");
            if (endpoint.Provider == "openai" && endpoint.BaseUrl.TrimEnd('/') != "https://api.openai.com/v1")
                throw new InvalidDataException("Use an explicitly allowed compatible endpoint for custom hosts.");
            if (endpoint.Provider == "anthropic" && endpoint.BaseUrl.TrimEnd('/') != "https://api.anthropic.com")
                throw new InvalidDataException("The native Anthropic profile requires the official origin.");
            foreach (var cidr in endpoint.AllowedPrivateCidrs ?? [])
                if (!System.Net.IPNetwork.TryParse(cidr, out _))
                    throw new InvalidDataException("Invalid AI endpoint network.");
        }
        return this;
    }

    public SkillAiEndpoint Resolve(string id) => Endpoints.SingleOrDefault(e => e.Id == id)
        ?? throw new InvalidDataException("Endpoint is not allowed by the deployment policy.");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillAiConfiguration
{
    public long Revision { get; init; }
    public bool Enabled { get; init; }
    public string EndpointId { get; init; } = "openai";
    public string Model { get; init; } = "";
    public string Trigger { get; init; } = "manual";
    public bool RequireForApproval { get; init; }
    public bool ExternalTransmissionApproved { get; init; }
    public int TimeoutSeconds { get; init; } = 180;
    public int MaxConcurrentScans { get; init; } = 1;
    public int MaxRequestsPerScan { get; init; } = 16;
    public int MaxPromptBytes { get; init; } = 32768;
    public int MaxOutputTokens { get; init; } = 4096;
    public long MonthlyBudgetMicroUsd { get; init; } = 10_000_000;
    // Operator-supplied rates: local admission budgeting, not a provider billing guarantee.
    public decimal InputUsdPerMillionTokens { get; init; }
    public decimal OutputUsdPerMillionTokens { get; init; }
    public string OutputLanguage { get; init; } = "English";

    public void Validate(SkillAiDeployment deployment, bool hasSecret)
    {
        if (Revision < 0 || Trigger is not ("manual" or "suspicious" or "always") ||
            TimeoutSeconds is < 15 or > 600 || MaxConcurrentScans is < 1 or > 4 ||
            MaxRequestsPerScan is < 1 or > 64 || MaxPromptBytes is < 1024 or > 131072 ||
            MaxOutputTokens is < 64 or > 16384 || MonthlyBudgetMicroUsd is < 0 or > 1_000_000_000_000 ||
            InputUsdPerMillionTokens is < 0 or > 1000 || OutputUsdPerMillionTokens is < 0 or > 1000 ||
            OutputLanguage is not ("English" or "German") ||
            (RequireForApproval && (!Enabled || Trigger == "suspicious")))
            throw new InvalidDataException("Invalid AI policy or limits.");
        var endpoint = deployment.Resolve(EndpointId);
        if (Model is null || Model.Length > 160 || (Model.Length > 0 &&
            !Regex.IsMatch(Model, "^[A-Za-z0-9][A-Za-z0-9._:/@-]*$", RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Invalid model identifier.");
        if (!Enabled) return;
        if (Model.Length == 0 || (endpoint.External && (!hasSecret || !ExternalTransmissionApproved ||
            MonthlyBudgetMicroUsd == 0 || InputUsdPerMillionTokens == 0 || OutputUsdPerMillionTokens == 0)))
            throw new InvalidDataException("Model, credentials, transmission consent and nonzero external rates/budget are required.");
    }

    public long ReservationMicroUsd => checked((long)decimal.Ceiling(
        (MaxPromptBytes + 4096L) * InputUsdPerMillionTokens + MaxOutputTokens * OutputUsdPerMillionTokens));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SkillAiSettingsChange(SkillAiConfiguration Configuration, string? ApiKey,
    bool ClearApiKey, string Reason);

/// <summary>No SDK credential is ever sent to the scanner, browser, or queue.</summary>
public sealed class SkillAiSecrets(SkillAiDeployment deployment)
{
    private sealed record Ring(string Active, Dictionary<string, string> Keys);
    private sealed record Envelope(string KeyId, string Nonce, string Ciphertext, string Tag);
    private Ring ReadRing()
    {
        if (string.IsNullOrWhiteSpace(deployment.KeyRingFile))
            throw new InvalidDataException("An operator-managed AI keyring is required.");
        var ring = SkillAiJson.Read<Ring>(File.ReadAllText(deployment.KeyRingFile), 16384);
        if (ring.Keys is null || ring.Active is null || ring.Keys.Count is < 1 or > 8 || !ring.Keys.ContainsKey(ring.Active) ||
            ring.Keys.Any(k => !Regex.IsMatch(k.Key, "^[a-zA-Z0-9_-]{1,64}$", RegexOptions.CultureInvariant) ||
                Convert.FromBase64String(k.Value).Length != 32))
            throw new InvalidDataException("Invalid AI keyring.");
        return ring;
    }
    public string Protect(string value, string endpointId)
    {
        if (value.Length is < 1 or > 4096 || value.Any(char.IsControl))
            throw new InvalidDataException("Invalid API credential.");
        var ring = ReadRing();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var clear = Encoding.UTF8.GetBytes(value);
        var encrypted = new byte[clear.Length];
        var tag = new byte[16];
        var key = Convert.FromBase64String(ring.Keys[ring.Active]);
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, clear, encrypted, tag, Encoding.UTF8.GetBytes(SkillAiDeployment.Profile + "\n" + endpointId));
            return JsonSerializer.Serialize(new Envelope(ring.Active, Convert.ToBase64String(nonce),
                Convert.ToBase64String(encrypted), Convert.ToBase64String(tag)), SkillAiJson.Options);
        }
        finally { CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(key); }
    }
    public string Unprotect(string value, string endpointId)
    {
        var ring = ReadRing();
        var envelope = SkillAiJson.Read<Envelope>(value, 16384);
        if (!ring.Keys.TryGetValue(envelope.KeyId, out var encoded))
            throw new InvalidDataException("AI credential key is unavailable.");
        var key = Convert.FromBase64String(encoded);
        var cipher = Convert.FromBase64String(envelope.Ciphertext);
        var clear = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(envelope.Nonce), cipher,
                Convert.FromBase64String(envelope.Tag), clear,
                Encoding.UTF8.GetBytes(SkillAiDeployment.Profile + "\n" + endpointId));
            return Encoding.UTF8.GetString(clear);
        }
        finally { CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(key); }
    }
}

public static class SkillAiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, MaxDepth = 40 };
    public static T Read<T>(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) > maxBytes) throw new InvalidDataException("AI JSON exceeds the limit.");
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 40 });
        Check(doc.RootElement);
        return JsonSerializer.Deserialize<T>(text, Options) ?? throw new InvalidDataException("Empty AI JSON.");
    }
    private static void Check(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
            {
                if (!seen.Add(item.Name)) throw new InvalidDataException("Duplicate AI JSON field.");
                Check(item.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Check(item);
        else if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var n) || !double.IsFinite(n)))
            throw new InvalidDataException("Non-finite AI JSON number.");
    }
}
