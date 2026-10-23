using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Packory;

public interface ISkillAiTransport
{
    Task<string> SendAsync(SkillAiEndpoint endpoint, string body, string? credential, CancellationToken ct);
}

/// <summary>Only the trusted API process has egress or provider credentials.</summary>
public sealed class SkillAiTransport : ISkillAiTransport
{
    private static readonly System.Net.IPNetwork[] Forbidden = Networks(
        "0.0.0.0/8", "169.254.0.0/16", "224.0.0.0/4", "240.0.0.0/4", "100.64.0.0/10",
        "192.0.0.0/24", "192.0.2.0/24", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24",
        "::/128", "ff00::/8", "fe80::/10", "2001:db8::/32", "64:ff9b::/96", "64:ff9b:1::/48", "2002::/16");
    private static readonly System.Net.IPNetwork[] Private = Networks(
        "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7");
    private static System.Net.IPNetwork[] Networks(params string[] values) => values.Select(System.Net.IPNetwork.Parse).ToArray();

    public static bool AddressAllowed(SkillAiEndpoint endpoint, IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.ScopeIdIfIpv6() != 0 || Forbidden.Any(n => n.Contains(address))) return false;
        if (Private.Any(n => n.Contains(address)))
            return (endpoint.AllowedPrivateCidrs ?? []).Any(n => System.Net.IPNetwork.Parse(n).Contains(address));
        // A local-model profile must never silently exfiltrate to a public IP,
        // even if an operator configures a misleading HTTP hostname.
        if (endpoint.Provider == "ollama") return false;
        // IPv6 outside global unicast is not an Internet provider destination.
        return address.AddressFamily == AddressFamily.InterNetwork || System.Net.IPNetwork.Parse("2000::/3").Contains(address);
    }
    public async Task<string> SendAsync(SkillAiEndpoint endpoint, string body, string? credential, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10), MaxResponseHeadersLength = 16,
            ConnectCallback = async (context, token) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host, endpoint.Uri.Host, StringComparison.OrdinalIgnoreCase) ||
                    context.DnsEndPoint.Port != endpoint.Uri.Port)
                    throw new IOException("AI egress destination rejected.");
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                if (addresses.Length == 0 || addresses.Any(a => !AddressAllowed(endpoint, a)))
                    throw new IOException("AI egress address rejected.");
                // Connect the vetted numeric address: no second DNS lookup/rebinding window.
                var socket = new Socket(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var uri = new Uri(endpoint.Uri, endpoint.Provider == "anthropic" ? "v1/messages" : "chat/completions");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (endpoint.Provider == "anthropic")
        {
            request.Headers.Add("x-api-key", credential);
            request.Headers.Add("anthropic-version", "2023-06-01");
        }
        else if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("AI provider rejected the request.", null, response.StatusCode);
        if (response.Content.Headers.ContentLength > 1024 * 1024) throw new InvalidDataException("AI response exceeds limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = await BlobStore.ReadBoundedAsync(stream, 1024 * 1024, ct);
        var text = new UTF8Encoding(false, true).GetString(bytes);
        // A misconfigured or malicious gateway must not echo the credential into reports.
        return credential is null ? text : text.Replace(credential, "[credential redacted]", StringComparison.Ordinal);
    }
}
internal static class SkillAiAddressExtensions
{
    internal static long ScopeIdIfIpv6(this IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? address.ScopeId : 0;
}

public sealed class SkillAiGateway(SkillAiDeployment deployment, SkillAiSecrets secrets,
    SkillAiStore store, ISkillAiTransport transport)
{
    private static readonly HashSet<string> OpenAiFields = new(StringComparer.Ordinal)
    { "model", "messages", "max_tokens", "max_completion_tokens", "temperature", "top_p", "tools", "tool_choice", "response_format", "stream", "stop", "seed", "reasoning_effort", "parallel_tool_calls", "n", "store" };
    private static readonly HashSet<string> AnthropicFields = new(StringComparer.Ordinal)
    { "model", "messages", "max_tokens", "temperature", "top_p", "tools", "tool_choice", "stream", "system", "stop_sequences" };

    public static string ValidateRequest(string raw, SkillAiConfiguration cfg, SkillAiEndpoint endpoint)
    {
        var body = SkillAiJson.Read<JsonObject>(raw, cfg.MaxPromptBytes);
        var native = endpoint.Provider == "anthropic";
        if (body.Any(p => !(native ? AnthropicFields : OpenAiFields).Contains(p.Key)) ||
            body["messages"] is not JsonArray messages || messages.Count is < 1 or > 128 ||
            body["stream"]?.GetValue<bool>() == true)
            throw new InvalidDataException("Unsupported AI request.");
        foreach (var message in messages)
        {
            if (message is not JsonObject item || item["role"]?.GetValue<string>() is not ("system" or "developer" or "user" or "assistant") ||
                item.Any(p => p.Key is not ("role" or "content"))) throw new InvalidDataException("Unsupported AI message.");
            ValidateText(item["content"]);
        }
        if (body["system"] is not null) ValidateText(body["system"]);
        if (body["tools"] is JsonArray tools)
        {
            if (tools.Count > 8) throw new InvalidDataException("Too many structured schemas.");
            foreach (var tool in tools)
            {
                // Function schemas are output formatting only. No hosted tools or tool execution.
                if (tool is not JsonObject schema || (native
                    ? schema.Any(p => p.Key is not ("name" or "description" or "input_schema"))
                    : schema["type"]?.GetValue<string>() != "function" || schema.Any(p => p.Key is not ("type" or "function"))))
                    throw new InvalidDataException("Hosted tools are not allowed.");
            }
        }
        else if (body.ContainsKey("tools")) throw new InvalidDataException("Invalid structured schemas.");
        if (body["n"] is not null && body["n"]!.GetValue<int>() != 1) throw new InvalidDataException("Multiple completions are not allowed.");
        body["model"] = cfg.Model; // Never let scanned text or scanner defaults select a different model.
        body["stream"] = false;
        if (native || endpoint.Provider is "ollama" or "openai-compatible") { body.Remove("max_completion_tokens"); body["max_tokens"] = cfg.MaxOutputTokens; }
        else { body.Remove("max_tokens"); body["max_completion_tokens"] = cfg.MaxOutputTokens; }
        if (endpoint.Provider is "openai" or "azure-openai") body["store"] = false;
        else body.Remove("store");
        return body.ToJsonString(SkillAiJson.Options);
    }
    private static void ValidateText(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out _)) return;
        if (content is not JsonArray parts || parts.Count > 128) throw new InvalidDataException("Only text analysis is supported.");
        foreach (var part in parts)
            if (part is not JsonObject block || block["type"]?.GetValue<string>() != "text" ||
                block["text"] is not JsonValue text || !text.TryGetValue<string>(out _) ||
                block.Any(p => p.Key is not ("type" or "text"))) throw new InvalidDataException("Only text analysis is supported.");
    }
    public async Task<string> CompleteAsync(SkillAiStore.Job job, string requestId, string raw, CancellationToken ct)
    {
        var cfg = SkillAiJson.Read<SkillAiConfiguration>(job.Snapshot, 16384);
        var endpoint = deployment.Resolve(cfg.EndpointId);
        var body = ValidateRequest(raw, cfg, endpoint);
        if (!store.AdmitRequest(job.Id, requestId, out var error)) throw new InvalidOperationException(error);
        try
        {
            var secret = job.Ciphertext is null ? null : secrets.Unprotect(job.Ciphertext, cfg.EndpointId);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, (job.Deadline ?? 0) - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
            var response = await transport.SendAsync(endpoint, body, secret, deadline.Token);
            var json = SkillAiJson.Read<JsonObject>(response, 1024 * 1024);
            if (endpoint.Provider == "anthropic"
                ? json["content"] is not JsonArray
                : json["choices"] is not JsonArray choices || choices.Count != 1 || choices[0]?["message"] is not JsonObject)
                throw new InvalidDataException("Invalid provider completion.");
            long? input = null, output = null;
            if (json["usage"] is JsonObject usage)
            {
                input = Tokens(usage[nativeField(endpoint, "input_tokens", "prompt_tokens")]);
                output = Tokens(usage[nativeField(endpoint, "output_tokens", "completion_tokens")]);
            }
            store.RecordUsage(requestId, "COMPLETED", input, output);
            return response;
        }
        catch { store.RecordUsage(requestId, "FAILED"); throw; }
    }
    private static string nativeField(SkillAiEndpoint endpoint, string native, string other) => endpoint.Provider == "anthropic" ? native : other;
    private static long? Tokens(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var n) && n is >= 0 and <= 100_000_000 ? n : null;
}
