using Microsoft.Data.Sqlite;

namespace Packory;

public sealed record CatalogEntry(string Kind, string Name, string Version, string Description, string Origin, string UpdatedAt);
public sealed record SkillEntry(string Name, string Version, string Description, string UpdatedAt);
public sealed record StoredArtifact(string Kind, string Name, string Version, string Origin);
public sealed record AuditEntry(long Id, string Action, string Kind, string Name, string Version,
    string Actor, string CreatedAt);
public sealed record PendingPublication(string Key, string Kind, string Name, string Version, string StartedAt);
public sealed record QuarantineEvidence(string Key, string RecoveryKey, string Sha256,
    string SourceEtag, string CreatedAt, bool Completed);
public sealed record ManagedTokenRecord(string Id, string Hash, string Role, string? Namespace, string ExpiresAt);
public sealed record PyFileEntry(string Name, string Version, string Filename, string Sha256, long Size);
public sealed record RawArtifactEntry(string Name, string Version, string MediaType,
    string Sha256, long Size, string CreatedAt);
public sealed record MavenAssetEntry(string Path, string GroupId, string ArtifactId, string Version,
    string Sha256, long Size, string MediaType, string CreatedAt);
public sealed record GoModuleRevision(string Name, string Version, string BundleSha256,
    long BundleSize, string ZipSha256, long ZipSize,
    string ModSha256, long ModSize, string PublishedAt);
public sealed record FeedNugetRevision(string FeedId, string Name, string Version,
    string Description, string Sha512, long Size);
