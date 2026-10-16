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
            using var command = new NpgsqlCommand("SELECT 1", _connection);
            return Convert.ToInt32(command.ExecuteScalar()) == 1;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
