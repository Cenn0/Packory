using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class DatabaseWorkerProtocolTests
{
    private static readonly byte[] Key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly DatabaseQueryDefinition Query = new(
        "approved", "Approved report", "SELECT @value AS value",
        [new("value", "string", true, 20)], 2, 5);

    [Fact]
    public async Task Signed_request_roundtrips_without_using_a_tcp_listener()
    {
        using var args = JsonDocument.Parse("""{"value":"test"}""");
        var request = new DatabaseWorkerRequest(1, Guid.NewGuid().ToString("N"), "query",
            DatabaseConnectorExecutorTests.Definition("postgresql"), Query,
            args.RootElement, "synthetic-secret-only");
        await using var stream = new MemoryStream();
        await DatabaseWorkerProtocol.WriteAsync(stream, request, Key,
            DatabaseWorkerProtocol.MaximumRequestBytes, CancellationToken.None);
        Assert.Contains("synthetic-secret-only", Encoding.UTF8.GetString(stream.ToArray()));
        stream.Position = 0;
        var decoded = await DatabaseWorkerProtocol.ReadAsync<DatabaseWorkerRequest>(stream, Key,
            DatabaseWorkerProtocol.MaximumRequestBytes, CancellationToken.None);
        Assert.Equal(request.RequestId, decoded.RequestId);
        Assert.Equal("synthetic-secret-only", decoded.Password);
        Assert.Equal("test", decoded.Arguments.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Modified_body_fails_authentication_before_deserialization()
    {
        await using var stream = new MemoryStream();
        await DatabaseWorkerProtocol.WriteAsync(stream,
            new DatabaseWorkerResponse(1, Guid.NewGuid().ToString("N"), false, null),
            Key, DatabaseWorkerProtocol.MaximumResponseBytes, CancellationToken.None);
        var tampered = stream.ToArray();
        tampered[^2] ^= 0x01;
        await using var altered = new MemoryStream(tampered);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatabaseWorkerProtocol.ReadAsync<DatabaseWorkerResponse>(altered, Key,
                DatabaseWorkerProtocol.MaximumResponseBytes, CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_key_fails_closed()
    {
        await using var stream = new MemoryStream();
        await DatabaseWorkerProtocol.WriteAsync(stream,
            new DatabaseWorkerResponse(1, "0123456789abcdef0123456789abcdef", true, null),
            Key, DatabaseWorkerProtocol.MaximumResponseBytes, CancellationToken.None);
        stream.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatabaseWorkerProtocol.ReadAsync<DatabaseWorkerResponse>(stream,
                RandomNumberGenerator.GetBytes(32),
                DatabaseWorkerProtocol.MaximumResponseBytes, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(143361)]
    [InlineData(int.MaxValue)]
    public async Task Frame_length_is_rejected_before_allocating_a_payload(int length)
    {
        var header = new byte[36];
        BinaryPrimitives.WriteInt32BigEndian(header, length);
        await using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatabaseWorkerProtocol.ReadAsync<DatabaseWorkerResponse>(stream, Key,
                DatabaseWorkerProtocol.MaximumResponseBytes, CancellationToken.None));
    }

    [Fact]
    public async Task Truncated_frame_never_yields_partial_data()
    {
        await using var stream = new MemoryStream();
        await DatabaseWorkerProtocol.WriteAsync(stream,
            new DatabaseWorkerResponse(1, "0123456789abcdef0123456789abcdef", true, null),
            Key, DatabaseWorkerProtocol.MaximumResponseBytes, CancellationToken.None);
        var bytes = stream.ToArray();
        await using var truncated = new MemoryStream(bytes[..^3]);
        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            DatabaseWorkerProtocol.ReadAsync<DatabaseWorkerResponse>(truncated, Key,
                DatabaseWorkerProtocol.MaximumResponseBytes, CancellationToken.None));
    }

    [Fact]
    public async Task Serializer_rejects_undeclared_envelope_fields()
    {
        var wire = """{"version":1,"requestId":"a","succeeded":true,"result":null,"secret":"x"}""";
        var content = Encoding.UTF8.GetBytes(wire);
        var packet = new byte[36 + content.Length];
        BinaryPrimitives.WriteInt32BigEndian(packet, content.Length);
        HMACSHA256.HashData(Key, content).CopyTo(packet, 4);
        content.CopyTo(packet, 36);
        await using var stream = new MemoryStream(packet);
        await Assert.ThrowsAsync<JsonException>(() =>
            DatabaseWorkerProtocol.ReadAsync<DatabaseWorkerResponse>(stream, Key,
                DatabaseWorkerProtocol.MaximumResponseBytes, CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"columns":["value"],"rows":[{"value":{"nested":1}}],"truncated":false}""")]
    [InlineData("""{"columns":["value"],"rows":[{"value":[1,2,3]}],"truncated":false}""")]
    [InlineData("""{"columns":["value"],"rows":[{"extra":1}],"truncated":false}""")]
    [InlineData("""{"columns":["value","value"],"rows":[],"truncated":false}""")]
    [InlineData("""{"columns":["value"],"rows":[{"value":1},{"value":2},{"value":3}],"truncated":false}""")]
    [InlineData("""{"columns":["value"],"rows":[],"truncated":false,"sourceDataUntrusted":false}""")]
    [InlineData("""{"columns":["value"],"rows":[{"value":"synthetic-secret-only"}],"truncated":false}""")]
    public void Compromised_worker_cannot_serialize_unbounded_or_nested_results(string payload)
    {
        var result = JsonSerializer.Deserialize<DatabaseQueryResult>(payload,
            DatabaseWorkerProtocol.JsonOptions)!;
        Assert.Throws<InvalidDataException>(() =>
            DatabaseWorkerProtocol.ValidateResult(result, Query, "synthetic-secret-only"));
    }

    [Fact]
    public void Compromised_worker_cannot_exceed_cell_or_total_response_limit()
    {
        var text = new string('x', DatabaseConnectorExecutor.MaximumCellCharacters + 1);
        var payload = JsonSerializer.Serialize(new
        {
            columns = new[] { "value" },
            rows = new[] { new { value = text } },
            truncated = false
        });
        var result = JsonSerializer.Deserialize<DatabaseQueryResult>(payload,
            DatabaseWorkerProtocol.JsonOptions)!;
        Assert.Throws<InvalidDataException>(() =>
            DatabaseWorkerProtocol.ValidateResult(result, Query, "synthetic-secret-only"));

        var huge = new string('y', DatabaseConnectorExecutor.MaximumCellCharacters);
        var rows = Enumerable.Range(0, 2).Select(_ =>
            Enumerable.Range(0, DatabaseConnectorExecutor.MaximumColumns).ToDictionary(
                i => "col" + i, _ => huge)).ToArray();
        var large = JsonSerializer.Serialize(new
        {
            columns = Enumerable.Range(0, DatabaseConnectorExecutor.MaximumColumns)
                .Select(i => "col" + i).ToArray(),
            rows,
            truncated = false
        });
        var bigResult = JsonSerializer.Deserialize<DatabaseQueryResult>(large,
            DatabaseWorkerProtocol.JsonOptions)!;
        Assert.Throws<InvalidDataException>(() =>
            DatabaseWorkerProtocol.ValidateResult(bigResult, Query, "synthetic-secret-only"));
    }

    [Fact]
    public void Bounded_flat_untrusted_scalar_data_is_accepted()
    {
        var result = JsonSerializer.Deserialize<DatabaseQueryResult>(
            """{"columns":["value"],"rows":[{"value":14.5}],"truncated":false}""",
            DatabaseWorkerProtocol.JsonOptions)!;
        DatabaseWorkerProtocol.ValidateResult(result, Query, "synthetic-secret-only");
        Assert.True(result.SourceDataUntrusted);
    }

    [Fact]
    public void Isolated_executor_has_no_inprocess_driver_constructor_parameter()
    {
        Assert.Empty(typeof(IsolatedDatabaseConnectorExecutor).GetConstructors()
            .SelectMany(ctor => ctor.GetParameters()));
        Assert.True(typeof(IDatabaseConnectorExecutor).IsAssignableFrom(
            typeof(IsolatedDatabaseConnectorExecutor)));
    }
}
