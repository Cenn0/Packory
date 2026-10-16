using Microsoft.Data.Sqlite;

namespace Packory;

public sealed record CatalogEntry(string Kind, string Name, string Version, string Description, string Origin, string UpdatedAt);
public sealed record SkillEntry(string Name, string Version, string Description, string UpdatedAt);

public sealed class Database(Settings settings)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(Path.GetFullPath(settings.DataDirectory), "packory.sqlite"),
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Initialize()
    {
        Directory.CreateDirectory(settings.DataDirectory);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS packages (
                kind TEXT NOT NULL, name TEXT NOT NULL, version TEXT NOT NULL,
                description TEXT NOT NULL, origin TEXT NOT NULL,
                updated_at TEXT NOT NULL, PRIMARY KEY(kind, name, version)
            );
            CREATE INDEX IF NOT EXISTS packages_updated ON packages(updated_at DESC);
            CREATE TABLE IF NOT EXISTS npm_versions (
                name TEXT NOT NULL, version TEXT NOT NULL, json TEXT NOT NULL,
                PRIMARY KEY(name, version)
            );
            CREATE TABLE IF NOT EXISTS npm_tags (
                name TEXT NOT NULL, tag TEXT NOT NULL, version TEXT NOT NULL,
                PRIMARY KEY(name, tag)
            );
            CREATE TABLE IF NOT EXISTS skills (
                name TEXT NOT NULL, version TEXT NOT NULL, description TEXT NOT NULL,
                updated_at TEXT NOT NULL, PRIMARY KEY(name, version)
            );
            """;
        command.ExecuteNonQuery();
    }

    public void UpsertPackage(string kind, string name, string version, string description, string origin)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO packages (kind,name,version,description,origin,updated_at)
            VALUES ($kind,$name,$version,$description,$origin,$updated)
            ON CONFLICT(kind,name,version) DO UPDATE SET
                description=excluded.description, origin=excluded.origin, updated_at=excluded.updated_at
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$origin", origin);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<string> Versions(string kind, string name)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT version FROM packages WHERE kind=$kind AND name=$name ORDER BY version";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public IReadOnlyList<CatalogEntry> Search(string query, string? kind, int limit)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT kind,name,version,description,origin,updated_at FROM packages
            WHERE ($kind IS NULL OR kind=$kind) AND
                  (name LIKE $query ESCAPE '\' OR description LIKE $query ESCAPE '\')
            ORDER BY updated_at DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        command.Parameters.AddWithValue("$query", "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using var reader = command.ExecuteReader();
        var result = new List<CatalogEntry>();
        while (reader.Read()) result.Add(new CatalogEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return result;
    }

    public bool HasPackage(string kind, string name, string version)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM packages WHERE kind=$kind AND name=$name AND version=$version LIMIT 1";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        return cmd.ExecuteScalar() is not null;
    }

    public bool PutNpmVersion(string name, string version, string json, string description, IReadOnlyDictionary<string, string> tags)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO npm_versions(name,version,json) VALUES($name,$version,$json)";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$json", json);
        if (cmd.ExecuteNonQuery() != 1) return false;
        cmd.CommandText = """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('npm',$name,$version,$description,'hosted',$updated)
            ON CONFLICT(kind,name,version) DO UPDATE SET description=excluded.description,origin='hosted',updated_at=excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$description", description);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        cmd.CommandText = "INSERT INTO npm_tags(name,tag,version) VALUES($name,$tag,$version) ON CONFLICT(name,tag) DO UPDATE SET version=excluded.version";
        foreach (var (tag, value) in tags)
        {
            cmd.Parameters.AddWithValue("$tag", tag);
            cmd.Parameters["$version"].Value = value;
            cmd.ExecuteNonQuery();
            cmd.Parameters.RemoveAt("$tag");
        }
        tx.Commit();
        return true;
    }

    public string? NpmVersion(string name, string version)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM npm_versions WHERE name=$name AND version=$version";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        return cmd.ExecuteScalar() as string;
    }

    public (Dictionary<string, string> Versions, Dictionary<string, string> Tags) NpmHosted(string name)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT version,json FROM npm_versions WHERE name=$name";
        cmd.Parameters.AddWithValue("$name", name);
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) versions[reader.GetString(0)] = reader.GetString(1);
        cmd.CommandText = "SELECT tag,version FROM npm_tags WHERE name=$name";
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) tags[reader.GetString(0)] = reader.GetString(1);
        return (versions, tags);
    }

    public bool AddSkill(string name, string version, string description)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO skills(name,version,description,updated_at) VALUES($name,$version,$description,$updated)";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$description", description);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        return cmd.ExecuteNonQuery() == 1;
    }

    public IReadOnlyList<SkillEntry> Skills(string? name = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name,version,description,updated_at FROM skills WHERE $name IS NULL OR name=$name ORDER BY name,version";
        cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var entries = new List<SkillEntry>();
        while (reader.Read()) entries.Add(new SkillEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return entries;
    }
}
