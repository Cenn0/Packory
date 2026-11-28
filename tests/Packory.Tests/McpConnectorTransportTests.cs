using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Packory;
using Xunit;

namespace Packory.Tests;

[CollectionDefinition("McpConnectorTransport", DisableParallelization = true)]
public sealed class McpConnectorTransportCollection;

[Collection("McpConnectorTransport")]
public sealed class McpConnectorTransportTests
{
    private static readonly Uri Target = new("https://mcp.example.org/mcp");

    private sealed class ChunkedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public int BytesRead { get; private set; }
        public override bool CanSeek => false;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override int Read(Span<byte> buffer)
        {
            // MemoryStream.Read(Span) may dispatch through Read(byte[],...) for subclasses.
            var temporary = new byte[buffer.Length];
            var read = base.Read(temporary, 0, temporary.Length);
            temporary.AsSpan(0, read).CopyTo(buffer);
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }
    }

    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult(reply(request));
        }
    }

    private static HttpResponseMessage Chunked(ChunkedStream stream, string mediaType = "application/json")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }

    [Fact]
    public async Task UnknownLengthBodiesAreStoppedBeforeSdkCanConsumeBeyondLimit()
    {
        var body = new ChunkedStream(Encoding.UTF8.GetBytes(new string('x', 1000)));
        using var inner = new ReplyHandler(_ => Chunked(body));
        using var guard = new McpConnectorResponseGuard(inner, Target, responseLimit: 32, operationLimit: 64);
        using var client = new HttpClient(guard);
        using var response = await client.GetAsync(Target, HttpCompletionOption.ResponseHeadersRead);
        var error = await Record.ExceptionAsync(() => response.Content.ReadAsByteArrayAsync());
        Assert.NotNull(error);
        Assert.IsType<InvalidDataException>(error.GetBaseException());
        Assert.Equal(33, body.BytesRead);
    }

    [Fact]
    public async Task AdvertisedLengthAndCumulativeOperationBudgetAreEnforced()
    {
        var first = new ChunkedStream(new byte[40]);
        var second = new ChunkedStream(new byte[40]);
        var streams = new Queue<ChunkedStream>([first, second]);
        using var inner = new ReplyHandler(_ => Chunked(streams.Dequeue()));
        using var guard = new McpConnectorResponseGuard(inner, Target, responseLimit: 48, operationLimit: 64);
        using var client = new HttpClient(guard);
        using (var response = await client.GetAsync(Target))
            Assert.Equal(40, (await response.Content.ReadAsByteArrayAsync()).Length);
        var error = await Record.ExceptionAsync(() => client.GetAsync(Target));
        Assert.NotNull(error);
        Assert.IsType<InvalidDataException>(error.GetBaseException());
        Assert.Equal(25, second.BytesRead);

        var advertised = new ChunkedStream(new byte[1000]);
        using var advertisedInner = new ReplyHandler(_ =>
        {
            var result = Chunked(advertised);
            result.Content.Headers.ContentLength = 1000;
            return result;
        });
        using var advertisedGuard = new McpConnectorResponseGuard(advertisedInner, Target,
            responseLimit: 32, operationLimit: 64);
        using var advertisedClient = new HttpClient(advertisedGuard);
        await Assert.ThrowsAsync<InvalidDataException>(() => advertisedClient.GetAsync(Target));
        Assert.Equal(0, advertised.BytesRead);
    }

    [Fact]
    public async Task RedirectsCompressionAndDifferentEndpointsCannotEscapeTheGuard()
    {
        using var inner = new ReplyHandler(_ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
        {
            Headers = { Location = new Uri("https://other.example.org/steal") }
        });
        using var guard = new McpConnectorResponseGuard(inner, Target);
        using var client = new HttpClient(guard);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Target));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://other.example.org/steal"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://mcp.example.org/other"));
        Assert.Equal(1, inner.Requests);

        using var compressedInner = new ReplyHandler(_ =>
        {
            var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            result.Content.Headers.ContentEncoding.Add("gzip");
            return result;
        });
        using var compressedGuard = new McpConnectorResponseGuard(compressedInner, Target);
        using var compressedClient = new HttpClient(compressedGuard);
        await Assert.ThrowsAsync<InvalidDataException>(() => compressedClient.GetAsync(Target));
    }

    [Fact]
    public void JsonReflectionRedactionUsesDecodedStringsAndKeys()
    {
        const string secret = "synthetic-\\\"-<bearer>";
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["key-" + secret] = new object[] { "Bearer " + secret, new { value = secret } },
            ["ordinary"] = 42
        }));
        Assert.DoesNotContain(secret, json.RootElement.GetRawText());
        Assert.True(McpConnectorRedaction.ContainsSecret(json.RootElement, secret));
        var result = McpConnectorRedaction.RedactJson(json.RootElement, secret);
        Assert.False(McpConnectorRedaction.ContainsSecret(result, secret));
        Assert.Equal("Bearer [redacted]", result.GetProperty("key-[redacted]")[0].GetString());
        Assert.Equal(42, result.GetProperty("ordinary").GetInt32());
        Assert.Equal("description [redacted]", McpConnectorRedaction.RedactText("description " + secret, secret));
    }

    private sealed class McpReplies(bool oversizedSse) : HttpMessageHandler
    {
        public List<ChunkedStream> Bodies { get; } = [];
        public List<string?> Authorization { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authorization.Add(request.Headers.Authorization?.ToString());
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.OK);
            using var message = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var method = message.RootElement.GetProperty("method").GetString();
            if (!message.RootElement.TryGetProperty("id", out var id)) return new(HttpStatusCode.Accepted);
            object result = method switch
            {
                "server/discover" => new
                {
                    supportedVersions = new[] { "2026-07-28" },
                    capabilities = new { tools = new { } }, ttlMs = 0, cacheScope = "private"
                },
                "initialize" => new
                {
                    protocolVersion = message.RootElement.GetProperty("params").GetProperty("protocolVersion").GetString(),
                    capabilities = new { tools = new { } },
                    serverInfo = new { name = "synthetic-mcp", version = "1" }
                },
                "tools/list" => new
                {
                    tools = new[] { new { name = "query_readonly", description = "Synthetic tool",
                        inputSchema = new { type = "object", properties = new { record_id = new { type = "integer" } },
                            required = new[] { "record_id" }, additionalProperties = false } } }
                },
                _ => throw new InvalidOperationException("Unexpected synthetic MCP method: " + method)
            };
            var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.Clone(), result });
            var sse = method == "tools/list";
            var bytes = Encoding.UTF8.GetBytes(sse
                ? oversizedSse ? "data: " + new string('x', 20000) : "event: message\ndata: " + json + "\n\n"
                : json);
            var stream = new ChunkedStream(bytes);
            Bodies.Add(stream);
            return Chunked(stream, sse ? "text/event-stream" : "application/json");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfficialSdkStreamableHttpUsesBoundedJsonAndSse(bool oversizedSse)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var inner = new McpReplies(oversizedSse);
        using var guard = new McpConnectorResponseGuard(inner, Target, deadline.Token,
            responseLimit: 8192, operationLimit: 16384);
        using var http = new HttpClient(guard);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = Target, TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer synthetic-upstream-only" }
        }, http);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: deadline.Token);
        if (oversizedSse)
        {
            var error = await Record.ExceptionAsync(async () =>
                await client.ListToolsAsync(new ListToolsRequestParams(), deadline.Token));
            Assert.NotNull(error);
            Assert.True(inner.Bodies.Last().BytesRead <= 8193);
            Assert.False(deadline.IsCancellationRequested);
        }
        else
        {
            var result = await client.ListToolsAsync(new ListToolsRequestParams(), deadline.Token);
            Assert.Single(result.Tools);
            Assert.Equal("integer", result.Tools[0].InputSchema.GetProperty("properties")
                .GetProperty("record_id").GetProperty("type").GetString());
        }
        Assert.All(inner.Authorization, value => Assert.Equal("Bearer synthetic-upstream-only", value));
    }

    [Fact]
    public async Task SharedGatewayConcurrencyHasNoWaitingQueueAndReleasesOnCancellation()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var endpoint = $"https://127.0.0.1:{port}/mcp";
        var policy = new McpConnectorPolicy([$"https://127.0.0.1:{port}"], ["127.0.0.1/32"]);
        var record = new McpConnectorRecord("synthetic-busy", "Synthetic busy", endpoint, true,
            [], 1, 0, null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var sockets = new List<TcpClient>();
        // Keep the four TLS handshakes pending so all operation slots are occupied.
        var requests = Enumerable.Range(0, 4).Select(_ => new McpConnectorGateway(policy,
            new McpConnectorSecrets(null)).DiscoverAsync(record, deadline.Token)).ToArray();
        try
        {
            for (var i = 0; i < 4; i++) sockets.Add(await listener.AcceptTcpClientAsync(deadline.Token));
            var extraInstance = new McpConnectorGateway(policy, new McpConnectorSecrets(null));
            await Assert.ThrowsAsync<ConnectorBusyException>(() =>
                extraInstance.DiscoverAsync(record, deadline.Token));
            Assert.False(listener.Pending());
        }
        finally
        {
            deadline.Cancel();
            foreach (var socket in sockets) socket.Dispose();
            try { await Task.WhenAll(requests); }
            catch (Exception e) when (e is OperationCanceledException or IOException or HttpRequestException) { }
        }
        // Admission succeeds again; this request reaches policy validation and is
        // rejected for its origin, rather than retaining a leaked concurrency slot.
        await Assert.ThrowsAsync<InvalidDataException>(() => new McpConnectorGateway(policy,
            new McpConnectorSecrets(null)).DiscoverAsync(record with
            { Endpoint = "https://unapproved.example.org/mcp" }, CancellationToken.None));
    }

    [Fact]
    public async Task GatewayRejectsUntrustedTlsBeforeSendingStoredBearer()
    {
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=synthetic-mcp", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        certificateRequest.CertificateExtensions.Add(names.Build());
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var endpoint = $"https://127.0.0.1:{port}/mcp";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var receivedApplicationBytes = 0;
        var server = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var tls = new SslStream(accepted.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, deadline.Token);
                receivedApplicationBytes = await tls.ReadAsync(new byte[1024], deadline.Token);
            }
            catch (Exception e) when (e is AuthenticationException or IOException) { }
        }, deadline.Token);
        var directory = Path.Combine(Path.GetTempPath(), "packory-mcp-tls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "ring.json");
            File.WriteAllText(file, JsonSerializer.Serialize(new
            {
                active = "test", keys = new Dictionary<string, string>
                { ["test"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
            }));
            var secrets = new McpConnectorSecrets(file);
            var record = new McpConnectorRecord("synthetic-tls", "Synthetic TLS", endpoint, true,
                ["query_readonly"], 1, 0, secrets.Protect("synthetic-never-on-wire", "synthetic-tls", endpoint));
            var gateway = new McpConnectorGateway(new McpConnectorPolicy(
                [$"https://127.0.0.1:{port}"], ["127.0.0.1/32"]), secrets);
            var error = await Record.ExceptionAsync(() => gateway.DiscoverAsync(record, deadline.Token));
            Assert.NotNull(error);
            Assert.False(deadline.IsCancellationRequested);
            await server;
            Assert.Equal(0, receivedApplicationBytes);
        }
        finally { Directory.Delete(directory, true); }
    }
}
