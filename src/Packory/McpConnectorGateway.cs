using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Packory;

public sealed record McpConnectorToolSummary(string Name, string? Description, bool Allowed,
    JsonElement? InputSchema = null);

public interface IMcpConnectorRuntime
{
    Task<IReadOnlyList<McpConnectorToolSummary>> DiscoverAsync(McpConnectorRecord connector,
        CancellationToken cancellationToken);
    Task<JsonElement> CallAsync(McpConnectorRecord connector, string tool,
        JsonElement arguments, CancellationToken cancellationToken);
}

/// <summary>
/// Remote MCP client with deployment-owned egress policy, pinned DNS addresses and no
/// redirect, OS proxy, cookies, local process execution, or automatic tool execution.
/// </summary>
public sealed class McpConnectorGateway(McpConnectorPolicy policy, McpConnectorSecrets secrets)
    : IMcpConnectorRuntime
{
    // Shared by all instances: creating another connector does not create another quota.
    // Saturation fails immediately instead of retaining an unbounded queue of requests.
    private static readonly SemaphoreSlim Operations = new(4, 4);

    private static async Task<T> WithClientAsync<T>(McpConnectorRecord connector,
        McpConnectorPolicy policy, McpConnectorSecrets secrets,
        Func<McpClient, string?, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Operations.Wait(0))
            throw new ConnectorBusyException();
        try
        {
            // Revalidate at use time, before decrypting or contacting a destination.
            var target = policy.ValidateEndpoint(connector.Endpoint);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                MaxResponseHeadersLength = 16,
                ConnectTimeout = TimeSpan.FromSeconds(8),
                MaxConnectionsPerServer = 2,
                // Chain construction must not make a second, unrestricted AIA/CRL
                // HTTP request. Require the peer to present its complete trusted chain.
                SslOptions = new()
                {
                    CertificateChainPolicy = new X509ChainPolicy
                    {
                        DisableCertificateDownloads = true,
                        RevocationMode = X509RevocationMode.NoCheck,
                        ApplicationPolicy = { new Oid("1.3.6.1.5.5.7.3.1") }
                    }
                },
                ConnectCallback = async (context, ct) =>
                {
                    if (!string.Equals(context.DnsEndPoint.Host, target.IdnHost,
                            StringComparison.OrdinalIgnoreCase) ||
                        context.DnsEndPoint.Port != target.Port)
                        throw new IOException("Connector destination is not approved.");
                    var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                    if (addresses.Length == 0 || addresses.Any(a => !policy.AddressAllowed(target, a)))
                        throw new IOException("Connector DNS address violates egress policy.");
                    // Connect the vetted numeric address directly, while HttpClient retains
                    // the original hostname for SNI and normal TLS certificate validation.
                    var selected = addresses[0];
                    var socket = new Socket(selected.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(selected, context.DnsEndPoint.Port), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            using var guard = new McpConnectorResponseGuard(handler, target, deadline.Token);
            using var http = new HttpClient(guard) { Timeout = TimeSpan.FromSeconds(25) };
            var credential = connector.SecretCiphertext is null ? null
                : secrets.Unprotect(connector.SecretCiphertext, connector.Id, connector.Endpoint);
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            if (credential is not null) headers["Authorization"] = "Bearer " + credential;
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "Packory connector " + connector.Id,
                Endpoint = target,
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(8),
                EnableStandaloneGetStream = false,
                AdditionalHeaders = headers
            }, http);
            await using var client = await McpClient.CreateAsync(transport,
                cancellationToken: deadline.Token);
            return await action(client, credential, deadline.Token);
        }
        finally { Operations.Release(); }
    }

    public Task<IReadOnlyList<McpConnectorToolSummary>> DiscoverAsync(
        McpConnectorRecord connector, CancellationToken cancellationToken) =>
        WithClientAsync(connector, policy, secrets, async (client, credential, ct) =>
        {
            if (client.ServerCapabilities.Tools is null)
                return (IReadOnlyList<McpConnectorToolSummary>)[];
            var result = new List<McpConnectorToolSummary>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            var schemaBytes = 0;
            string? cursor = null;
            for (var pageNumber = 0; pageNumber < 4; pageNumber++)
            {
                var page = await client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, ct);
                foreach (var tool in page.Tools)
                {
                    if (result.Count >= 100) break;
                    if (!McpConnectorPolicy.ValidTool(tool.Name)) continue;
                    if (!names.Add(tool.Name))
                        throw new InvalidDataException("Remote MCP tool names must be unique.");
                    // A credential reflected into a tool identifier/schema is rejected rather
                    // than silently changing the contract the operator is asked to approve.
                    var schema = tool.InputSchema;
                    if (schema.ValueKind != JsonValueKind.Object ||
                        (credential is not null && (tool.Name.Contains(credential, StringComparison.Ordinal) ||
                            McpConnectorRedaction.ContainsSecret(schema, credential))))
                        throw new InvalidDataException("Remote MCP tool contract is invalid.");
                    var size = Encoding.UTF8.GetByteCount(schema.GetRawText());
                    if (size > 16384 || (schemaBytes += size) > 131072)
                        throw new InvalidDataException("Remote MCP tool schemas exceed the discovery limit.");
                    var description = McpConnectorRedaction.RedactText(tool.Description, credential);
                    result.Add(new McpConnectorToolSummary(tool.Name,
                        description is { Length: > 512 } ? description[..512] : description,
                        connector.AllowedTools.Contains(tool.Name, StringComparer.Ordinal), schema.Clone()));
                }
                if (result.Count >= 100 || page.NextCursor is null) break;
                if (page.NextCursor.Length > 512 || !cursors.Add(page.NextCursor))
                    throw new InvalidDataException("Remote MCP pagination cursor is invalid.");
                cursor = page.NextCursor;
            }
            return result;
        }, cancellationToken);

    public Task<JsonElement> CallAsync(McpConnectorRecord connector, string tool,
        JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!connector.Enabled || !McpConnectorPolicy.ValidTool(tool) ||
            !connector.AllowedTools.Contains(tool, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("Connector tool is disabled or not explicitly allowed.");
        if (arguments.ValueKind != JsonValueKind.Object ||
            Encoding.UTF8.GetByteCount(arguments.GetRawText()) > 65536)
            throw new InvalidDataException("Connector tool arguments require an object no larger than 64 KiB.");
        var args = arguments.EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal);
        return WithClientAsync(connector, policy, secrets, async (client, credential, ct) =>
        {
            var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
            var json = JsonSerializer.SerializeToElement(result, SkillAiJson.Options);
            if (Encoding.UTF8.GetByteCount(json.GetRawText()) > 131072)
                throw new InvalidDataException("Remote MCP tool response exceeds 128 KiB.");
            // Walk decoded JSON strings, including property names. Replacing text in the
            // serialized JSON misses quotes, backslashes and Unicode escape sequences.
            var redacted = McpConnectorRedaction.RedactJson(json, credential);
            if (Encoding.UTF8.GetByteCount(redacted.GetRawText()) > 131072)
                throw new InvalidDataException("Remote MCP tool response exceeds 128 KiB.");
            return redacted;
        }, cancellationToken);
    }
}

