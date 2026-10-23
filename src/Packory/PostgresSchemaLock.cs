using System.Data;
using Npgsql;

namespace Packory;

/// <summary>Serialize cooperating bootstrap/migration DDL per database and schema.</summary>
internal static class PostgresSchemaLock
{
    // Two-key advisory locks have a separate key space from the writer's bigint lease.
    private const int Domain = 0x5041434B;

    internal static IDisposable Acquire(NpgsqlConnection connection)
    {
        using var identity = new NpgsqlCommand("SELECT hashtext(current_schema())", connection);
        if (identity.ExecuteScalar() is not int schema)
            throw new InvalidOperationException("A current PostgreSQL schema is required.");
        using var acquire = new NpgsqlCommand("SELECT pg_advisory_lock(@domain,@schema)", connection);
        acquire.Parameters.AddWithValue("domain", Domain);
        acquire.Parameters.AddWithValue("schema", schema);
        acquire.CommandTimeout = 30;
        acquire.ExecuteNonQuery();
        return new Lease(connection, schema);
    }

    private sealed class Lease(NpgsqlConnection connection, int schema) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // A terminated session already releases its advisory locks. Successful
            // and failed migrations release explicitly before returning to the pool.
            if (connection.State != ConnectionState.Open) return;
            using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@domain,@schema)", connection);
            release.Parameters.AddWithValue("domain", Domain);
            release.Parameters.AddWithValue("schema", schema);
            release.ExecuteNonQuery();
        }
    }
}
