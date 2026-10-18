using Npgsql;

namespace Packory;

/// <summary>
/// Keep exactly one active Packory process per PostgreSQL database.
/// Session-level PostgreSQL advisory lock is released on connection loss / process exit.
/// This is a fail-closed safety barrier, NOT horizontal scaling.
/// </summary>
public sealed class PostgresSingleWriterLease(Settings settings) : IDisposable
{
    // Constant application-wide advisory namespace inside the selected PostgreSQL database.
    private const long LockId = 0x5041434B4F525931;
    private NpgsqlConnection? _connection;

    public void Acquire()
    {
        if (settings.MetadataProvider != "postgresql") return;
        var connection = new NpgsqlConnection(settings.PostgresConnectionString);
        connection.Open();
        try
        {
            using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@id)", connection);
            command.Parameters.AddWithValue("id", LockId);
            if (command.ExecuteScalar() is not true)
                throw new InvalidOperationException(
                    "Another Packory process owns the PostgreSQL single-writer lease. Multi-instance writes are unsupported.");
            _connection = connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public bool IsHeld()
    {
        if (settings.MetadataProvider != "postgresql") return true;
        try
        {
            if (_connection?.State != System.Data.ConnectionState.Open) return false;
            // A healthy backend session alone is not evidence of owning the
            // advisory lock: confirm it is still granted to THIS PostgreSQL PID.
            // PostgreSQL exposes a 64-bit advisory key as classid/objid, objsubid=1.
            const string sql = """
                SELECT EXISTS (
                    SELECT 1 FROM pg_catalog.pg_locks
                    WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND granted
                      AND objsubid = 1
                      AND classid::text = @high AND objid::text = @low
                )
                """;
            using var command = new NpgsqlCommand(sql, _connection);
            var key = (ulong)LockId;
            command.Parameters.AddWithValue("high", (key >> 32).ToString(System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("low", (key & 0xffffffffUL).ToString(System.Globalization.CultureInfo.InvariantCulture));
            return command.ExecuteScalar() is true;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        // Lifetime callback and dependency-injection disposal can race or both
        // call Dispose. Claim ownership only once; never dereference a field
        // that another disposer can null while the first thread is using it.
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null) return;
        try
        {
            if (connection.State == System.Data.ConnectionState.Open)
            {
                using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@id)", connection);
                command.Parameters.AddWithValue("id", LockId);
                command.ExecuteScalar();
            }
        }
        catch { /* Connection loss releases the session lock on PostgreSQL. */ }
        finally
        {
            connection.Dispose();
        }
    }
}
