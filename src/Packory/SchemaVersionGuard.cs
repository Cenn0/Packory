using Microsoft.Data.Sqlite;
using Npgsql;

namespace Packory;

/// <summary>
/// Pre-V3 baseline marker only, not an automatic migration/downgrade service.
/// Reject unknown future schemas BEFORE applying the historical CREATE TABLE batch.
/// </summary>
public static class SchemaVersionGuard
{
    public const int Current = 1;

    private static void RequireCurrent(object? value)
    {
        if (value is null || value is DBNull || Convert.ToInt32(value) != Current)
            throw new InvalidOperationException(
                "Unsupported Packory metadata schema version; reviewed migration required.");
    }

    public static void Verify(SqliteConnection connection)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='packory_schema_version'";
        if (exists.ExecuteScalar() is null) return; // old unmarked installation
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM packory_schema_version WHERE slot=1";
        RequireCurrent(cmd.ExecuteScalar());
    }

    public static void Stamp(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS packory_schema_version (
                slot INTEGER PRIMARY KEY CHECK(slot=1),
                version INTEGER NOT NULL, applied_at TEXT NOT NULL
            );
            INSERT OR IGNORE INTO packory_schema_version(slot,version,applied_at)
            VALUES(1,$version,$at);
            """;
        cmd.Parameters.AddWithValue("$version", Current);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        Verify(connection);
    }

    public static void Verify(NpgsqlConnection connection)
    {
        using var exists = new NpgsqlCommand(
            "SELECT to_regclass('packory_schema_version') IS NOT NULL", connection);
        if (exists.ExecuteScalar() is not bool found || !found) return;
        using var cmd = new NpgsqlCommand(
            "SELECT version FROM packory_schema_version WHERE slot=1", connection);
        RequireCurrent(cmd.ExecuteScalar());
    }

    public static void Stamp(NpgsqlConnection connection)
    {
        using var cmd = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS packory_schema_version (
                slot INTEGER PRIMARY KEY CHECK(slot=1),
                version INTEGER NOT NULL, applied_at TEXT NOT NULL
            );
            INSERT INTO packory_schema_version(slot,version,applied_at)
            VALUES(1,@version,@at) ON CONFLICT(slot) DO NOTHING;
            """, connection);
        cmd.Parameters.AddWithValue("version", Current);
        cmd.Parameters.AddWithValue("at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        Verify(connection);
    }
}
