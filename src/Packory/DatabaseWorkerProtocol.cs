using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Packory;

// The main server and the containerized worker exchange only these bounded,
// authenticated, versioned messages. No Packory service or database store is
// exposed on the IPC channel.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DatabaseWorkerRequest(
    int Version, string RequestId, string Operation, DatabaseConnectorDefinition Definition,
    DatabaseQueryDefinition? Query, JsonElement Arguments, string Password);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DatabaseWorkerResponse(
    int Version, string RequestId, bool Succeeded, DatabaseQueryResult? Result);

internal static class DatabaseWorkerProtocol
{
    internal const int Version = 1;
    internal const int MaximumRequestBytes = 96 * 1024;
    internal const int MaximumResponseBytes = 140 * 1024;
    private const int HeaderBytes = 36;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static string SocketPath()
    {
        var path = Environment.GetEnvironmentVariable("PACKORY_DB_WORKER_SOCKET");
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(path) ||
            !Path.IsPathFullyQualified(path) || Encoding.UTF8.GetByteCount(path) > 100 ||
            !path.EndsWith(".sock", StringComparison.Ordinal) || path.IndexOf('\0') >= 0)
            throw new InvalidDataException("A Linux absolute database worker socket path is required.");
        return path;
    }

    // The operator provisions exactly 32 unpredictable bytes as a private file,
    // readable only by the two dedicated container identities. There is never
    // a compiled-in key, environment-variable secret, or automatic fallback.
    internal static byte[] LoadKey()
    {
        var path = Environment.GetEnvironmentVariable("PACKORY_DB_WORKER_IPC_KEY_FILE");
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(path) ||
            !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("A private database worker IPC key file is required.");
        var mode = File.GetUnixFileMode(path);
        if ((mode & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite |
                     UnixFileMode.OtherExecute | UnixFileMode.GroupWrite |
                     UnixFileMode.GroupExecute)) != 0)
            throw new InvalidDataException("Database worker IPC key permissions are too broad.");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != 32)
            throw new InvalidDataException("The database worker IPC key must contain exactly 32 bytes.");
        return bytes;
    }

    internal static async Task WriteAsync<T>(Stream stream, T message, byte[] key,
        int maximumBytes, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        if (body.Length is < 2 || body.Length > maximumBytes)
            throw new InvalidDataException("Database worker message exceeds its limit.");
        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), body.Length);
        var signature = HMACSHA256.HashData(key, body);
        signature.CopyTo(header, 4);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal static async Task<T> ReadAsync<T>(Stream stream, byte[] key,
        int maximumBytes, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderBytes];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0, 4));
        if (length is < 2 || length > maximumBytes)
            throw new InvalidDataException("Database worker frame length is invalid.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);
        var expected = HMACSHA256.HashData(key, body);
        if (!CryptographicOperations.FixedTimeEquals(header.AsSpan(4), expected))
            throw new InvalidDataException("Database worker message authentication failed.");
        return JsonSerializer.Deserialize<T>(body, JsonOptions) ??
            throw new InvalidDataException("Database worker message is invalid.");
    }

    // The trusted Packory process revalidates the data returned by a potentially
    // compromised worker. A worker must never be able to enlarge the result
    // budget, introduce nested JSON or present its data as trusted instructions.
    internal static void ValidateResult(DatabaseQueryResult result,
        DatabaseQueryDefinition query, string password)
    {
        if (!result.SourceDataUntrusted || result.Columns is null || result.Rows is null ||
            result.Columns.Length is < 1 or > DatabaseConnectorExecutor.MaximumColumns ||
            result.Rows.Count > query.MaxRows ||
            result.Columns.Any(column => string.IsNullOrEmpty(column) || column.Length > 128 ||
                column.Any(char.IsControl)) ||
            result.Columns.Distinct(StringComparer.Ordinal).Count() != result.Columns.Length)
            throw new InvalidDataException("The database worker response violates result limits.");
        var columns = result.Columns.ToHashSet(StringComparer.Ordinal);
        foreach (var row in result.Rows)
        {
            if (row is null || row.Count > columns.Count || row.Keys.Any(key => !columns.Contains(key)))
                throw new InvalidDataException("The database worker returned an invalid row.");
            foreach (var cell in row.Values)
            {
                if (cell is null) continue;
                if (cell is not JsonElement value ||
                    value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or
                        JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))
                    throw new InvalidDataException("A database worker cell is not a scalar.");
                if (value.ValueKind == JsonValueKind.String)
                {
                    var text = value.GetString()!;
                    if (text.Length > DatabaseConnectorExecutor.MaximumCellCharacters ||
                        (password.Length > 0 && text.Contains(password, StringComparison.Ordinal)))
                        throw new InvalidDataException("The database worker returned an invalid text cell.");
                }
                else if (value.ValueKind == JsonValueKind.Number && value.GetRawText().Length > 128)
                    throw new InvalidDataException("The database worker returned an oversized numeric cell.");
            }
        }
        if (JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length >
            DatabaseConnectorExecutor.MaximumOutputBytes)
            throw new InvalidDataException("The database worker result exceeds the output budget.");
    }
}
