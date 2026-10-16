using Microsoft.Data.Sqlite;

namespace Packory;

public sealed record CatalogEntry(string Kind, string Name, string Version, string Description, string Origin, string UpdatedAt);
public sealed record SkillEntry(string Name, string Version, string Description, string UpdatedAt);
public sealed record StoredArtifact(string Kind, string Name, string Version, string Origin);
public sealed record AuditEntry(long Id, string Action, string Kind, string Name, string Version,
    string Actor, string CreatedAt);
public sealed record PyFileEntry(string Name, string Version, string Filename, string Sha256, long Size);

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
            CREATE TABLE IF NOT EXISTS cargo_versions (
                name TEXT NOT NULL, version TEXT NOT NULL, json TEXT NOT NULL,
                PRIMARY KEY(name,version)
            );
            CREATE TABLE IF NOT EXISTS pypi_files (
                name TEXT NOT NULL, version TEXT NOT NULL, filename TEXT NOT NULL,
                sha256 TEXT NOT NULL, size INTEGER NOT NULL,
                PRIMARY KEY(name,filename)
            );
            CREATE TABLE IF NOT EXISTS audit_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                action TEXT NOT NULL, kind TEXT NOT NULL,
                name TEXT NOT NULL, version TEXT NOT NULL,
                actor TEXT NOT NULL, created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS audit_created ON audit_events(id DESC);
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
            WHERE packages.origin <> 'hosted'
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$description", description);
        command.Parameters.AddWithValue("$origin", origin);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>Publish hosted NuGet metadata and audit record in one SQLite transaction.</summary>
    public bool PublishNuget(string name, string version, string description, string actor)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('nuget',$name,$version,$description,'hosted',$updated)
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$description", description);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        if (cmd.ExecuteNonQuery() != 1) return false;
        InsertAudit(db, tx, "publish", "nuget", name, version, actor);
        tx.Commit();
        return true;
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

    public IReadOnlyList<string> CargoIndex(string name)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT json FROM cargo_versions WHERE name=$name ORDER BY version";
        cmd.Parameters.AddWithValue("$name", name);
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public bool PublishCargo(string name, string version, string json, string description, string actor)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO cargo_versions(name,version,json) VALUES($name,$version,$json)";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$json", json);
        if (cmd.ExecuteNonQuery() != 1) return false;
        cmd.CommandText = """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('cargo',$name,$version,$description,'hosted',$updated)
            """;
        cmd.Parameters.AddWithValue("$description", description);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        InsertAudit(db, tx, "publish", "cargo", name, version, actor);
        tx.Commit();
        return true;
    }

    public IReadOnlyList<string> PyProjects()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT name FROM pypi_files ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    public IReadOnlyList<PyFileEntry> PyFiles(string? name = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT name,version,filename,sha256,size FROM pypi_files
            WHERE $name IS NULL OR name=$name ORDER BY name,filename
            """;
        cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var entries = new List<PyFileEntry>();
        while (reader.Read())
            entries.Add(new PyFileEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4)));
        return entries;
    }

    public bool PublishPyFile(PyFileEntry file, string description, string actor)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO pypi_files(name,version,filename,sha256,size)
            VALUES($name,$version,$filename,$sha256,$size)
            """;
        cmd.Parameters.AddWithValue("$name", file.Name);
        cmd.Parameters.AddWithValue("$version", file.Version);
        cmd.Parameters.AddWithValue("$filename", file.Filename);
        cmd.Parameters.AddWithValue("$sha256", file.Sha256);
        cmd.Parameters.AddWithValue("$size", file.Size);
        if (cmd.ExecuteNonQuery() != 1) return false;
        cmd.CommandText = """
            INSERT OR IGNORE INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('pypi',$name,$version,$description,'hosted',$updated)
            """;
        cmd.Parameters.AddWithValue("$description", description);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        InsertAudit(db, tx, "publish", "pypi", file.Name, file.Version, actor);
        tx.Commit();
        return true;
    }

    public IReadOnlyList<StoredArtifact> StoredArtifacts()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT kind,name,version,origin FROM packages WHERE kind IN ('nuget','npm','skill','cargo')";
        using var reader = cmd.ExecuteReader();
        var result = new List<StoredArtifact>();
        while (reader.Read())
            result.Add(new StoredArtifact(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return result;
    }

    public string? PackageOrigin(string kind, string name, string version)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT origin FROM packages WHERE kind=$kind AND name=$name AND version=$version";
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        return cmd.ExecuteScalar() as string;
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

    public bool PutNpmVersion(string name, string version, string json, string description,
        IReadOnlyDictionary<string, string> tags, string actor)
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
        InsertAudit(db, tx, "publish", "npm", name, version, actor);
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

    public bool AddSkill(string name, string version, string description, string actor)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT OR IGNORE INTO skills(name,version,description,updated_at) VALUES($name,$version,$description,$updated)";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$description", description);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        if (cmd.ExecuteNonQuery() != 1) return false;
        cmd.CommandText = """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('skill',$name,$version,$description,'hosted',$updated)
            """;
        cmd.ExecuteNonQuery();
        InsertAudit(db, tx, "publish", "skill", name, version, actor);
        tx.Commit();
        return true;
    }

    public IReadOnlyList<AuditEntry> AuditEvents(long? before, int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id,action,kind,name,version,actor,created_at FROM audit_events
            WHERE ($before IS NULL OR id < $before)
            ORDER BY id DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$before", (object?)before ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using var reader = cmd.ExecuteReader();
        var entries = new List<AuditEntry>();
        while (reader.Read())
            entries.Add(new AuditEntry(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        return entries;
    }

    private static void InsertAudit(SqliteConnection connection, SqliteTransaction tx,
        string action, string kind, string name, string version, string actor)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO audit_events(action,kind,name,version,actor,created_at)
            VALUES($action,$kind,$name,$version,$actor,$time)
            """;
        cmd.Parameters.AddWithValue("$action", action);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$actor", actor);
        cmd.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
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
