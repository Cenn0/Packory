using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using NpgsqlTypes;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

[assembly: InternalsVisibleTo("Packory.Tests")]

namespace Packory;

public interface IDatabaseConnectorExecutor
{
    Task ProbeAsync(DatabaseConnectorDefinition definition, string password,
        CancellationToken cancellationToken);
    Task<DatabaseQueryResult> QueryAsync(DatabaseConnectorDefinition definition,
        DatabaseQueryDefinition query, JsonElement arguments, string password,
        CancellationToken cancellationToken);
}

/// <summary>Decimal and 64-bit integer cells are invariant strings, preserving precision
/// for JavaScript MCP clients; other finite primitive numbers retain numeric JSON values.</summary>
public sealed record DatabaseQueryResult(string[] Columns,
    List<Dictionary<string, object?>> Rows, bool Truncated,
    bool SourceDataUntrusted = true, bool CellsTruncated = false);

/// <summary>
/// Executes fixed administrator-approved queries using native drivers. Caller values
/// are parameters, never SQL, identifiers, connection options or connection strings.
/// Application output limits do not bound a driver's internal protocol buffers: an
/// operator-controlled database and deployment network/resource limits remain required.
/// </summary>
public sealed class DatabaseConnectorExecutor(DatabaseConnectorPolicy policy) : IDatabaseConnectorExecutor
{
    internal const int MaximumOutputBytes = 128 * 1024;
    internal const int MaximumCellCharacters = 2048;
    internal const int MaximumColumns = 32;
    private const int MaximumColumnCharacters = 128;

    public async Task ProbeAsync(DatabaseConnectorDefinition definition, string password,
        CancellationToken cancellationToken)
    {
        var query = new DatabaseQueryDefinition("connection_test", "Connection test",
            definition.Provider == "oracle" ? "SELECT 1 AS value FROM DUAL" : "SELECT 1 AS value",
            [], 1, 4);
        using var arguments = JsonDocument.Parse("{}");
        await QueryAsync(definition, query, arguments.RootElement, password, cancellationToken);
    }

