using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packory;

public sealed class McpConnectorKeyringException(string message, Exception? cause = null)
    : Exception(message, cause);

/// <summary>
/// Per-connector AES-GCM envelope; keys are provided by the deployment, never the API.
/// Associated data binds a stored bearer credential to its exact connector ID and URL.
/// </summary>
public sealed class McpConnectorSecrets
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Ring(string Active, Dictionary<string, string> Keys);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record Envelope(string KeyId, string Nonce, string Ciphertext, string Tag);
    private readonly string? keyringFile;

    public McpConnectorSecrets() : this(Environment.GetEnvironmentVariable("PACKORY_MCP_CONNECTOR_KEYRING_FILE")) { }
    public McpConnectorSecrets(string? keyringFile) => this.keyringFile = keyringFile;

    private Ring ReadRing()
    {
        if (string.IsNullOrWhiteSpace(keyringFile) || !Path.IsPathFullyQualified(keyringFile))
            throw new McpConnectorKeyringException("MCP credential keyring is not configured.");
        // Bound the file before decoding/deserializing. File.ReadAllText would allocate
        // the whole keyring before SkillAiJson's size check could reject it.
        var bytes = new byte[16385];
        try
        {
            using var file = new FileStream(keyringFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = 0;
            while (length < bytes.Length)
            {
                var read = file.Read(bytes.AsSpan(length));
                if (read == 0) break;
                length += read;
            }
            if (length > 16384) throw new InvalidDataException("MCP keyring is too large.");
            var ring = SkillAiJson.Read<Ring>(new UTF8Encoding(false, true).GetString(bytes, 0, length), 16384);
            if (ring.Active is null || ring.Keys is null || ring.Keys.Count is < 1 or > 8 ||
                !ring.Keys.ContainsKey(ring.Active))
                throw new InvalidDataException("Invalid MCP keyring structure.");
            foreach (var (id, encoded) in ring.Keys)
            {
                if (id.Length is < 1 or > 64 || encoded is null ||
                    id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
                    throw new InvalidDataException("Invalid MCP keyring structure.");
                var key = Convert.FromBase64String(encoded);
                try
                {
                    if (key.Length != 32) throw new InvalidDataException("Invalid MCP key size.");
                }
                finally { CryptographicOperations.ZeroMemory(key); }
            }
            return ring;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or JsonException or FormatException or ArgumentException)
        {
            throw new McpConnectorKeyringException("MCP credential keyring is unavailable or invalid.", ex);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static byte[] Context(string id, string endpoint) =>
        Encoding.UTF8.GetBytes("packory.mcp.connector.v1\n" + id + "\n" + endpoint);

    public string Protect(string token, string id, string endpoint)
    {
        if (token is null || token.Length is < 1 or > 4096 || token.Any(c => c < '!' || c > '~'))
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
        byte[]? key = null;
        byte[]? clear = null;
        try
        {
            var envelope = SkillAiJson.Read<Envelope>(encrypted, 16384);
            if (string.IsNullOrEmpty(envelope.KeyId) || envelope.Nonce is null ||
                envelope.Ciphertext is null || envelope.Tag is null)
                throw new CryptographicException("MCP credential envelope is invalid.");
            if (!ring.Keys.TryGetValue(envelope.KeyId, out var encoded))
                throw new McpConnectorKeyringException("MCP credential encryption key is unavailable.");
            key = Convert.FromBase64String(encoded);
            var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var tag = Convert.FromBase64String(envelope.Tag);
            if (ciphertext.Length is < 1 or > 4096 || nonce.Length != 12 || tag.Length != 16)
                throw new CryptographicException("MCP credential envelope is invalid.");
            clear = new byte[ciphertext.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ciphertext, tag, clear, Context(id, endpoint));
            var value = new UTF8Encoding(false, true).GetString(clear);
            if (value.Any(c => c < '!' || c > '~'))
                throw new CryptographicException("MCP credential envelope is invalid.");
            return value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or ArgumentException)
        {
            throw new CryptographicException("MCP credential envelope is invalid.", ex);
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
        }
    }
}
