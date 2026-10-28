using System.Text.Json;
using Npgsql;
using Packory;
using Xunit;

namespace Packory.Tests;

/// <summary>
/// Executes against the disposable PostgreSQL service in the dedicated
/// postgres.yml CI job. A local run without PACKORY_TEST_POSTGRES does not
/// constitute a database interoperability pass.
/// </summary>
public sealed class McpConnectorPostgresTests
{
    [Fact]
    public async Task ParameterizedQueryUsesReadOnlyTransactionAndBoundedResults()
    {
        var connection = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var executor = new McpPostgresConnectorExecutor();
        var query = new McpManagedQuery
        {
            Name = "synthetic_orders",
            Description = "Synthetic IDs",
            Sql = "SELECT @id::bigint + n AS order_id FROM generate_series(0, 9) AS n",
            Parameters = new Dictionary<string, string> { ["id"] = "int64" },
            MaxRows = 2
        };
        McpConnectorPolicy.Validate(new McpManagedConnector
        {
            Id = "synthetic",
            Description = "Synthetic provider contract",
            AllowedSubjects = ["synthetic-sub"],
            Queries = [query]
        });
        var data = await executor.QueryAsync(connection, query, new Dictionary<string, string>
        {
            ["id"] = "42"
        }, CancellationToken.None);
        var json = JsonSerializer.SerializeToElement(data);
        var rows = json.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(42L, rows[0].GetProperty("order_id").GetInt64());
        Assert.Equal(43L, rows[1].GetProperty("order_id").GetInt64());
        Assert.True(json.GetProperty("truncated").GetBoolean());
        Assert.True(json.GetProperty("sourceDataUntrusted").GetBoolean());
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await executor.QueryAsync(connection, query, new Dictionary<string, string>
            {
                ["id"] = "42",
                ["extra"] = "injected"
            }, CancellationToken.None));
    }

    [Fact]
    public async Task ReadOnlyTransactionBlocksSequenceMutationThroughSelectFunction()
    {
        var connection = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var sequence = "packory_mcp_readonly_" + Guid.NewGuid().ToString("N")[..12];
        await using var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand("CREATE SEQUENCE public." + sequence, admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            var query = new McpManagedQuery
            {
                Name = "attempt_mutation",
                Description = "Negative synthetic mutation fixture",
                Sql = "SELECT nextval('public." + sequence + "') AS value",
                MaxRows = 1
            };
            var executor = new McpPostgresConnectorExecutor();
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                executor.QueryAsync(connection, query, new Dictionary<string, string>(),
                    CancellationToken.None));
            Assert.Equal(PostgresErrorCodes.ReadOnlySqlTransaction, exception.SqlState);
        }
        finally
        {
            await using var drop = new NpgsqlCommand("DROP SEQUENCE public." + sequence, admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ExternalProviderTextAndBinaryNeverBecomeUnlimitedResults()
    {
        var connection = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var executor = new McpPostgresConnectorExecutor();
        var query = new McpManagedQuery
        {
            Name = "oversized",
            Description = "Synthetic untrusted bytes",
            Sql = "SELECT repeat('x', 65000) AS text_value, decode('00ff', 'hex') AS binary_value",
            MaxRows = 1
        };
        var result = await executor.QueryAsync(connection, query,
            new Dictionary<string, string>(), CancellationToken.None);
        var row = JsonSerializer.SerializeToElement(result).GetProperty("rows")[0];
        Assert.True(row.GetProperty("text_value").GetString()!.Length <= 2049);
        Assert.Equal("[unsupported_type]", row.GetProperty("binary_value").GetString());
        Assert.DoesNotContain("00ff", JsonSerializer.Serialize(result));
    }
}
