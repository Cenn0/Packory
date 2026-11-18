using System.Data.Common;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class DatabaseNativeProviderFactAttribute : FactAttribute
{
    public DatabaseNativeProviderFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PACKORY_TEST_DB_PROVIDER")))
            Skip = "Requires an explicit disposable TLS database fixture; native provider was not tested in this run.";
    }
}

/// <summary>
/// Executed against real disposable servers by the database-connectors workflow.
/// Every connection uses the production TLS, pinning, parameter and transaction path.
/// </summary>
public sealed class DatabaseConnectorNativeProviderTests
{
    internal static string Required(string name) => Environment.GetEnvironmentVariable("PACKORY_TEST_DB_" + name)
        ?? throw new InvalidOperationException("Missing disposable database fixture setting: " + name);

    internal static DatabaseConnectorDefinition Definition()
    {
        var provider = Required("PROVIDER");
        return new("provider-fixture", "Disposable database fixture", provider, Required("HOST"),
            int.Parse(Required("PORT")), Required("DATABASE"), Required("USER"),
            Required("TLS_SERVER_NAME"), provider == "oracle" ? Required("EXPECTED_DN") : null,
            true, [], 1);
    }

    internal static DatabaseConnectorExecutor Executor(DatabaseConnectorDefinition definition)
    {
        var address = IPAddress.Parse(Required("ADDRESS"));
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("The disposable database fixture must have an IPv4 address.");
        return new(new DatabaseConnectorPolicy(
            [$"{definition.Provider}://{definition.Host}:{definition.Port}"], [$"{address}/32"]));
    }

    private static DatabaseQueryDefinition Query(string sql, DatabaseParameterDefinition[]? parameters = null,
        int maxRows = 20) => new("fixture_query", "Disposable fixture query", sql, parameters ?? [], maxRows, 5);

    private static string Marker(DatabaseConnectorDefinition d) => d.Provider == "oracle" ? ":" : "@";
    private static string ScalarSql(DatabaseConnectorDefinition d, string expression) =>
        "SELECT " + expression + " AS value" + (d.Provider == "oracle" ? " FROM DUAL" : "");
    private static JsonDocument Empty() => JsonDocument.Parse("{}");

    [DatabaseNativeProviderFact]
    public async Task Native_probe_and_typed_parameter_query_use_real_tls_and_return_fixture_data()
    {
        var definition = Definition();
        var executor = Executor(definition);
        await executor.ProbeAsync(definition, Required("PASSWORD"), default);
        var query = Query("SELECT id,name,amount FROM packory_connector_fixture WHERE id=" + Marker(definition) + "id",
            [new("id", "int64", true, 100)]);
        using var arguments = JsonDocument.Parse("{\"id\":1}");
        var result = await executor.QueryAsync(definition, query, arguments.RootElement, Required("PASSWORD"), default);
        var row = Assert.Single(result.Rows);
        Assert.Equal(1m, Convert.ToDecimal(row.Values.ElementAt(0)));
        Assert.Equal("alpha", row.Values.ElementAt(1));
        Assert.Equal(12.5m, Convert.ToDecimal(row.Values.ElementAt(2)));
        Assert.False(result.Truncated);
        Assert.True(result.SourceDataUntrusted);
        using var empty = Empty();
        var precise = await executor.QueryAsync(definition,
            Query(ScalarSql(definition, "CAST('1234567890123456789' AS DECIMAL(20,0))")),
            empty.RootElement, Required("PASSWORD"), default);
        Assert.Equal("1234567890123456789", Assert.Single(precise.Rows).Values.Single());
    }

    [DatabaseNativeProviderFact]
    public async Task Native_driver_treats_injection_text_as_a_single_parameter_value()
    {
        var definition = Definition();
        var query = Query("SELECT id,name,amount FROM packory_connector_fixture WHERE name=" + Marker(definition) + "name",
            [new("name", "string", true, 100)]);
        using var arguments = JsonDocument.Parse("{\"name\":\"alpha' OR 1=1 --\"}");
        var result = await Executor(definition).QueryAsync(definition, query, arguments.RootElement,
            Required("PASSWORD"), default);
        Assert.Empty(result.Rows);
    }