    public async Task<DatabaseQueryResult> QueryAsync(DatabaseConnectorDefinition definition,
        DatabaseQueryDefinition query, JsonElement arguments, string password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.MaxRows is < 1 or > 1000 || query.TimeoutSeconds is < 1 or > 30)
            throw new InvalidDataException("Invalid database query limits.");
        // Validate values before DNS, credentials or an external connection is used.
        var values = BindArguments(query.Parameters, arguments);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(35, query.TimeoutSeconds + 10)));
        var ct = deadline.Token;
        var address = await policy.ResolveAsync(definition, ct);
        await using var connection = CreateConnection(definition, address, password);
        await connection.OpenAsync(ct);
        await using var transaction = await BeginReadTransactionAsync(connection, definition.Provider,
            query.TimeoutSeconds, ct);
        await using var command = CreateCommand(connection, transaction, query, values);
        // GetChars is synchronous in ADO.NET. Cancel the native command as well
        // as passing the token to async methods so a deadline interrupts a cell
        // read instead of waiting for the next row boundary.
        using var cancelCommand = ct.UnsafeRegister(static state =>
        {
            try { ((DbCommand)state!).Cancel(); }
            catch (Exception) { /* Cancellation callbacks must never leak provider diagnostics. */ }
        }, command);
        DatabaseQueryResult result;
        await using (var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct))
        {
            result = await ReadResultAsync(reader, query.MaxRows, password, ct);
            if (await reader.NextResultAsync(ct))
                throw new InvalidDataException("Multiple database result sets are not permitted.");
        }
        // No query operation commits. The transaction is rolled back on success,
        // failure and connection disposal; this is not SQL Server read authorization.
        await transaction.RollbackAsync(ct);
        return result;
    }

    internal static DbConnection CreateConnection(DatabaseConnectorDefinition definition,
        IPAddress address, string password)
    {
        if (password is null || password.Length is < 1 or > 4096 || password.Contains('\0'))
            throw new InvalidDataException("Invalid database credential.");
        var pinnedHost = address.ToString();
        return definition.Provider switch
        {
            "postgresql" => CreatePostgres(definition, pinnedHost, password),
            "mysql" or "mariadb" => CreateMySql(definition, pinnedHost, password),
            "sqlserver" => CreateSqlServer(definition, pinnedHost, password),
            "oracle" => CreateOracle(definition, pinnedHost, password),
            _ => throw new InvalidDataException("Unsupported database provider.")
        };
    }

    private static NpgsqlConnection CreatePostgres(DatabaseConnectorDefinition d, string host,
        string password)
    {
        var options = new NpgsqlConnectionStringBuilder
        {
            Host = host, Port = d.Port, Database = d.Database, Username = d.Username,
            Password = password, SslMode = SslMode.VerifyFull,
            GssEncryptionMode = GssEncryptionMode.Disable, Pooling = false,
            Enlist = false, Timeout = 8, CommandTimeout = 30, CancellationTimeout = 1000,
            PersistSecurityInfo = false, IncludeErrorDetail = false,
            ApplicationName = "Packory database connector"
        };
        return new NpgsqlConnection(options.ConnectionString)
        {
            // Keep VerifyFull's trust validation, but authenticate the original TLS
            // identity while the driver connects only to the validated numeric IP.
            SslClientAuthenticationOptionsCallback = ssl =>
            {
                ssl.TargetHost = d.TlsServerName;
                ssl.CertificateChainPolicy ??= new X509ChainPolicy
                { RevocationMode = ssl.CertificateRevocationCheckMode };
                ssl.CertificateChainPolicy.DisableCertificateDownloads = true;
                if (ssl.CertificateChainPolicy.ApplicationPolicy.Count == 0)
                    ssl.CertificateChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            }
        };
    }

    private static MySqlConnection CreateMySql(DatabaseConnectorDefinition d, string host,
        string password)
    {
        var options = new MySqlConnectionStringBuilder
        {
            Server = host, Port = checked((uint)d.Port), Database = d.Database,
            UserID = d.Username, Password = password, Pooling = false,
            SslMode = MySqlSslMode.Required, ConnectionTimeout = 8,
            DefaultCommandTimeout = 30, CancellationTimeout = 1,
            AutoEnlist = false, AllowLoadLocalInfile = false, AllowPublicKeyRetrieval = false,
            AllowUserVariables = false, ServerRedirectionMode = MySqlServerRedirectionMode.Disabled,
            PersistSecurityInfo = false, GuidFormat = MySqlGuidFormat.Char36
        };
        return new MySqlConnection(options.ConnectionString)
        {
            // MySqlConnector only invokes this callback with Required/Preferred.
            // Required always negotiates TLS; this callback restores chain AND
            // original-name verification after the numeric-IP pin replaces Server.
            RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                ValidateMySqlCertificate(d.TlsServerName, certificate, chain, errors)
        };
    }

    internal static bool ValidateMySqlCertificate(string expectedName, X509Certificate? certificate,
        X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate is not X509Certificate2 leaf || chain is null ||
            (errors & ~SslPolicyErrors.RemoteCertificateNameMismatch) != SslPolicyErrors.None)
            return false;
        try { return leaf.MatchesHostname(expectedName, allowWildcards: true, allowCommonName: false); }
        catch (Exception e) when (e is ArgumentException or CryptographicException) { return false; }
    }

    private static SqlConnection CreateSqlServer(DatabaseConnectorDefinition d, string host,
        string password)
    {
        var options = new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{(host.Contains(':') ? "[" + host + "]" : host)},{d.Port}",
            InitialCatalog = d.Database, UserID = d.Username, Password = password,
            Authentication = SqlAuthenticationMethod.SqlPassword, IntegratedSecurity = false,
            Encrypt = SqlConnectionEncryptOption.Mandatory, TrustServerCertificate = false,
            HostNameInCertificate = d.TlsServerName, Pooling = false, Enlist = false,
            ConnectTimeout = 8, CommandTimeout = 30, ConnectRetryCount = 0,
            MultiSubnetFailover = false, MultipleActiveResultSets = false,
            PersistSecurityInfo = false, ApplicationName = "Packory database connector",
            // ReadOnly is a routing hint, not an authorization boundary, and can
            // cause a server to redirect to a different replica. Do not request it.
            ApplicationIntent = ApplicationIntent.ReadWrite
        };
        return new SqlConnection(options.ConnectionString);
    }

    private static OracleConnection CreateOracle(DatabaseConnectorDefinition d, string host,
        string password)
    {
        if (d.Database.Length is < 1 or > 128 || d.Database.Any(c =>
                !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-') ||
            string.IsNullOrWhiteSpace(d.OracleServerCertificateDn))
            throw new InvalidDataException("Invalid Oracle service or TLS identity.");
        var options = new OracleConnectionStringBuilder
        {
            DataSource = $"(DESCRIPTION=(CONNECT_TIMEOUT=8)(TRANSPORT_CONNECT_TIMEOUT=8)" +
                $"(RETRY_COUNT=0)(ADDRESS=(PROTOCOL=TCPS)(HOST={host})(PORT={d.Port}))" +
                $"(CONNECT_DATA=(SERVICE_NAME={d.Database})))",
            UserID = d.Username, Password = password, Pooling = false,
            ConnectionTimeout = 8, Enlist = "false", StatementCacheSize = 0
        };
        return new OracleConnection(options.ConnectionString)
        { SSLServerDNMatch = true, SSLServerCertDN = d.OracleServerCertificateDn,
            AutoProxy = false, WalletLocation = "system" };
    }

    private static async Task<DbTransaction> BeginReadTransactionAsync(DbConnection connection,
        string provider, int timeoutSeconds, CancellationToken ct)
    {
        // MySqlConnector's native flag produces START TRANSACTION READ ONLY.
        if (connection is MySqlConnection mysql)
            return await mysql.BeginTransactionAsync(IsolationLevel.ReadCommitted, isReadOnly: true, ct);
        var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            if (provider is "postgresql" or "oracle")
            {
                await using var readOnly = connection.CreateCommand();
                if (readOnly is not OracleCommand) readOnly.Transaction = tx;
                readOnly.CommandTimeout = timeoutSeconds;
                readOnly.CommandText = "SET TRANSACTION READ ONLY";
                await readOnly.ExecuteNonQueryAsync(ct);
            }
            if (provider == "postgresql")
            {
                await using var limits = connection.CreateCommand();
                limits.Transaction = tx;
                limits.CommandTimeout = timeoutSeconds;
                limits.CommandText = $"SET LOCAL statement_timeout = {timeoutSeconds * 1000}; SET LOCAL lock_timeout = 1000";
                await limits.ExecuteNonQueryAsync(ct);
            }
            // SQL Server has no read-only transaction mode. Its independently
            // provisioned SELECT-only login is mandatory, including for functions,
            // linked servers and execution rights. Rollback alone is insufficient.
            return tx;
        }
        catch
        {
            await tx.DisposeAsync();
            throw;
        }
    }

    internal sealed record BoundParameter(string Name, string Type, object Value, int MaxLength);

    internal static IReadOnlyList<BoundParameter> BindArguments(DatabaseParameterDefinition[] parameters,
        JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || parameters is null || parameters.Length > 32)
            throw new InvalidDataException("Database arguments must match the declared parameter contract.");
        var supplied = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
            if (!supplied.TryAdd(property.Name, property.Value) || supplied.Count > 32)
                throw new InvalidDataException("Duplicate or excessive database arguments.");
        if (supplied.Keys.Any(name => !parameters.Any(p => p.Name == name)))
            throw new InvalidDataException("Unknown database argument.");
        var bound = new List<BoundParameter>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (!names.Add(parameter.Name) || parameter.MaxLength is < 1 or > MaximumCellCharacters)
                throw new InvalidDataException("Invalid database parameter contract.");
            var present = supplied.TryGetValue(parameter.Name, out var value);
            if (!present || value.ValueKind == JsonValueKind.Null)
            {
                if (parameter.Required) throw new InvalidDataException("Required database argument is missing.");
                bound.Add(new(parameter.Name, parameter.Type, DBNull.Value, parameter.MaxLength));
                continue;
            }
            object parsed = parameter.Type switch
            {
                "string" when value.ValueKind == JsonValueKind.String &&
                    value.GetString()!.Length <= parameter.MaxLength => value.GetString()!,
                "int64" when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) => number,
                "decimal" when value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) => number,
                "bool" when value.ValueKind is JsonValueKind.True or JsonValueKind.False => value.GetBoolean(),
                "uuid" when value.ValueKind == JsonValueKind.String && Guid.TryParseExact(value.GetString(), "D", out var id) => id,
                "date" when value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(),
                    "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) =>
                        date.ToDateTime(TimeOnly.MinValue),
                "datetime" when value.ValueKind == JsonValueKind.String && TryTimestamp(value.GetString(), out var timestamp) => timestamp,
                _ => throw new InvalidDataException("Database argument has an invalid type or value.")
            };
            bound.Add(new(parameter.Name, parameter.Type, parsed, parameter.MaxLength));
        }
        return bound;
    }

    private static bool TryTimestamp(string? value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return value is { Length: >= 20 and <= 35 } && value[10] == 'T' &&
            (value.EndsWith('Z') || value[^6] is '+' or '-') &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);
    }

    internal static DbCommand CreateCommand(DbConnection connection, DbTransaction? transaction,
        DatabaseQueryDefinition query, IReadOnlyList<BoundParameter> arguments)
    {
        var command = connection.CreateCommand();
        command.CommandText = query.Sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = query.TimeoutSeconds;
        if (command is OracleCommand oracle)
        {
            oracle.BindByName = true;
            oracle.InitialLOBFetchSize = 0;
            // Transaction follows OracleConnection; the provider manages enlistment.
        }
        else command.Transaction = transaction;
        try
        {
            foreach (var argument in arguments)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = argument.Name;
                parameter.Direction = ParameterDirection.Input;
                if (parameter is not OracleParameter) parameter.Value = argument.Value;
                parameter.IsNullable = argument.Value is DBNull;
                ConfigureParameter(parameter, argument);
                command.Parameters.Add(parameter);
            }
            return command;
        }
        catch { command.Dispose(); throw; }
    }

    private static void ConfigureParameter(DbParameter parameter, BoundParameter argument)
    {
        if (parameter is not OracleParameter &&
            !(parameter is MySqlParameter && argument.Type is "uuid" or "datetime"))
            parameter.DbType = argument.Type switch
            {
                "string" => DbType.String, "int64" => DbType.Int64, "decimal" => DbType.Decimal,
                "bool" => DbType.Boolean, "uuid" => DbType.Guid, "date" => DbType.Date,
                "datetime" => DbType.DateTimeOffset,
                _ => throw new InvalidDataException("Unsupported database parameter type.")
            };
        if (argument.Type == "string") parameter.Size = argument.MaxLength;
        if (argument.Type == "decimal" && argument.Value is decimal number)
        {
            parameter.Precision = 29;
            parameter.Scale = (byte)((decimal.GetBits(number)[3] >> 16) & 0x7f);
        }
        if (parameter is NpgsqlParameter pg)
        {
            pg.NpgsqlDbType = argument.Type switch
            {
                "string" => NpgsqlDbType.Text, "int64" => NpgsqlDbType.Bigint,
                "decimal" => NpgsqlDbType.Numeric, "bool" => NpgsqlDbType.Boolean,
                "uuid" => NpgsqlDbType.Uuid, "date" => NpgsqlDbType.Date,
                "datetime" => NpgsqlDbType.TimestampTz,
                _ => throw new InvalidDataException("Unsupported PostgreSQL parameter type.")
            };
            if (argument.Value is DateTimeOffset timestamp) pg.Value = timestamp.UtcDateTime;
        }
        else if (parameter is MySqlParameter mysql && argument.Type is "uuid" or "datetime")
        {
            mysql.MySqlDbType = argument.Type == "uuid" ? MySqlDbType.VarChar : MySqlDbType.DateTime;
            if (argument.Type == "uuid") mysql.Size = 36;
            mysql.Value = argument.Value switch
            {
                Guid id => id.ToString("D"), DateTimeOffset timestamp => timestamp.UtcDateTime,
                _ => argument.Value
            };
        }
        else if (parameter is OracleParameter oracle)
        {
            oracle.OracleDbType = argument.Type switch
            {
                "string" => OracleDbType.NVarchar2, "int64" => OracleDbType.Int64,
                "decimal" => OracleDbType.Decimal, "bool" => OracleDbType.Int16,
                "uuid" => OracleDbType.Varchar2, "date" => OracleDbType.Date,
                "datetime" => OracleDbType.TimeStampTZ,
                _ => throw new InvalidDataException("Unsupported Oracle parameter type.")
            };
            if (argument.Type == "uuid") oracle.Size = 36;
            oracle.Value = argument.Value switch
            {
                bool flag => (short)(flag ? 1 : 0), Guid id => id.ToString("D"),
                DateTimeOffset timestamp => new OracleTimeStampTZ(timestamp.UtcDateTime, "+00:00"),
                _ => argument.Value
            };
        }
    }

    internal static async Task<DatabaseQueryResult> ReadResultAsync(DbDataReader reader, int maxRows,
        string password, CancellationToken ct)
    {
        if (reader.FieldCount is < 1 or > MaximumColumns)
            throw new InvalidDataException("Database output has too many columns or no result set.");
        var columns = new string[reader.FieldCount];
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Length; i++)
        {
            var raw = reader.GetName(i);
            if (raw.Length is < 1 or > MaximumColumnCharacters || raw.Any(char.IsControl))
                throw new InvalidDataException("Database output column name is invalid.");
            columns[i] = Redact(raw, password);
            if (!names.Add(columns[i])) throw new InvalidDataException("Database output column names must be unique.");
        }
        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;
        var cellsTruncated = false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(columns).Length + 256;
        while (await reader.ReadAsync(ct))
        {
            if (rows.Count == maxRows) { truncated = true; break; }
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < columns.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var cellTruncated = false;
                var cell = ReadCell(reader, i, ref cellTruncated);
                cellsTruncated |= cellTruncated;
                row.Add(columns[i], cell is string text ? Redact(text, password, cellTruncated) : cell);
            }
            // Individual cells and columns are already bounded before serialization.
            // Retained rows never exceed the total JSON budget.
            bytes += JsonSerializer.SerializeToUtf8Bytes(row).Length + 1;
            if (bytes > MaximumOutputBytes)
                throw new InvalidDataException("Database output exceeds the response budget.");
            rows.Add(row);
        }
        return new(columns, rows, truncated, CellsTruncated: cellsTruncated);
    }

    private static object? ReadCell(DbDataReader reader, int index, ref bool truncated)
    {
        if (reader.IsDBNull(index)) return null;
        var type = reader.GetFieldType(index);
        if (type == typeof(string))
        {
            var buffer = new char[MaximumCellCharacters + 1];
            var count = checked((int)reader.GetChars(index, 0, buffer, 0, buffer.Length));
            if (count > MaximumCellCharacters) truncated = true;
            var length = Math.Min(count, MaximumCellCharacters);
            // Never split a Unicode surrogate pair at the cell limit.
            if (length > 0 && char.IsHighSurrogate(buffer[length - 1])) length--;
            return new string(buffer, 0, length);
        }
        if (type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) ||
            type == typeof(short) || type == typeof(ushort) || type == typeof(int) ||
            type == typeof(uint))
            return reader.GetValue(index);
        if (type == typeof(long)) return reader.GetInt64(index).ToString(CultureInfo.InvariantCulture);
        if (type == typeof(ulong)) return ((ulong)reader.GetValue(index)).ToString(CultureInfo.InvariantCulture);
        if (type == typeof(decimal))
        {
            // Provider decimal structs preserve SQL Server/Oracle/MySQL precision
            // beyond System.Decimal without reading an arbitrary object or LOB.
            var number = reader switch
            {
                SqlDataReader sql => sql.GetSqlDecimal(index).ToString(),
                OracleDataReader oracle => oracle.GetOracleDecimal(index).ToString(false),
                MySqlDataReader mysql => mysql.GetMySqlDecimal(index).ToString(),
                _ => reader.GetDecimal(index).ToString(CultureInfo.InvariantCulture)
            };
            if (number.Length > 256)
                throw new InvalidDataException("Database numeric result exceeds the scalar limit.");
            return number;
        }
        if (type == typeof(float))
        {
            var value = reader.GetFloat(index);
            return float.IsFinite(value) ? value : "[non_finite_number]";
        }
        if (type == typeof(double))
        {
            var value = reader.GetDouble(index);
            return double.IsFinite(value) ? value : "[non_finite_number]";
        }
        if (type == typeof(Guid)) return reader.GetGuid(index).ToString("D");
        if (type == typeof(DateTime)) return reader.GetDateTime(index).ToString("O", CultureInfo.InvariantCulture);
        if (type == typeof(DateTimeOffset)) return ((DateTimeOffset)reader.GetValue(index)).ToString("O", CultureInfo.InvariantCulture);
        if (type == typeof(DateOnly)) return ((DateOnly)reader.GetValue(index)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (type == typeof(TimeOnly)) return ((TimeOnly)reader.GetValue(index)).ToString("O", CultureInfo.InvariantCulture);
        // Never materialize binary values, arrays, LOB objects or arbitrary custom
        // provider objects. A reviewed SQL template may cast a bounded scalar.
        return "[unsupported_type]";
    }

    private static string Redact(string value, string password, bool truncated = false)
    {
        if (password.Length == 0) return value;
        if (truncated)
        {
            // A reflected credential longer than the cell limit must not escape
            // redaction as a truncated prefix. KMP finds the matching suffix in
            // linear time, including highly repetitive passwords and cell text.
            var prefix = new int[password.Length];
            for (var i = 1; i < password.Length; i++)
            {
                var j = prefix[i - 1];
                while (j > 0 && password[i] != password[j]) j = prefix[j - 1];
                if (password[i] == password[j]) j++;
                prefix[i] = j;
            }
            var matched = 0;
            foreach (var c in value)
            {
                while (matched > 0 && (matched == password.Length || c != password[matched]))
                    matched = prefix[matched - 1];
                if (c == password[matched]) matched++;
            }
            if (matched > 0 && matched < password.Length)
                value = value[..^matched] + "[redacted]";
        }
        return value.Replace(password, "[redacted]", StringComparison.Ordinal);
    }
}
