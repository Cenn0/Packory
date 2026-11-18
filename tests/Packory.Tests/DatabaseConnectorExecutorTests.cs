using System.Collections;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class DatabaseConnectorExecutorTests
{
    private const string Password = "synthetic-password-only";
    internal static DatabaseConnectorDefinition Definition(string provider) => new(
        "synthetic-db", "Synthetic database", provider, "db.example.invalid", 5432,
        "packory_fixture", "packory_reader", "db.example.invalid",
        provider == "oracle" ? "CN=db.example.invalid" : null, true, [], 1);

    internal static DatabaseQueryDefinition Query(string sql = "SELECT @value AS value",
        DatabaseParameterDefinition[]? parameters = null, int rows = 10) =>
        new("approved_query", "Synthetic approved query", sql,
            parameters ?? [new("value", "string", true, 100)], rows, 5);

    [Theory]
    [InlineData("postgresql")]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("sqlserver")]
    [InlineData("oracle")]
    public void Native_connections_pin_numeric_address_and_require_authenticated_tls(string provider)
    {
        using var connection = DatabaseConnectorExecutor.CreateConnection(Definition(provider),
            IPAddress.Parse("203.0.113.12"), Password);
        Assert.DoesNotContain("db.example.invalid:5432", connection.ConnectionString);
        switch (connection)
        {
            case NpgsqlConnection pg:
                var po = new NpgsqlConnectionStringBuilder(pg.ConnectionString);
                Assert.Equal("203.0.113.12", po.Host);
                Assert.Equal(SslMode.VerifyFull, po.SslMode);
                Assert.Equal(GssEncryptionMode.Disable, po.GssEncryptionMode);
                Assert.False(po.Pooling);
                Assert.False(po.Enlist);
                var ssl = new SslClientAuthenticationOptions();
                pg.SslClientAuthenticationOptionsCallback!(ssl);
                Assert.Equal("db.example.invalid", ssl.TargetHost);
                Assert.Null(ssl.RemoteCertificateValidationCallback);
                Assert.True(ssl.CertificateChainPolicy!.DisableCertificateDownloads);
                break;
            case MySqlConnection mysql:
                var mo = new MySqlConnectionStringBuilder(mysql.ConnectionString);
                Assert.Equal("203.0.113.12", mo.Server);
                Assert.Equal(MySqlSslMode.Required, mo.SslMode);
                Assert.NotNull(mysql.RemoteCertificateValidationCallback);
                Assert.False(mo.Pooling);
                Assert.False(mo.AllowLoadLocalInfile);
                Assert.False(mo.AllowPublicKeyRetrieval);
                Assert.False(mo.AllowUserVariables);
                Assert.Equal(MySqlServerRedirectionMode.Disabled, mo.ServerRedirectionMode);
                break;
            case SqlConnection sql:
                var so = new SqlConnectionStringBuilder(sql.ConnectionString);
                Assert.Equal("tcp:203.0.113.12,5432", so.DataSource);
                Assert.Equal("db.example.invalid", so.HostNameInCertificate);
                Assert.Equal(SqlConnectionEncryptOption.Mandatory, so.Encrypt);
                Assert.False(so.TrustServerCertificate);
                Assert.False(so.IntegratedSecurity);
                Assert.False(so.Pooling);
                Assert.Equal(0, so.ConnectRetryCount);
                Assert.Equal(ApplicationIntent.ReadWrite, so.ApplicationIntent);
                break;
            case OracleConnection oracle:
                Assert.Contains("(PROTOCOL=TCPS)(HOST=203.0.113.12)(PORT=5432)", oracle.DataSource);
                Assert.Contains("(RETRY_COUNT=0)", oracle.DataSource);
                Assert.True(oracle.SSLServerDNMatch);
                Assert.Equal("CN=db.example.invalid", oracle.SSLServerCertDN);
                Assert.False(oracle.AutoProxy);
                Assert.Equal("system", oracle.WalletLocation);
                Assert.False(new OracleConnectionStringBuilder(oracle.ConnectionString).Pooling);
                break;
            default: Assert.Fail("Unsupported provider connection."); break;
        }
    }

    [Fact]
    public void MySql_certificate_override_checks_chain_errors_and_original_hostname()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=db.example.invalid", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("db.example.invalid");
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
        using var chain = new X509Chain();
        Assert.True(DatabaseConnectorExecutor.ValidateMySqlCertificate("db.example.invalid",
            certificate, chain, SslPolicyErrors.RemoteCertificateNameMismatch));
        Assert.False(DatabaseConnectorExecutor.ValidateMySqlCertificate("other.example.invalid",
            certificate, chain, SslPolicyErrors.None));
        Assert.False(DatabaseConnectorExecutor.ValidateMySqlCertificate("db.example.invalid",
            certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(DatabaseConnectorExecutor.ValidateMySqlCertificate("db.example.invalid",
            certificate, null, SslPolicyErrors.None));
        Assert.False(DatabaseConnectorExecutor.ValidateMySqlCertificate("db.example.invalid",
            null, chain, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("sqlserver")]
    [InlineData("oracle")]
    public void Provider_parameters_preserve_values_without_interpolating_sql(string provider)
    {
        using var connection = DatabaseConnectorExecutor.CreateConnection(Definition(provider),
            IPAddress.Parse("203.0.113.12"), Password);
        var marker = provider == "oracle" ? ":" : "@";
        var query = Query("SELECT " + marker + "value AS value" + (provider == "oracle" ? " FROM DUAL" : ""));
        const string injection = "x'; DROP TABLE forbidden; --";
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { value = injection }));
        var parameters = DatabaseConnectorExecutor.BindArguments(query.Parameters, doc.RootElement);
        using var command = DatabaseConnectorExecutor.CreateCommand(connection, null, query, parameters);
        Assert.Equal(query.Sql, command.CommandText);
        Assert.DoesNotContain(injection, command.CommandText);
        Assert.Single(command.Parameters);
        Assert.Equal(injection, command.Parameters[0].Value);
        Assert.Equal(ParameterDirection.Input, command.Parameters[0].Direction);
        if (command is OracleCommand oracle) Assert.True(oracle.BindByName);
    }

    [Theory]
    [InlineData("postgresql")]
    [InlineData("mysql")]
    [InlineData("mariadb")]
    [InlineData("sqlserver")]
    [InlineData("oracle")]
    public void Every_declared_scalar_type_and_optional_null_maps_to_native_parameter(string provider)
    {
        DatabaseParameterDefinition[] parameters =
        [new("text", "string", true, 100), new("whole", "int64", true, 100),
            new("fraction", "decimal", true, 100), new("flag", "bool", true, 100),
            new("id", "uuid", true, 100), new("day", "date", true, 100),
            new("instant", "datetime", true, 100), new("optional", "uuid", false, 100)];
        using var arguments = JsonDocument.Parse("""
            {"text":"plain","whole":9223372036854775807,"fraction":12.50,"flag":true,
             "id":"00000000-0000-0000-0000-000000000001","day":"2026-01-02",
             "instant":"2026-01-02T03:04:05Z"}
            """);
        using var connection = DatabaseConnectorExecutor.CreateConnection(Definition(provider),
            IPAddress.Parse("203.0.113.12"), Password);
        var bound = DatabaseConnectorExecutor.BindArguments(parameters, arguments.RootElement);
        using var command = DatabaseConnectorExecutor.CreateCommand(connection, null,
            Query(parameters: parameters), bound);
        Assert.Equal(parameters.Length, command.Parameters.Count);
        Assert.Equal(long.MaxValue, command.Parameters[1].Value);
        Assert.Equal(12.50m, command.Parameters[2].Value);
        Assert.Equal(DBNull.Value, command.Parameters[7].Value);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"value\":null}")]
    [InlineData("{\"value\":1}")]
    [InlineData("{\"value\":\"ok\",\"extra\":\"bad\"}")]
    [InlineData("{\"value\":\"a\",\"value\":\"b\"}")]
    [InlineData("[]")]
    public void Argument_contract_rejects_missing_unknown_duplicate_and_wrong_typed_values(string json)
    {
        using var arguments = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() =>
            DatabaseConnectorExecutor.BindArguments(Query().Parameters, arguments.RootElement));
    }

    [Theory]
    [InlineData("0.123456789012345678901234567890123")]
    [InlineData("-0.123456789012345678901234567890123")]
    [InlineData("1e-29")]
    [InlineData("-1e-29")]
    [InlineData("79228162514264337593543950335.1")]
    [InlineData("-79228162514264337593543950335.1")]
    [InlineData("0e2147483648")]
    [InlineData("0e-2147483649")]
    public void Decimal_arguments_reject_rounding_underflow_and_unbounded_exponents(string raw)
    {
        using var arguments = JsonDocument.Parse("{\"value\":" + raw + "}");
        Assert.Throws<InvalidDataException>(() => DatabaseConnectorExecutor.BindArguments(
            [new("value", "decimal", true, 100)], arguments.RootElement));
    }

    [Theory]
    [InlineData("1e-28", "0.0000000000000000000000000001")]
    [InlineData("-1e-28", "-0.0000000000000000000000000001")]
    [InlineData("7.9228162514264337593543950335e28", "79228162514264337593543950335")]
    [InlineData("-7.9228162514264337593543950335e28", "-79228162514264337593543950335")]
    [InlineData("1.0000000000000000000000000000000", "1")]
    [InlineData("-12.50000E+2", "-1250")]
    [InlineData("123450000e-4", "12345")]
    [InlineData("0e2147483647", "0")]
    [InlineData("-0e-2147483648", "0")]
    public void Decimal_arguments_preserve_exact_values_across_notation_and_scale(string raw, string expected)
    {
        using var arguments = JsonDocument.Parse("{\"value\":" + raw + "}");
        var bound = DatabaseConnectorExecutor.BindArguments(
            [new("value", "decimal", true, 100)], arguments.RootElement);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            Assert.IsType<decimal>(Assert.Single(bound).Value));
    }

    [Fact]
    public void Decimal_literal_length_is_bounded_before_coefficient_parsing()
    {
        using var accepted = JsonDocument.Parse("{\"value\":1." + new string('0', 126) + "}");
        Assert.Equal(1m, Assert.Single(DatabaseConnectorExecutor.BindArguments(
            [new("value", "decimal", true, 100)], accepted.RootElement)).Value);
        using var rejected = JsonDocument.Parse("{\"value\":1." + new string('0', 127) + "}");
        Assert.Throws<InvalidDataException>(() => DatabaseConnectorExecutor.BindArguments(
            [new("value", "decimal", true, 100)], rejected.RootElement));
    }

    [Fact]
    public async Task Huge_text_and_binary_are_not_materialized_and_rows_are_bounded()
    {
        using var reader = new GuardedReader(["large", "binary"], [typeof(string), typeof(byte[])],
            [[new HugeText(10_000_000), new byte[1]], [new HugeText(2), new byte[1]]]);
        var result = await DatabaseConnectorExecutor.ReadResultAsync(reader, 1, Password, default);
        Assert.Single(result.Rows);
        Assert.Equal(2048, Assert.IsType<string>(result.Rows[0]["large"]).Length);
        Assert.Equal("[unsupported_type]", result.Rows[0]["binary"]);
        Assert.True(result.Truncated);
        Assert.True(result.CellsTruncated);
        Assert.True(reader.LargestRead <= 2049);
        Assert.Equal(0, reader.UnboundedValueReads);
    }

    [Fact]
    public async Task Output_budget_and_duplicate_columns_fail_closed()
    {
        using var reader = new GuardedReader(["value"], [typeof(string)],
            Enumerable.Range(0, 100).Select(_ => new object?[] { new HugeText(2048) }).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatabaseConnectorExecutor.ReadResultAsync(reader, 100, Password, default));
        using var duplicate = new GuardedReader(["value", "value"], [typeof(int), typeof(int)], [[1, 2]]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatabaseConnectorExecutor.ReadResultAsync(duplicate, 1, Password, default));
        using var excessive = new GuardedReader(Enumerable.Range(0, 33).Select(i => "v" + i).ToArray(),
            Enumerable.Repeat(typeof(int), 33).ToArray(), []);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DatabaseConnectorExecutor.ReadResultAsync(excessive, 1, Password, default));
    }

    [Fact]
    public async Task Raw_password_is_removed_from_columns_and_text_before_json_encoding()
    {
        const string escapedPassword = "secret\"with\\escapes";
        using var reader = new GuardedReader([escapedPassword], [typeof(string)],
            [["prefix-" + escapedPassword + "-suffix"]]);
        var result = await DatabaseConnectorExecutor.ReadResultAsync(reader, 1, escapedPassword, default);
        Assert.Equal("[redacted]", result.Columns[0]);
        Assert.Equal("prefix-[redacted]-suffix", result.Rows[0]["[redacted]"]);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Credential_reflection_is_redacted_when_the_cell_limit_splits_the_password()
    {
        var password = new string('z', 3000);
        using var reader = new GuardedReader(["value"], [typeof(string)], [["prefix-" + password]]);
        var result = await DatabaseConnectorExecutor.ReadResultAsync(reader, 1, password, default);
        Assert.Equal("prefix-[redacted]", result.Rows[0]["value"]);
        Assert.True(result.CellsTruncated);
    }

    [Fact]
    public async Task Short_credential_crossing_the_cell_limit_does_not_leak_a_partial_suffix()
    {
        const string password = "synthetic-secret-crossing-cell-limit";
        var prefix = new string('x', 2040);
        using var reader = new GuardedReader(["value"], [typeof(string)], [[prefix + password]]);
        var result = await DatabaseConnectorExecutor.ReadResultAsync(reader, 1, password, default);
        Assert.Equal(prefix + "[redacted]", result.Rows[0]["value"]);
        Assert.True(result.CellsTruncated);
    }

    [Fact]
    public async Task Decimal_and_int64_results_preserve_precision_as_invariant_strings()
    {
        using var reader = new GuardedReader(["amount", "signed", "unsigned", "ordinary"],
            [typeof(decimal), typeof(long), typeof(ulong), typeof(int)],
            [[1234567890123456789.0123456789m, long.MaxValue, ulong.MaxValue, 123]]);
        var result = await DatabaseConnectorExecutor.ReadResultAsync(reader, 1, Password, default);
        var row = Assert.Single(result.Rows);
        Assert.Equal("1234567890123456789.0123456789", row["amount"]);
        Assert.Equal("9223372036854775807", row["signed"]);
        Assert.Equal("18446744073709551615", row["unsigned"]);
        Assert.Equal(123, row["ordinary"]);
    }

    [Fact]
    public void Oracle_exact_decimal_serialization_uses_invariant_punctuation()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            var number = new Oracle.ManagedDataAccess.Types.OracleDecimal(1234.5m);
            Assert.Equal("1234.5", number.ToString(false));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task Cancellation_is_observed_before_resolution_or_external_connection()
    {
        var executor = new DatabaseConnectorExecutor(new DatabaseConnectorPolicy([]));
        using var arguments = JsonDocument.Parse("{\"value\":\"plain\"}");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.QueryAsync(
            Definition("postgresql"), Query(), arguments.RootElement, Password, new CancellationToken(true)));
    }

    private sealed record HugeText(int Length);
    private sealed class GuardedReader(string[] columns, Type[] types, object?[][] rows) : DbDataReader
    {
        private int position = -1;
        public int LargestRead { get; private set; }
        public int UnboundedValueReads { get; private set; }
        private object? Value(int ordinal) => rows[position][ordinal];
        public override int FieldCount => columns.Length;
        public override string GetName(int ordinal) => columns[ordinal];
        public override Type GetFieldType(int ordinal) => types[ordinal];
        public override bool Read() => ++position < rows.Length;
        public override bool NextResult() => false;
        public override bool IsDBNull(int ordinal) => Value(ordinal) is null or DBNull;
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        {
            LargestRead = Math.Max(LargestRead, length);
            if (Value(ordinal) is HugeText huge)
            {
                var count = (int)Math.Min(length, huge.Length - dataOffset);
                if (buffer is not null) Array.Fill(buffer, 'x', bufferOffset, count);
                return count;
            }
            var text = (string)Value(ordinal)!;
            var available = (int)Math.Min(length, text.Length - dataOffset);
            text.AsSpan((int)dataOffset, available).CopyTo(buffer.AsSpan(bufferOffset));
            return available;
        }
        public override object GetValue(int ordinal)
        {
            if (Value(ordinal) is HugeText or string or byte[])
            {
                UnboundedValueReads++;
                throw new InvalidOperationException("Unbounded value materialization is forbidden.");
            }
            return Value(ordinal) ?? DBNull.Value;
        }
        public override int GetValues(object[] values) => throw new NotSupportedException();
        public override string GetString(int ordinal) => throw new NotSupportedException();
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override bool HasRows => rows.Length != 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => -1;
        public override int Depth => 0;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override int GetOrdinal(string name) => Array.IndexOf(columns, name);
        public override string GetDataTypeName(int ordinal) => types[ordinal].Name;
        public override IEnumerator GetEnumerator() => throw new NotSupportedException();
        public override bool GetBoolean(int ordinal) => (bool)Value(ordinal)!;
        public override byte GetByte(int ordinal) => (byte)Value(ordinal)!;
        public override char GetChar(int ordinal) => (char)Value(ordinal)!;
        public override DateTime GetDateTime(int ordinal) => (DateTime)Value(ordinal)!;
        public override decimal GetDecimal(int ordinal) => (decimal)Value(ordinal)!;
        public override double GetDouble(int ordinal) => (double)Value(ordinal)!;
        public override float GetFloat(int ordinal) => (float)Value(ordinal)!;
        public override Guid GetGuid(int ordinal) => (Guid)Value(ordinal)!;
        public override short GetInt16(int ordinal) => (short)Value(ordinal)!;
        public override int GetInt32(int ordinal) => (int)Value(ordinal)!;
        public override long GetInt64(int ordinal) => (long)Value(ordinal)!;
    }
}
