using Microsoft.Data.Sqlite;

namespace Packory;

public sealed record CatalogEntry(string Kind, string Name, string Version, string Description, string Origin, string UpdatedAt);
public sealed record SkillEntry(string Name, string Version, string Description, string UpdatedAt);
public sealed record StoredArtifact(string Kind, string Name, string Version, string Origin);
public sealed record AuditEntry(long Id, string Action, string Kind, string Name, string Version,
    string Actor, string CreatedAt);
public sealed record ManagedTokenRecord(string Id, string Hash, string Role, string? Namespace, string ExpiresAt);
public sealed record PyFileEntry(string Name, string Version, string Filename, string Sha256, long Size);
public sealed record OciManifestEntry(string Digest, string MediaType, long Size);
public sealed record OciReferrerEntry(string Digest, string MediaType, long Size, string ArtifactType);

public sealed class Database(Settings settings) : IPackageDatabase
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

    public bool IsHealthy()
    {
        using var connection = Open();
        return true;
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
            CREATE TABLE IF NOT EXISTS oci_blobs (
                repository TEXT NOT NULL, digest TEXT NOT NULL,
                PRIMARY KEY(repository,digest)
            );
            CREATE TABLE IF NOT EXISTS oci_manifests (
                repository TEXT NOT NULL, digest TEXT NOT NULL, media_type TEXT NOT NULL,
                size INTEGER NOT NULL, PRIMARY KEY(repository,digest)
            );
            CREATE TABLE IF NOT EXISTS oci_tags (
                repository TEXT NOT NULL, tag TEXT NOT NULL, digest TEXT NOT NULL,
                PRIMARY KEY(repository,tag)
            );
            CREATE TABLE IF NOT EXISTS oci_referrers (
                repository TEXT NOT NULL, subject TEXT NOT NULL, digest TEXT NOT NULL,
                media_type TEXT NOT NULL, size INTEGER NOT NULL, artifact_type TEXT NOT NULL,
                PRIMARY KEY(repository,subject,digest)
            );
            CREATE TABLE IF NOT EXISTS managed_tokens (
                id TEXT PRIMARY KEY, hash TEXT NOT NULL UNIQUE, role TEXT NOT NULL,
                namespace TEXT, expires_at TEXT NOT NULL, revoked INTEGER NOT NULL DEFAULT 0
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

    public bool OciHasBlob(string repository, string digest)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM oci_blobs WHERE repository=$repo AND digest=$digest";
        cmd.Parameters.AddWithValue("$repo", repository);
        cmd.Parameters.AddWithValue("$digest", digest);
        return cmd.ExecuteScalar() is not null;
    }

    public void OciAddBlob(string repository, string digest)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO oci_blobs(repository,digest) VALUES($repo,$digest)";
        cmd.Parameters.AddWithValue("$repo", repository);
        cmd.Parameters.AddWithValue("$digest", digest);
        cmd.ExecuteNonQuery();
    }

    public OciManifestEntry? OciManifest(string repository, string reference)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT m.digest,m.media_type,m.size FROM oci_manifests m
            WHERE m.repository=$repo AND
              (m.digest=$reference OR EXISTS (
                SELECT 1 FROM oci_tags t WHERE t.repository=m.repository AND t.digest=m.digest AND t.tag=$reference))
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$repo", repository);
        cmd.Parameters.AddWithValue("$reference", reference);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new OciManifestEntry(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)) : null;
    }

    public bool OciPutManifest(string repository, string reference, OciManifestEntry manifest,
        string? subject, string? artifactType, string actor)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO oci_manifests(repository,digest,media_type,size)
            VALUES($repo,$digest,$media,$size)
            """;
        cmd.Parameters.AddWithValue("$repo", repository);
        cmd.Parameters.AddWithValue("$digest", manifest.Digest);
        cmd.Parameters.AddWithValue("$media", manifest.MediaType);
        cmd.Parameters.AddWithValue("$size", manifest.Size);
        cmd.ExecuteNonQuery();
        if (!reference.StartsWith("sha256:", StringComparison.Ordinal))
        {
            cmd.CommandText = """
                INSERT INTO oci_tags(repository,tag,digest) VALUES($repo,$tag,$digest)
                ON CONFLICT(repository,tag) DO UPDATE SET digest=excluded.digest
                """;
            cmd.Parameters.AddWithValue("$tag", reference);
            cmd.ExecuteNonQuery();
            cmd.CommandText = """
                INSERT INTO packages(kind,name,version,description,origin,updated_at)
                VALUES('oci',$repo,$tag,$media,'hosted',$updated)
                ON CONFLICT(kind,name,version) DO UPDATE SET description=excluded.description,updated_at=excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        if (subject is not null)
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO oci_referrers(repository,subject,digest,media_type,size,artifact_type)
                VALUES($repo,$subject,$digest,$media,$size,$artifact)
                """;
            cmd.Parameters.AddWithValue("$subject", subject);
            cmd.Parameters.AddWithValue("$artifact", artifactType ?? "");
            cmd.ExecuteNonQuery();
        }
        InsertAudit(db, tx, "publish", "oci", repository,
            reference, actor);
        tx.Commit();
        return true;
    }

    public IReadOnlyList<string> OciTags(string repository)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT tag FROM oci_tags WHERE repository=$repo ORDER BY tag";
        cmd.Parameters.AddWithValue("$repo", repository);
        using var reader = cmd.ExecuteReader();
        var tags = new List<string>();
        while (reader.Read()) tags.Add(reader.GetString(0));
        return tags;
    }

    public IReadOnlyList<OciReferrerEntry> OciReferrers(string repository, string subject)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT digest,media_type,size,artifact_type FROM oci_referrers
            WHERE repository=$repo AND subject=$subject ORDER BY digest
            """;
        cmd.Parameters.AddWithValue("$repo", repository);
        cmd.Parameters.AddWithValue("$subject", subject);
        using var reader = cmd.ExecuteReader();
        var refs = new List<OciReferrerEntry>();
        while (reader.Read())
            refs.Add(new OciReferrerEntry(reader.GetString(0), reader.GetString(1),
                reader.GetInt64(2), reader.GetString(3)));
        return refs;
    }

    public IReadOnlyList<string> OciAllBlobDigests()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT digest FROM oci_blobs";
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public IReadOnlyList<string> OciAllManifestDigests()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT digest FROM oci_manifests";
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
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

    public void CreateManagedToken(ManagedTokenRecord token)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO managed_tokens(id,hash,role,namespace,expires_at,revoked)
                VALUES($id,$hash,$role,$namespace,$expiry,0)
                """;
            cmd.Parameters.AddWithValue("$id", token.Id);
            cmd.Parameters.AddWithValue("$hash", token.Hash);
            cmd.Parameters.AddWithValue("$role", token.Role);
            cmd.Parameters.AddWithValue("$namespace", (object?)token.Namespace ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$expiry", token.ExpiresAt);
            cmd.ExecuteNonQuery();
        }
        InsertAudit(db, tx, "create", "managed-token", token.Id, "", "admin-token");
        tx.Commit();
    }

    public ManagedTokenRecord? FindManagedToken(string sha256)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id,hash,role,namespace,expires_at FROM managed_tokens
            WHERE hash=$hash AND revoked=0
            """;
        cmd.Parameters.AddWithValue("$hash", sha256);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new ManagedTokenRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4))
            : null;
    }

    public bool RevokeManagedToken(string id)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE managed_tokens SET revoked=1 WHERE id=$id AND revoked=0";
        cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteNonQuery() != 1) return false;
        InsertAudit(db, tx, "revoke", "managed-token", id, "", "admin-token");
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