    [DatabaseNativeProviderFact]
    public async Task Wrong_tls_certificate_identity_is_rejected_by_the_native_driver()
    {
        var definition = Definition();
        var wrong = definition.Provider == "oracle"
            ? definition with { OracleServerCertificateDn = "CN=wrong.example.invalid" }
            : definition with { TlsServerName = "wrong.example.invalid" };
        await Assert.ThrowsAnyAsync<Exception>(() => Executor(wrong).ProbeAsync(wrong, Required("PASSWORD"), default));
    }

    [DatabaseNativeProviderFact]
    public async Task Least_privilege_database_account_rejects_writes_independently_of_packory()
    {
        var definition = Definition();
        await using var connection = DatabaseConnectorExecutor.CreateConnection(definition,
            IPAddress.Parse(Required("ADDRESS")), Required("PASSWORD"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await connection.OpenAsync(timeout.Token);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "UPDATE packory_connector_fixture SET name='forbidden-change' WHERE id=1";
        await Assert.ThrowsAnyAsync<DbException>(() => command.ExecuteNonQueryAsync(timeout.Token));
        using var arguments = Empty();
        var result = await Executor(definition).QueryAsync(definition,
            Query("SELECT name FROM packory_connector_fixture WHERE id=1"), arguments.RootElement,
            Required("PASSWORD"), default);
        Assert.Equal("alpha", Assert.Single(result.Rows).Values.Single());
    }

    [DatabaseNativeProviderFact]
    public async Task Native_results_obey_row_and_cell_limits_without_returning_unbounded_values()
    {
        var definition = Definition();
        var executor = Executor(definition);
        using var arguments = Empty();
        var rows = await executor.QueryAsync(definition,
            Query("SELECT id,name,amount FROM packory_connector_fixture ORDER BY id", maxRows: 1),
            arguments.RootElement, Required("PASSWORD"), default);
        Assert.Single(rows.Rows);
        Assert.True(rows.Truncated);
        var expression = definition.Provider switch
        {
            "oracle" => "RPAD('x',4000,'x')",
            "sqlserver" => "REPLICATE(CAST('x' AS varchar(max)),4000)",
            _ => "REPEAT('x',4000)"
        };
        var text = await executor.QueryAsync(definition, Query(ScalarSql(definition, expression)),
            arguments.RootElement, Required("PASSWORD"), default);
        Assert.Equal(2048, Assert.IsType<string>(Assert.Single(text.Rows).Values.Single()).Length);
        Assert.True(text.CellsTruncated);
    }

    [DatabaseNativeProviderFact]
    public async Task Wrong_password_is_rejected_without_exposing_the_supplied_credential()
    {
        var definition = Definition();
        var wrong = "synthetic-invalid-password-" + Guid.NewGuid().ToString("N");
        var error = await Assert.ThrowsAnyAsync<Exception>(() => Executor(definition).ProbeAsync(definition, wrong, default));
        Assert.DoesNotContain(wrong, error.Message);
    }
}

public sealed class DatabaseUntrustedCertificateFactAttribute : FactAttribute
{
    public DatabaseUntrustedCertificateFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PACKORY_TEST_DB_PROVIDER")) ||
            Environment.GetEnvironmentVariable("PACKORY_TEST_DB_EXPECT_UNTRUSTED") != "true")
            Skip = "Requires a disposable TLS database while its test CA is deliberately untrusted.";
    }
}

public sealed class DatabaseConnectorUntrustedCertificateTests
{
    [DatabaseUntrustedCertificateFact]
    public async Task Native_driver_rejects_a_correct_hostname_signed_by_an_untrusted_ca()
    {
        var definition = DatabaseConnectorNativeProviderTests.Definition();
        await Assert.ThrowsAnyAsync<Exception>(() => DatabaseConnectorNativeProviderTests.Executor(definition)
            .ProbeAsync(definition, DatabaseConnectorNativeProviderTests.Required("PASSWORD"), default));
    }
}
