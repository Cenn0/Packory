using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;

namespace Packory;

public sealed record McpConnectorToolSummary(string Name, string? Description, bool Allowed);

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
    private static async Task<T> WithClientAsync<T>(McpConnectorRecord connector,
        McpConnectorPolicy policy, McpConnectorSecrets secrets,
        Func<McpClient, string?, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        // Validate again at use time. An operator changing egress policy must revoke
        // previously persisted destinations without requiring a database migration.
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
            MaxConnectionsPerServer = 4,
            ConnectCallback = async (context, ct) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host, target.Host,
                        StringComparison.OrdinalIgnoreCase) ||
                    context.DnsEndPoint.Port != target.Port)
                    throw new IOException("Connector destination is not approved.");
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                if (addresses.Length == 0 || addresses.Any(a => !policy.AddressAllowed(target, a)))
                    throw new IOException("Connector DNS address violates egress policy.");
                // Vetted numeric IP is connected directly. No DNS rebind between validation and connect.
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
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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

    public Task<IReadOnlyList<McpConnectorToolSummary>> DiscoverAsync(
        McpConnectorRecord connector, CancellationToken cancellationToken) =>
        WithClientAsync(connector, policy, secrets, async (client, _, ct) =>
        {
            if (client.ServerCapabilities.Tools is null)
                return (IReadOnlyList<McpConnectorToolSummary>)[];
            // ListToolsAsync aggregates pages; a hostile remote could return unbounded pages.
            // Individual response guards and deadline in the transport bound the request,
            // while only the first 100 names are returned to callers.
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            return tools.Where(t => McpConnectorPolicy.ValidTool(t.Name))
                .Take(100)
                .Select(t => new McpConnectorToolSummary(
                    t.Name,
                    t.Description is { Length: > 512 } ? t.Description[..512] : t.Description,
                    connector.AllowedTools.Contains(t.Name, StringComparer.Ordinal)))
                .ToArray();
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
            // Remote advertised tool metadata is untrusted; only locally approved names are callable.
            var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
            var json = JsonSerializer.Serialize(result, SkillAiJson.Options);
            if (Encoding.UTF8.GetByteCount(json) > 131072)
                throw new InvalidDataException("Remote MCP tool response exceeds 128 KiB.");
            if (credential is not null)
                json = json.Replace(credential, "[redacted]", StringComparison.Ordinal);
            using var parsed = JsonDocument.Parse(json);
            return parsed.RootElement.Clone();
        }, cancellationToken);
    }
}