/// <summary>
/// Literal credential reflection defence. It cannot stop a malicious trusted endpoint
/// deliberately encoding or otherwise exfiltrating a credential it has received.
/// </summary>
public static class McpConnectorRedaction
{
    public static string? RedactText(string? value, string? secret) =>
        value is null || string.IsNullOrEmpty(secret) ? value
            : value.Replace(secret, "[redacted]", StringComparison.Ordinal);

    public static bool ContainsSecret(JsonElement value, string secret)
    {
        if (string.IsNullOrEmpty(secret)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!.Contains(secret, StringComparison.Ordinal),
            JsonValueKind.Array => value.EnumerateArray().Any(item => ContainsSecret(item, secret)),
            JsonValueKind.Object => value.EnumerateObject().Any(p =>
                p.Name.Contains(secret, StringComparison.Ordinal) || ContainsSecret(p.Value, secret)),
            _ => false
        };
    }

    public static JsonElement RedactJson(JsonElement value, string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return value.Clone();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, value, secret);
        using var parsed = JsonDocument.Parse(buffer.ToArray());
        return parsed.RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, string secret)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    var name = RedactText(property.Name, secret)!;
                    if (!names.Add(name))
                        throw new InvalidDataException("Remote MCP result has ambiguous property names.");
                    writer.WritePropertyName(name);
                    Write(writer, property.Value, secret);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) Write(writer, item, secret);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactText(value.GetString(), secret));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}

