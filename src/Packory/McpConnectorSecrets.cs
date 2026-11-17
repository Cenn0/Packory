using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Packory;

public sealed class McpConnectorKeyringException(string message, Exception? cause = null)
    : Exception(message, cause);

/// <summary>
/// Per-connector AES-GCM envelope; keys are provided by the deployment, never the API.
/// Associated data binds a stored bearer credential to its exact connector ID and URL.
/// </summary>
public sealed class McpConnectorSecrets
{
    private sealed record Ring(string Active, Dictionary<string, string> Keys);
    private sealed record Envelope(string KeyId, string Nonce, string Ciphertext, string Tag);
    private readonly string? keyringFile;

    public McpConnectorSecrets() : this(Environment.GetEnvironmentVariable("PACKORY_MCP_CONNECTOR_KEYRING_FILE")) { }
    public McpConnectorSecrets(string? keyringFile) => this.keyringFile = keyringFile;

    private Ring ReadRing()
    {
        if (string.IsNullOrWhiteSpace(keyringFile) || !Path.IsPathFullyQualified(keyringFile))
            throw new McpConnectorKeyringException("MCP credential keyring is not configured.");
        try
        {
            var ring = SkillAiJson.Read<Ring>(File.ReadAllText(keyringFile), 16384);
            if (ring.Active is null || ring.Keys is null || ring.Keys.Count is < 1 or > 8 ||
                !ring.Keys.ContainsKey(ring.Active) ||
                ring.Keys.Any(p => p.Key.Length is < 1 or > 64 ||
                    p.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and '-') ||
                    Convert.FromBase64String(p.Value).Length != 32))
                throw new InvalidDataException("Invalid MCP keyring structure.");
            return ring;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or FormatException or ArgumentException)
        {
            throw new McpConnectorKeyringException("MCP credential keyring is unavailable or invalid.", ex);
        }
    }

    private static byte[] Context(string id, string endpoint) =>
        Encoding.UTF8.GetBytes("packory.mcp.connector.v1\n" + id + "\n" + endpoint);

    public string Protect(string token, string id, string endpoint)
    {
        if (token.Length is < 1 or > 4096 || token.Any(c => c < '!' || c > '~'))
            throw new InvalidDataException("Invalid bearer credential.");
        var ring = ReadRing();
        var key = Convert.FromBase64String(ring.Keys[ring.Active]);
        var clear = Encoding.UTF8.GetBytes(token);
        var ciphertext = new byte[clear.Length];
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, clear, ciphertext, tag, Context(id, endpoint));
            return JsonSerializer.Serialize(new Envelope(ring.Active, Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag)), SkillAiJson.Options);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(clear);
        }
    }

    public string Unprotect(string encrypted, string id, string endpoint)
    {
        var ring = ReadRing();
        Envelope envelope;
        try { envelope = SkillAiJson.Read<Envelope>(encrypted, 16384); }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            throw new CryptographicException("MCP credential envelope is invalid.", e);
        }
        if (!ring.Keys.TryGetValue(envelope.KeyId, out var encoded))
            throw new McpConnectorKeyringException("MCP credential encryption key is unavailable.");
        var key = Convert.FromBase64String(encoded);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var clear = new byte[ciphertext.Length];
        try
        {
            if (clear.Length is < 1 or > 4096) throw new CryptographicException("Invalid MCP credential size.");
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(envelope.Nonce), ciphertext,
                Convert.FromBase64String(envelope.Tag), clear, Context(id, endpoint));
            return Encoding.UTF8.GetString(clear);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(clear);
        }
    }
}
