using System.Data.Common;
using Npgsql;

namespace Packory;

/// <summary>Atomic deprecation overlays and audit; immutable bundles are never rewritten.</summary>
internal static class FeedNpmDeprecationStore
{
    internal static bool Write(DbConnection db, string feedId, string name,
        IReadOnlyDictionary<string, string> changes,
        IReadOnlyDictionary<string, string>? expected, string actor)
    {
        if (!FeedDefinition.ValidId(feedId) || !Names.Npm(name) || changes.Count is < 1 or > 4096 ||
            changes.Any(x => !FeedNpmEndpoints.ValidVersion(x.Key) ||
                !FeedNpmDeprecationEndpoints.ValidMessage(x.Value)))
            throw new ArgumentException("Invalid npm deprecation batch.");
        using var tx = db.BeginTransaction();
        DbCommand Command(string sql, params (string Name, object Value)[] values)
        {
            var command = db.CreateCommand();
            command.Transaction = tx;
            command.CommandText = sql;
            foreach (var (key, value) in values)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = key;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            return command;
        }
        // Serialize every overlay writer, including the single-version management API.
        // SQLite begins an immediate write transaction; PostgreSQL locks the feed row.
        using (var active = Command("SELECT id FROM feeds WHERE id=@feed AND kind='npm' " +
            "AND mode='hosted' AND visibility='private' AND enabled=1" +
            (db is NpgsqlConnection ? " FOR UPDATE" : ""), ("feed", feedId)))
            if (active.ExecuteScalar() is null) return false;
        if (expected is not null)
        {
            var current = new Dictionary<string, string>(StringComparer.Ordinal);
            using var query = Command("SELECT version,message FROM feed_npm_deprecations WHERE feed_id=@feed AND name=@name",
                ("feed", feedId), ("name", name));
            using (var reader = query.ExecuteReader())
                while (reader.Read()) current.Add(reader.GetString(0), reader.GetString(1));
            if (current.Count != expected.Count ||
                current.Any(x => !expected.TryGetValue(x.Key, out var value) || x.Value != value)) return false;
        }
        foreach (var (version, message) in changes.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var at = DateTimeOffset.UtcNow.ToString("O");
            using var put = Command("""
                INSERT INTO feed_npm_deprecations(feed_id,name,version,message,updated_at)
                SELECT @feed,@name,@version,@message,@at
                WHERE EXISTS(SELECT 1 FROM feed_assets WHERE feed_id=@feed AND kind='npm'
                    AND name=@name AND version=@version AND path=@path)
                ON CONFLICT(feed_id,name,version) DO UPDATE
                SET message=excluded.message,updated_at=excluded.updated_at
                """, ("feed", feedId), ("name", name), ("version", version), ("message", message),
                ("at", at), ("path", FeedNpmEndpoints.PackagePath(name, version)));
            if (put.ExecuteNonQuery() != 1) return false; // Dispose rolls back the whole batch.
            using var audit = Command("""
                INSERT INTO audit_events(action,kind,name,version,actor,created_at)
                VALUES(@action,'feed-npm',@name,@version,@actor,@at)
                """, ("action", message.Length == 0 ? "undeprecate" : "deprecate"),
                ("name", feedId + "/" + name), ("version", version), ("actor", actor), ("at", at));
            audit.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }
}
