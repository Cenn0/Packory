using System.Text.Json;
using Packory;
using Xunit;

namespace Packory.Tests;

/// <summary>
/// Real native-provider process boundary: a separate worker executable connects
/// over an authenticated Unix socket to the same disposable TLS database used
/// by the direct-driver provider matrix. This test does not claim the host
/// process has Docker's namespace/cgroup restrictions; the separate Docker
/// worker smoke verifies those boundaries.
/// </summary>
public sealed class DatabaseConnectorIsolatedNativeProviderTests
{
    [DatabaseNativeProviderFact]
    public async Task Approved_query_roundtrips_through_a_distinct_worker_process()
    {
        if (!OperatingSystem.IsLinux() ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PACKORY_DB_WORKER_SOCKET")))
            throw new InvalidOperationException("A live isolated worker must be provisioned.");

        var definition = DatabaseConnectorNativeProviderTests.Definition();
        var marker = definition.Provider == "oracle" ? ":" : "@";
        var query = new DatabaseQueryDefinition(
            "isolation_query", "Isolated native provider fixture query",
            "SELECT id,name,amount FROM packory_connector_fixture WHERE id=" + marker + "id",
            [new DatabaseParameterDefinition("id", "int64", true, 20)], 2, 5);

        var executor = new IsolatedDatabaseConnectorExecutor();
        await executor.ProbeAsync(definition, DatabaseConnectorNativeProviderTests.Required("PASSWORD"),
            CancellationToken.None);
        using var arguments = JsonDocument.Parse("""{"id":1}""");
        var result = await executor.QueryAsync(definition with { Queries = [query] }, query,
            arguments.RootElement, DatabaseConnectorNativeProviderTests.Required("PASSWORD"),
            CancellationToken.None);

        var row = Assert.Single(result.Rows);
        var values = row.Values.ToArray();
        Assert.Equal("1", Assert.IsType<JsonElement>(values[0]).ToString());
        Assert.Equal("alpha", Assert.IsType<JsonElement>(values[1]).GetString());
        Assert.Equal(12.5m, decimal.Parse(Assert.IsType<JsonElement>(values[2]).ToString(),
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(result.Truncated);
        Assert.True(result.SourceDataUntrusted);
    }
}