public sealed record FeedNpmTag(string Tag, string Version, bool Deleted);
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
        SchemaVersionGuard.Verify(db);
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
            CREATE TABLE IF NOT EXISTS recovery_quarantines (
                artifact_key TEXT PRIMARY KEY, recovery_key TEXT NOT NULL UNIQUE,
                sha256 TEXT NOT NULL, source_etag TEXT NOT NULL,
                created_at TEXT NOT NULL, completed INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS publication_intents (
                artifact_key TEXT PRIMARY KEY,
                kind TEXT NOT NULL, name TEXT NOT NULL, version TEXT NOT NULL,
                started_at TEXT NOT NULL
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
        SchemaVersionGuard.Stamp(db);
        SchemaVersionGuard.Upgrade(db);
    }

    public GoModuleRevision? GetGoModule(string name, string version)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT name,version,bundle_sha256,bundle_size,zip_sha256,zip_size,
                mod_sha256,mod_size,published_at FROM go_modules
            WHERE name=$name AND version=$version
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadGo(reader) : null;
    }

    private static GoModuleRevision ReadGo(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3), reader.GetString(4), reader.GetInt64(5),
            reader.GetString(6), reader.GetInt64(7), reader.GetString(8));

    public IReadOnlyList<GoModuleRevision> GoModules()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT name,version,bundle_sha256,bundle_size,zip_sha256,zip_size,
                mod_sha256,mod_size,published_at FROM go_modules ORDER BY name,version
            """;
        using var reader = cmd.ExecuteReader();
        var rows = new List<GoModuleRevision>();
        while (reader.Read()) rows.Add(ReadGo(reader));
        return rows;
    }

    public IReadOnlyList<string> GoModuleVersions(string name)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT version FROM go_modules WHERE name=$name ORDER BY version";
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = cmd.ExecuteReader();
        var versions = new List<string>();
        while (reader.Read()) versions.Add(reader.GetString(0));
        return versions;
    }

    public bool PublishGoModule(GoModuleRevision entry, string actor)
    {
        if (!GoModuleBundle.ValidRecord(entry))
            throw new ArgumentException("Invalid immutable Go metadata.", nameof(entry));
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO go_modules(name,version,bundle_sha256,bundle_size,
                zip_sha256,zip_size,mod_sha256,mod_size,published_at)
            VALUES($name,$version,$bundle,$bundleSize,$zip,$zipSize,$mod,$modSize,$at)
            """;
        cmd.Parameters.AddWithValue("$name", entry.Name);
        cmd.Parameters.AddWithValue("$version", entry.Version);
        cmd.Parameters.AddWithValue("$bundle", entry.BundleSha256);
        cmd.Parameters.AddWithValue("$bundleSize", entry.BundleSize);
        cmd.Parameters.AddWithValue("$zip", entry.ZipSha256);
        cmd.Parameters.AddWithValue("$zipSize", entry.ZipSize);
        cmd.Parameters.AddWithValue("$mod", entry.ModSha256);
        cmd.Parameters.AddWithValue("$modSize", entry.ModSize);
        cmd.Parameters.AddWithValue("$at", entry.PublishedAt);
        if (cmd.ExecuteNonQuery() != 1) return false;
        using var catalog = db.CreateCommand();
        catalog.Transaction = tx;
        catalog.CommandText = """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('go',$name,$version,'Hosted Go module','hosted',$at)
            """;
        catalog.Parameters.AddWithValue("$name", entry.Name);
        catalog.Parameters.AddWithValue("$version", entry.Version);
        catalog.Parameters.AddWithValue("$at", entry.PublishedAt);
        catalog.ExecuteNonQuery();
        InsertAudit(db, tx, "publish", "go", entry.Name, entry.Version, actor);
        tx.Commit();
        return true;
    }

    public MavenAssetEntry? GetMavenAsset(string path)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT path,group_id,artifact_id,version,sha256,size,media_type,created_at
            FROM maven_assets WHERE path=$path
            """;
        cmd.Parameters.AddWithValue("$path", path);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new MavenAssetEntry(reader.GetString(0),
            reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5), reader.GetString(6),
            reader.GetString(7)) : null;
    }

    public IReadOnlyList<MavenAssetEntry> MavenAssets()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT path,group_id,artifact_id,version,sha256,size,media_type,created_at
            FROM maven_assets ORDER BY path
            """;
        using var reader = cmd.ExecuteReader();
        var rows = new List<MavenAssetEntry>();
        while (reader.Read())
            rows.Add(new MavenAssetEntry(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5), reader.GetString(6), reader.GetString(7)));
        return rows;
    }

    public IReadOnlyList<string> MavenVersions(string groupId, string artifactId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT version FROM maven_assets
            WHERE group_id=$group AND artifact_id=$artifact
                AND media_type='application/xml'
            ORDER BY version
            """;
        cmd.Parameters.AddWithValue("$group", groupId);
        cmd.Parameters.AddWithValue("$artifact", artifactId);
        using var reader = cmd.ExecuteReader();
        var versions = new List<string>();
        while (reader.Read()) versions.Add(reader.GetString(0));
        return versions;
    }

    public bool PublishMavenAsset(MavenAssetEntry asset, string actor)
    {
        if (!MavenEndpoints.ValidRecord(asset))
            throw new ArgumentException("Invalid Maven asset metadata.", nameof(asset));
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var put = db.CreateCommand();
        put.Transaction = tx;
        put.CommandText = """
            INSERT OR IGNORE INTO maven_assets
                (path,group_id,artifact_id,version,sha256,size,media_type,created_at)
            VALUES($path,$group,$artifact,$version,$sha,$size,$mime,$at)
            """;
        put.Parameters.AddWithValue("$path", asset.Path);
        put.Parameters.AddWithValue("$group", asset.GroupId);
        put.Parameters.AddWithValue("$artifact", asset.ArtifactId);
        put.Parameters.AddWithValue("$version", asset.Version);
        put.Parameters.AddWithValue("$sha", asset.Sha256);
        put.Parameters.AddWithValue("$size", asset.Size);
        put.Parameters.AddWithValue("$mime", asset.MediaType);
        put.Parameters.AddWithValue("$at", asset.CreatedAt);
        if (put.ExecuteNonQuery() != 1) return false;
        using var catalog = db.CreateCommand();
        catalog.Transaction = tx;
        catalog.CommandText = """
            INSERT OR IGNORE INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('maven',$name,$version,'Hosted Maven release','hosted',$at)
            """;
        catalog.Parameters.AddWithValue("$name", asset.GroupId + ":" + asset.ArtifactId);
        catalog.Parameters.AddWithValue("$version", asset.Version);
        catalog.Parameters.AddWithValue("$at", asset.CreatedAt);
        catalog.ExecuteNonQuery();
        InsertAudit(db, tx, "publish", "maven",
            asset.GroupId + ":" + asset.ArtifactId, asset.Version, actor);
        tx.Commit();
        return true;
    }

    public MavenMetadataEntry? GetMavenMetadata(string scope, string path)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT scope,path,content_base64,sha256,size,updated_at
            FROM maven_metadata WHERE scope=$scope AND path=$path
            """;
        cmd.Parameters.AddWithValue("$scope", scope);
        cmd.Parameters.AddWithValue("$path", path);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new MavenMetadataEntry(reader.GetString(0),
            reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), reader.GetString(5)) : null;
    }

    public IReadOnlyList<MavenMetadataEntry> MavenMetadata()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT scope,path,content_base64,sha256,size,updated_at
            FROM maven_metadata ORDER BY scope,path
            """;
        using var reader = cmd.ExecuteReader();
        var rows = new List<MavenMetadataEntry>();
        while (reader.Read())
            rows.Add(new MavenMetadataEntry(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5)));
        return rows;
    }

    public bool PutMavenMetadata(MavenMetadataEntry entry, string actor, string? expectedSha256)
    {
        if (!Packory.MavenMetadata.ValidRecord(entry) ||
            expectedSha256 is not null && !ArtifactDigests.ValidSha256(expectedSha256))
            throw new ArgumentException("Invalid Maven metadata revision.", nameof(entry));
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var put = db.CreateCommand();
        put.Transaction = tx;
        const string activeScope = """
            ($scope='' OR EXISTS(SELECT 1 FROM feeds WHERE id=$scope
                AND kind='maven' AND mode='hosted' AND visibility='private' AND enabled=1))
            """;
        put.CommandText = expectedSha256 is null
            ? """
                INSERT INTO maven_metadata(scope,path,content_base64,sha256,size,updated_at)
                SELECT $scope,$path,$content,$sha,$size,$at WHERE
                """ + activeScope + " ON CONFLICT(scope,path) DO NOTHING"
            : """
                UPDATE maven_metadata SET content_base64=$content,sha256=$sha,size=$size,updated_at=$at
                WHERE scope=$scope AND path=$path AND sha256=$expected AND
                """ + activeScope;
        put.Parameters.AddWithValue("$scope", entry.Scope);
        put.Parameters.AddWithValue("$path", entry.Path);
        put.Parameters.AddWithValue("$content", entry.ContentBase64);
        put.Parameters.AddWithValue("$sha", entry.Sha256);
        put.Parameters.AddWithValue("$size", entry.Size);
        put.Parameters.AddWithValue("$at", entry.UpdatedAt);
        if (expectedSha256 is not null) put.Parameters.AddWithValue("$expected", expectedSha256);
        if (put.ExecuteNonQuery() != 1) return false;
        InsertAudit(db, tx, "metadata-update", "maven",
            (entry.Scope.Length == 0 ? "global" : "feeds/" + entry.Scope) + "/" + entry.Path,
            entry.Sha256, actor);
        tx.Commit();
        return true;
    }

    public RawArtifactEntry? GetRawArtifact(string name, string version)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT name,version,media_type,sha256,size,created_at FROM raw_artifacts
            WHERE name=$name AND version=$version
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new RawArtifactEntry(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5)) : null;
    }

    public IReadOnlyList<RawArtifactEntry> RawArtifacts()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT name,version,media_type,sha256,size,created_at FROM raw_artifacts ORDER BY name,version";
        using var reader = cmd.ExecuteReader();
        var rows = new List<RawArtifactEntry>();
        while (reader.Read())
            rows.Add(new RawArtifactEntry(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5)));
        return rows;
    }

    public bool PublishRaw(string name, string version, string mediaType, string sha256, long size, string actor)
    {
        if (!RawEndpoints.ValidName(name) || !RawEndpoints.ValidVersion(version) ||
            !RawEndpoints.ValidMediaType(mediaType) ||
            !ArtifactDigests.ValidSha256(sha256) || size < 0)
            throw new ArgumentException("Invalid raw artifact metadata.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var catalog = db.CreateCommand();
        catalog.Transaction = tx;
        catalog.CommandText = """
            INSERT OR IGNORE INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('raw',$name,$version,'Generic immutable artifact','hosted',$at)
            """;
        catalog.Parameters.AddWithValue("$name", name);
        catalog.Parameters.AddWithValue("$version", version);
        catalog.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        if (catalog.ExecuteNonQuery() != 1) return false;
        using var payload = db.CreateCommand();
        payload.Transaction = tx;
        payload.CommandText = """
            INSERT INTO raw_artifacts(name,version,media_type,sha256,size,created_at)
            VALUES($name,$version,$mime,$sha,$size,$at)
            """;
        payload.Parameters.AddWithValue("$name", name);
        payload.Parameters.AddWithValue("$version", version);
        payload.Parameters.AddWithValue("$mime", mediaType);
        payload.Parameters.AddWithValue("$sha", sha256);
        payload.Parameters.AddWithValue("$size", size);
        payload.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        payload.ExecuteNonQuery();
        InsertAudit(db, tx, "publish", "raw", name, version, actor);
        tx.Commit();
        return true;
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
    public bool PublishNuget(string name, string version, string description, string actor,
        string? sha512 = null, long? size = null)
    {
        if (sha512 is null && size is not null || sha512 is not null && size is null)
            throw new InvalidDataException("NuGet checksum and size must be set together.");
        if (sha512 is not null) NugetDigest.Validate(sha512, size!.Value);
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
        if (sha512 is not null)
        {
            cmd.CommandText = """
                INSERT INTO nuget_digests(name,version,sha512,size,origin)
                VALUES($name,$version,$sha512,$size,'hosted')
                """;
            cmd.Parameters.AddWithValue("$sha512", sha512);
            cmd.Parameters.AddWithValue("$size", size!.Value);
            if (cmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("NuGet digest record could not be committed.");
        }
        InsertAudit(db, tx, "publish", "nuget", name, version, actor);
        tx.Commit();
        return true;
    }

    public NugetDigest? GetNugetDigest(string name, string version)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT sha512,size,origin FROM nuget_digests WHERE name=$name AND version=$version";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new NugetDigest(reader.GetString(0), reader.GetInt64(1),
            reader.GetString(2)) : null;
    }

    public void UpsertNugetMirror(string name, string version, string description,
        string sha512, long size)
    {
        NugetDigest.Validate(sha512, size);
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('nuget',$name,$version,$description,'mirror',$updated)
            ON CONFLICT(kind,name,version) DO UPDATE SET
                description=excluded.description, updated_at=excluded.updated_at
            WHERE packages.origin='mirror'
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$description", description);
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Hosted NuGet metadata cannot be overwritten by a mirror.");
        cmd.CommandText = """
            INSERT INTO nuget_digests(name,version,sha512,size,origin)
            VALUES($name,$version,$sha512,$size,'mirror')
            ON CONFLICT(name,version) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$sha512", sha512);
        cmd.Parameters.AddWithValue("$size", size);
        cmd.ExecuteNonQuery();
        cmd.CommandText = "SELECT sha512,size,origin FROM nuget_digests WHERE name=$name AND version=$version";
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != sha512 ||
                reader.GetInt64(1) != size || reader.GetString(2) != "mirror")
                throw new InvalidDataException("Existing NuGet mirror digest conflicts with incoming bytes.");
        }
        tx.Commit();
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

    public FeedAssetEntry? GetFeedAsset(string feedId, string kind, string path)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT feed_id,kind,path,name,version,media_type,sha256,size,created_at
            FROM feed_assets WHERE feed_id=$feed AND kind=$kind AND path=$path
            """;
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$path", path);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFeedAsset(reader) : null;
    }

    private static FeedAssetEntry ReadFeedAsset(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetInt64(7), reader.GetString(8));

    public IReadOnlyList<FeedAssetEntry> FeedAssets()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT feed_id,kind,path,name,version,media_type,sha256,size,created_at
            FROM feed_assets ORDER BY feed_id,kind,path
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<FeedAssetEntry>();
        while (reader.Read()) result.Add(ReadFeedAsset(reader));
        return result;
    }

    public IReadOnlyList<FeedAssetEntry> FeedPackageAssets(string feedId, string kind, string? name = null)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT feed_id,kind,path,name,version,media_type,sha256,size,created_at
            FROM feed_assets WHERE feed_id=$feed AND kind=$kind
              AND ($name IS NULL OR name=$name) ORDER BY name,version,path
            """;
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var result = new List<FeedAssetEntry>();
        while (reader.Read()) result.Add(ReadFeedAsset(reader));
        return result;
    }

    public IReadOnlyList<FeedAssetEntry> RawFeedPage(string feedId, string? after, string prefix, int limit)
    {
        if (!FeedDefinition.ValidId(feedId) || limit is < 1 or > 101)
            throw new ArgumentException("Invalid private Raw page.");
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT feed_id,kind,path,name,version,media_type,sha256,size,created_at
            FROM feed_assets WHERE feed_id=$feed AND kind='raw'
              AND ($after IS NULL OR path COLLATE BINARY > $after)
              AND substr(name,1,length($prefix))=$prefix
            ORDER BY path COLLATE BINARY LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$prefix", prefix);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var rows = new List<FeedAssetEntry>();
        while (reader.Read()) rows.Add(ReadFeedAsset(reader));
        return rows;
    }

    public IReadOnlyList<FeedNpmTag> FeedNpmTags(string feedId, string name)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT tag,version,deleted FROM feed_npm_tags
            WHERE feed_id=$feed AND name=$name ORDER BY tag
            """;
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = cmd.ExecuteReader();
        var tags = new List<FeedNpmTag>();
        while (reader.Read()) tags.Add(new FeedNpmTag(
            reader.GetString(0), reader.GetString(1), reader.GetInt32(2) == 1));
        return tags;
    }

    public bool SetFeedNpmTag(string feedId, string name, string tag,
        string version, bool deleted, string actor)
    {
        if (!FeedDefinition.ValidId(feedId) || !Names.Npm(name) ||
            !FeedNpmEndpoints.ValidTag(tag) ||
            (!deleted && !FeedNpmEndpoints.ValidVersion(version)) ||
            (deleted && version != ""))
            throw new ArgumentException("Invalid private npm dist-tag reference.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO feed_npm_tags(feed_id,name,tag,version,deleted,updated_at)
            SELECT $feed,$name,$tag,$version,$deleted,$at
            WHERE EXISTS (SELECT 1 FROM feeds WHERE id=$feed AND kind='npm'
                AND mode='hosted' AND visibility='private' AND enabled=1)
              AND EXISTS (SELECT 1 FROM feed_assets WHERE feed_id=$feed
                AND kind='npm' AND name=$name AND ($deleted=1 OR version=$version))
            ON CONFLICT(feed_id,name,tag) DO UPDATE
                SET version=excluded.version,deleted=excluded.deleted,updated_at=excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$tag", tag);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$deleted", deleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        if (cmd.ExecuteNonQuery() != 1) return false;
        InsertAudit(db, tx, deleted ? "tag-delete" : "tag-set",
            "feed-npm", feedId + "/" + name, tag, actor);
        tx.Commit();
        return true;
    }

    public bool? FeedCargoYanked(string feedId, string name, string version)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT yanked FROM feed_cargo_yanks
            WHERE feed_id=$feed AND name=$name AND version=$version
            """;
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt32(value) == 1;
    }

    public bool SetFeedCargoYanked(string feedId, string name, string version,
        bool yanked, string actor)
    {
        if (!FeedDefinition.ValidId(feedId) || !CargoEndpoints.IsName(name) ||
            !FeedCargoEndpoints.IsVersion(version))
            throw new ArgumentException("Invalid private Cargo yank identity.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO feed_cargo_yanks(feed_id,name,version,yanked,updated_at)
            SELECT $feed,$name,$version,$yanked,$at
            WHERE EXISTS (SELECT 1 FROM feeds WHERE id=$feed AND kind='cargo'
                AND mode='hosted' AND visibility='private' AND enabled=1)
              AND EXISTS (SELECT 1 FROM feed_assets WHERE feed_id=$feed
                AND kind='cargo' AND name=$name AND version=$version AND path=$path)
            ON CONFLICT(feed_id,name,version) DO UPDATE
                SET yanked=excluded.yanked,updated_at=excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$yanked", yanked ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$path", FeedCargoEndpoints.AssetPath(name, version));
        if (cmd.ExecuteNonQuery() != 1) return false;
        InsertAudit(db, tx, yanked ? "yank" : "unyank",
            "feed-cargo", feedId + "/" + name, version, actor);
        tx.Commit();
        return true;
    }

    public string? GetFeedOciTag(string feedId, string name, string tag)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT digest FROM feed_oci_tags WHERE feed_id=$feed AND name=$name AND tag=$tag";
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$tag", tag);
        return cmd.ExecuteScalar() as string;
    }

    public IReadOnlyList<string> FeedOciTags(string feedId, string name)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT tag FROM feed_oci_tags WHERE feed_id=$feed AND name=$name ORDER BY tag";
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public void SetFeedOciTag(string feedId, string name, string tag, string digest, string actor)
    {
        if (!FeedDefinition.ValidId(feedId) || !OciEndpoints.ValidRepository(name) ||
            !OciEndpoints.ValidReference(tag) || OciEndpoints.ValidDigest(tag) || !OciEndpoints.ValidDigest(digest))
            throw new InvalidOperationException("Invalid private OCI tag.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO feed_oci_tags(feed_id,name,tag,digest,updated_at)
            SELECT $feed,$name,$tag,$digest,$at
            WHERE EXISTS (SELECT 1 FROM feeds WHERE id=$feed AND kind='oci'
                AND mode='hosted' AND visibility='private' AND enabled=1)
              AND EXISTS (SELECT 1 FROM feed_assets WHERE feed_id=$feed AND kind='oci'
                AND name=$name AND version=$digest AND path=$path)
            ON CONFLICT(feed_id,name,tag) DO UPDATE SET digest=excluded.digest,updated_at=excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$tag", tag);
        cmd.Parameters.AddWithValue("$digest", digest);
        cmd.Parameters.AddWithValue("$path", FeedOciEndpoints.AssetPath(name, digest, true));
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Private OCI tag requires an active feed and committed manifest.");
        InsertAudit(db, tx, "tag", "feed-oci", feedId + "/" + name, tag, actor);
        tx.Commit();
    }

    public IReadOnlyList<string> FeedMavenVersions(string feedId, string groupId, string artifactId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT version FROM feed_assets
            WHERE feed_id=$feed AND kind='maven' AND name=$name
            ORDER BY version
            """;
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$name", groupId + ":" + artifactId);
        using var reader = cmd.ExecuteReader();
        var versions = new List<string>();
        while (reader.Read()) versions.Add(reader.GetString(0));
        return versions;
    }

    public IReadOnlyList<string> FeedGoVersions(string feedId, string module)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT version FROM feed_assets
            WHERE feed_id=$feed AND kind='go' AND name=$module
            ORDER BY version
            """;
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$module", module);
        using var reader = cmd.ExecuteReader();
        var versions = new List<string>();
        while (reader.Read()) versions.Add(reader.GetString(0));
        return versions;
    }

    public bool PublishFeedAsset(FeedAssetEntry asset, string actor)
    {
        if (!FeedAssetEntry.Valid(asset))
            throw new ArgumentException("Invalid private hosted asset.", nameof(asset));
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO feed_assets
                (feed_id,kind,path,name,version,media_type,sha256,size,created_at)
            SELECT $feed,$kind,$path,$name,$version,$mime,$sha,$size,$at
            WHERE EXISTS (SELECT 1 FROM feeds
                WHERE id=$feed AND kind=$kind AND mode='hosted'
                  AND visibility='private' AND enabled=1)
            """;
        cmd.Parameters.AddWithValue("$feed", asset.FeedId);
        cmd.Parameters.AddWithValue("$kind", asset.Kind);
        cmd.Parameters.AddWithValue("$path", asset.Path);
        cmd.Parameters.AddWithValue("$name", asset.Name);
        cmd.Parameters.AddWithValue("$version", asset.Version);
        cmd.Parameters.AddWithValue("$mime", asset.MediaType);
        cmd.Parameters.AddWithValue("$sha", asset.Sha256);
        cmd.Parameters.AddWithValue("$size", asset.Size);
        cmd.Parameters.AddWithValue("$at", asset.CreatedAt);
        if (cmd.ExecuteNonQuery() != 1) return false;
        InsertAudit(db, tx, "publish", "feed-" + asset.Kind,
            asset.FeedId + "/" + asset.Name, asset.Version, actor);
        tx.Commit();
        return true;
    }

    public IReadOnlyList<FeedDefinition> Feeds(string? after, int limit)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id,kind,mode,visibility,display_name,enabled,created_at,updated_at
            FROM feeds WHERE ($after IS NULL OR id > $after)
            ORDER BY id LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$after", (object?)after ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 101));
        var feeds = new List<FeedDefinition>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            feeds.Add(new FeedDefinition(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt32(5) == 1, reader.GetString(6), reader.GetString(7)));
        return feeds;
    }

    public FeedDefinition? Feed(string id)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id,kind,mode,visibility,display_name,enabled,created_at,updated_at
            FROM feeds WHERE id=$id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new FeedDefinition(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetInt32(5) == 1, reader.GetString(6), reader.GetString(7)) : null;
    }

    public bool TryCreateDraftFeed(FeedDefinition feed, string actor)
    {
        if (feed.Enabled || feed.Visibility != "private" || feed.Mode is not ("hosted" or "proxy"))
            throw new ArgumentException("Only disabled private draft feeds may be created.", nameof(feed));
        // Older versions could remove the feed row while retaining its
        // activation history, grants or journal. Those IDs cannot be reassigned.
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT OR IGNORE INTO feeds(id,kind,mode,visibility,display_name,enabled,created_at,updated_at)
            SELECT $id,$kind,$mode,'private',$name,0,$at,$at
            WHERE NOT EXISTS (SELECT 1 FROM audit_events
                WHERE action='feed-hosted-activate' AND kind='feed' AND name=$id)
                AND NOT EXISTS (SELECT 1 FROM managed_tokens
                    WHERE namespace IN ('feed:' || $id || ':read', 'feed:' || $id || ':publish'))
                AND NOT EXISTS (SELECT 1 FROM publication_intents
                    WHERE kind LIKE 'feed-%' AND substr(name,1,length($id)+1)=$id || '|')
            """;
        insert.Parameters.AddWithValue("$id", feed.Id);
        insert.Parameters.AddWithValue("$kind", feed.Kind);
        insert.Parameters.AddWithValue("$mode", feed.Mode);
        insert.Parameters.AddWithValue("$name", feed.DisplayName);
        insert.Parameters.AddWithValue("$at", feed.CreatedAt);
        if (insert.ExecuteNonQuery() != 1) return false;
        InsertAudit(db, tx, "feed-draft-create", "feed", feed.Id, "", actor);
        tx.Commit();
        return true;
    }

    public bool TryDeleteDraftFeed(string id, string actor)
    {
        // Activated IDs are durable authorization identities, including for
        // externally signed OIDC grants. The append-only activation audit is
        // their tombstone; imported grants and pending evidence also retain IDs.
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var delete = db.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = """
            DELETE FROM feeds
            WHERE id=$id AND mode IN ('hosted','proxy') AND enabled=0
                AND visibility='private'
                AND NOT EXISTS (SELECT 1 FROM feed_namespaces WHERE feed_id=$id)
                AND NOT EXISTS (SELECT 1 FROM feed_policies WHERE feed_id=$id)
                AND NOT EXISTS (SELECT 1 FROM feed_upstreams WHERE feed_id=$id)
                AND NOT EXISTS (SELECT 1 FROM feed_nuget_versions WHERE feed_id=$id)
                AND NOT EXISTS (SELECT 1 FROM feed_assets WHERE feed_id=$id)
                AND NOT EXISTS (SELECT 1 FROM audit_events
                    WHERE action='feed-hosted-activate' AND kind='feed' AND name=$id)
                AND NOT EXISTS (SELECT 1 FROM managed_tokens
                    WHERE namespace IN ('feed:' || $id || ':read', 'feed:' || $id || ':publish'))
                AND NOT EXISTS (SELECT 1 FROM publication_intents
                    WHERE kind LIKE 'feed-%' AND substr(name,1,length($id)+1)=$id || '|')
            """;
        delete.Parameters.AddWithValue("$id", id);
        if (delete.ExecuteNonQuery() != 1) return false;
        InsertAudit(db, tx, "feed-draft-delete", "feed", id, "", actor);
        tx.Commit();
        return true;
    }

    public bool TryActivateHostedNugetFeed(string id, string actor) =>
        TrySetHostedFeedEnabled(id, "nuget", true, actor);

    public bool TryDeactivateHostedNugetFeed(string id, string actor) =>
        TrySetHostedFeedEnabled(id, "nuget", false, actor);

    public bool TrySetHostedFeedEnabled(string id, string kind, bool enabled, string actor)
    {
        if (!FeedDefinition.ValidId(id) || !FeedDefinition.SupportsIsolatedHosted(kind)) return false;
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE feeds SET enabled=$next, updated_at=$at
            WHERE id=$id AND kind=$kind AND mode='hosted'
              AND visibility='private' AND enabled=$previous
            """;
        cmd.Parameters.AddWithValue("$next", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$previous", enabled ? 0 : 1);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$kind", kind);
        if (cmd.ExecuteNonQuery() != 1) return false;
        InsertAudit(db, tx, enabled ? "feed-hosted-activate" : "feed-hosted-deactivate",
            "feed", id, "", actor);
        tx.Commit();
        return true;
    }

    public IReadOnlyList<string> FeedNugetVersions(string feedId, string name)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT version FROM feed_nuget_versions
            WHERE feed_id=$feed AND name=$name ORDER BY version
            """;
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = cmd.ExecuteReader();
        var versions = new List<string>();
        while (reader.Read()) versions.Add(reader.GetString(0));
        return versions;
    }

    public FeedNugetRevision? GetFeedNugetRevision(string feedId, string name, string version)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT feed_id,name,version,description,sha512,size
            FROM feed_nuget_versions
            WHERE feed_id=$feed AND name=$name AND version=$version
            """;
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new FeedNugetRevision(reader.GetString(0),
            reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5)) : null;
    }

    public IReadOnlyList<FeedNugetRevision> FeedNugetRevisions()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT feed_id,name,version,description,sha512,size
            FROM feed_nuget_versions ORDER BY feed_id,name,version
            """;
        using var reader = cmd.ExecuteReader();
        var items = new List<FeedNugetRevision>();
        while (reader.Read()) items.Add(new FeedNugetRevision(reader.GetString(0),
            reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5)));
        return items;
    }

    // Feed-scoped enumeration avoids loading other tenants' catalog rows.
    public IReadOnlyList<FeedNugetRevision> FeedNugetRevisions(string feedId)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT feed_id,name,version,description,sha512,size
            FROM feed_nuget_versions WHERE feed_id=$feed ORDER BY name,version
            """;
        cmd.Parameters.AddWithValue("$feed", feedId);
        using var reader = cmd.ExecuteReader();
        var items = new List<FeedNugetRevision>();
        while (reader.Read()) items.Add(new FeedNugetRevision(reader.GetString(0),
            reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5)));
        return items;
    }

    public bool PublishFeedNuget(string feedId, string name, string version,
        string description, string sha512, long size, string actor)
    {
        NugetDigest.Validate(sha512, size);
        if (!FeedDefinition.ValidId(feedId) || !Names.Nuget(name) || !Names.Version(version))
            throw new ArgumentException("Invalid private NuGet feed reference.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO feed_nuget_versions
                (feed_id,name,version,description,sha512,size,created_at)
            SELECT $feed,$name,$version,$description,$sha512,$size,$at
            WHERE EXISTS (
                SELECT 1 FROM feeds
                WHERE id=$feed AND kind='nuget' AND mode='hosted'
                  AND visibility='private' AND enabled=1)
            """;
        cmd.Parameters.AddWithValue("$feed", feedId);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$description", description);
        cmd.Parameters.AddWithValue("$sha512", sha512);
        cmd.Parameters.AddWithValue("$size", size);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        if (cmd.ExecuteNonQuery() != 1) return false;
        InsertAudit(db, tx, "publish", "feed-nuget", feedId + "/" + name,
            version, actor);
        tx.Commit();
        return true;
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

    /// <summary>Change only the yanked flag, never the immutable crate bytes.</summary>
    public bool SetCargoYanked(string name, string version, bool yanked, string actor)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var find = db.CreateCommand();
        find.Transaction = tx;
        find.CommandText = "SELECT json FROM cargo_versions WHERE name=$name AND version=$version";
        find.Parameters.AddWithValue("$name", name);
        find.Parameters.AddWithValue("$version", version);
        if (find.ExecuteScalar() is not string raw) return false;
        var index = System.Text.Json.Nodes.JsonNode.Parse(raw) as System.Text.Json.Nodes.JsonObject
            ?? throw new InvalidDataException("Invalid stored Cargo index metadata");
        index["yanked"] = yanked;
        using var update = db.CreateCommand();
        update.Transaction = tx;
        update.CommandText = "UPDATE cargo_versions SET json=$json WHERE name=$name AND version=$version";
        update.Parameters.AddWithValue("$json", index.ToJsonString());
        update.Parameters.AddWithValue("$name", name);
        update.Parameters.AddWithValue("$version", version);
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Cargo index update lost its metadata row");
        InsertAudit(db, tx, yanked ? "yank" : "unyank", "cargo", name, version, actor);
        tx.Commit();
        return true;
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
        cmd.CommandText = "SELECT kind,name,version,origin FROM packages WHERE kind IN ('nuget','npm','skill','cargo','raw')";
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

    public void BeginPublication(string key, string kind, string name, string version)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO publication_intents(artifact_key,kind,name,version,started_at)
            VALUES($key,$kind,$name,$version,$started)
            ON CONFLICT(artifact_key) DO NOTHING
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$version", version);
        cmd.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public void CompletePublication(string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM publication_intents WHERE artifact_key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<PendingPublication> PendingPublications()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT artifact_key,kind,name,version,started_at
            FROM publication_intents ORDER BY started_at
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<PendingPublication>();
        while (reader.Read()) result.Add(new PendingPublication(reader.GetString(0),
            reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return result;
    }

    public QuarantineEvidence? FindQuarantine(string key)
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT artifact_key,recovery_key,sha256,source_etag,created_at,completed FROM recovery_quarantines WHERE artifact_key=$key";
        cmd.Parameters.AddWithValue("$key", key);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new QuarantineEvidence(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5) != 0) : null;
    }

    public void PrepareQuarantine(QuarantineEvidence evidence)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO recovery_quarantines(artifact_key,recovery_key,sha256,source_etag,created_at,completed)
                SELECT $key,$archive,$sha,$etag,$created,0
                WHERE EXISTS(SELECT 1 FROM publication_intents WHERE artifact_key=$key)
                ON CONFLICT(artifact_key) DO NOTHING
                """;
            cmd.Parameters.AddWithValue("$key", evidence.Key);
            cmd.Parameters.AddWithValue("$archive", evidence.RecoveryKey);
            cmd.Parameters.AddWithValue("$sha", evidence.Sha256);
            cmd.Parameters.AddWithValue("$etag", evidence.SourceEtag);
            cmd.Parameters.AddWithValue("$created", evidence.CreatedAt);
            if (cmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Quarantine evidence already exists or journal is missing");
        }
        InsertAudit(db, tx, "prepare", "recovery", evidence.Key, "", "admin-token");
        tx.Commit();
    }

    public void FinishQuarantine(string key)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE recovery_quarantines SET completed=1 WHERE artifact_key=$key AND completed=0";
            cmd.Parameters.AddWithValue("$key", key);
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Prepared quarantine not found");
        }
        using (var cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM publication_intents WHERE artifact_key=$key";
            cmd.Parameters.AddWithValue("$key", key);
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Pending intent not found");
        }
        InsertAudit(db, tx, "quarantine", "recovery", key, "", "admin-token");
        tx.Commit();
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