/// <summary>
/// Enforces wire budgets before the MCP SDK can deserialize JSON or accumulate SSE
/// events. Each instance belongs to one bounded connector operation, not one request.
/// </summary>
public sealed class McpConnectorResponseGuard : DelegatingHandler
{
    private readonly Uri target;
    private readonly CancellationToken operationCancellation;
    private readonly int responseLimit;
    private readonly long operationLimit;
    private long received;

    public McpConnectorResponseGuard(HttpMessageHandler inner, Uri target,
        CancellationToken operationCancellation = default, int responseLimit = 262144,
        int operationLimit = 1048576) : base(inner)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(responseLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(operationLimit, responseLimit);
        this.target = target;
        this.operationCancellation = operationCancellation;
        this.responseLimit = responseLimit;
        this.operationLimit = operationLimit;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri != target)
            throw new HttpRequestException("MCP request destination is not approved.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, operationCancellation);
        var response = await base.SendAsync(request, linked.Token);
        try
        {
            if ((int)response.StatusCode is >= 300 and <= 399)
                throw new HttpRequestException("MCP redirects are not permitted.");
            if (response.Content.Headers.ContentEncoding.Any(e =>
                    !string.Equals(e, "identity", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Compressed MCP responses are not permitted.");
            if (response.Content.Headers.ContentLength is { } length &&
                (length > responseLimit || length > Remaining))
                throw new InvalidDataException("MCP response exceeds the wire budget.");
            response.Content = new BoundedContent(response.Content, this);
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    private long Remaining => operationLimit - Interlocked.Read(ref received);

    private sealed class BoundedContent : HttpContent
    {
        private readonly HttpContent content;
        private readonly McpConnectorResponseGuard guard;
        public BoundedContent(HttpContent content, McpConnectorResponseGuard guard)
        {
            this.content = content;
            this.guard = guard;
            foreach (var header in content.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length)
        {
            length = content.Headers.ContentLength ?? 0;
            return content.Headers.ContentLength.HasValue;
        }
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            CreateContentReadStreamAsync(guard.operationCancellation);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken ct) =>
            new BoundedStream(await content.ReadAsStreamAsync(ct), guard);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var source = await CreateContentReadStreamAsync(guard.operationCancellation);
            await source.CopyToAsync(stream, guard.operationCancellation);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) content.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class BoundedStream : Stream
    {
        private readonly Stream inner;
        private readonly McpConnectorResponseGuard guard;
        private readonly CancellationTokenRegistration cancellation;
        private long count;
        public BoundedStream(Stream inner, McpConnectorResponseGuard guard)
        {
            this.inner = inner;
            this.guard = guard;
            cancellation = guard.operationCancellation.Register(static state => ((Stream)state!).Dispose(), inner);
        }
        private int Allowed(int requested)
        {
            guard.operationCancellation.ThrowIfCancellationRequested();
            var remaining = Math.Min(guard.responseLimit - count, guard.Remaining);
            if (remaining < 0) throw new InvalidDataException("MCP response exceeds the wire budget.");
            return (int)Math.Min(requested, remaining + 1);
        }
        private int Record(int read)
        {
            count += read;
            var total = Interlocked.Add(ref guard.received, read);
            if (count > guard.responseLimit || total > guard.operationLimit)
                throw new InvalidDataException("MCP response exceeds the wire budget.");
            return read;
        }
        public override int Read(byte[] buffer, int offset, int length) =>
            Record(inner.Read(buffer, offset, Allowed(length)));
        public override int Read(Span<byte> buffer) => Record(inner.Read(buffer[..Allowed(buffer.Length)]));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int length, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, length), ct).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, guard.operationCancellation);
            return Record(await inner.ReadAsync(buffer[..Allowed(buffer.Length)], linked.Token));
        }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int length) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { cancellation.Dispose(); inner.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await cancellation.DisposeAsync();
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }
    }
}
