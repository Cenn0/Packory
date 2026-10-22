using Npgsql;

namespace Packory;

/// <summary>
/// PostgreSQL metadata backend for the same single-node Packory contract as SQLite.
/// All published metadata and audit rows are committed in one database transaction.
/// Artifacts remain separately stored and recovered by BlobStore / ArtifactRecovery.
/// </summary>
public sealed class PostgresDatabase(Settings settings) : IPackageDatabase
{
    private readonly string _connectionString = settings.PostgresConnectionString ??
        throw new InvalidOperationException("PostgreSQL connection string is not configured.");

    private NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql,
        NpgsqlTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void Audit(NpgsqlConnection connection, NpgsqlTransaction transaction,
        string action, string kind, string name, string version, string actor)
    {
        using var cmd = Command(connection, """
            INSERT INTO audit_events(action,kind,name,version,actor,created_at)
            VALUES(@action,@kind,@name,@version,@actor,@time)
            """, transaction,
            ("action", action), ("kind", kind), ("name", name), ("version", version),
            ("actor", actor), ("time", DateTimeOffset.UtcNow.ToString("O")));
        cmd.ExecuteNonQuery();
    }

    public bool IsHealthy()
    {
        // Readers must not initialize or migrate a shared schema themselves.
        // A successful SELECT 1 alone is insufficient: it would advertise a
        // fresh, incomplete or incompatible database as a ready read replica.
        try
        {
            using var connection = Open();
            using var cmd = Command(connection, """
                SELECT (SELECT version FROM packory_schema_version WHERE slot=1) = @current
                    AND to_regclass('packages') IS NOT NULL
                    AND to_regclass('npm_versions') IS NOT NULL
                    AND to_regclass('npm_tags') IS NOT NULL
                    AND to_regclass('skills') IS NOT NULL
                    AND to_regclass('cargo_versions') IS NOT NULL
                    AND to_regclass('pypi_files') IS NOT NULL
                    AND to_regclass('oci_blobs') IS NOT NULL
                    AND to_regclass('oci_manifests') IS NOT NULL
                    AND to_regclass('oci_tags') IS NOT NULL
                    AND to_regclass('oci_referrers') IS NOT NULL
                    AND to_regclass('recovery_quarantines') IS NOT NULL
                    AND to_regclass('publication_intents') IS NOT NULL
                    AND to_regclass('managed_tokens') IS NOT NULL
                    AND to_regclass('audit_events') IS NOT NULL
                    AND to_regclass('feeds') IS NOT NULL
                    AND to_regclass('feed_namespaces') IS NOT NULL
                    AND to_regclass('feed_policies') IS NOT NULL
                    AND to_regclass('feed_upstreams') IS NOT NULL
                    AND to_regclass('nuget_digests') IS NOT NULL
                    AND to_regclass('feed_nuget_versions') IS NOT NULL
                    AND to_regclass('raw_artifacts') IS NOT NULL
                    AND to_regclass('maven_assets') IS NOT NULL
                    AND to_regclass('maven_metadata') IS NOT NULL
                    AND to_regclass('go_modules') IS NOT NULL
                    AND to_regclass('feed_assets') IS NOT NULL
                    AND to_regclass('feed_oci_tags') IS NOT NULL
                    AND to_regclass('feed_npm_tags') IS NOT NULL
                """, null, ("current", SchemaVersionGuard.Current));
            return cmd.ExecuteScalar() is true;
        }
        catch (NpgsqlException)
        {
            // Connection failure, absent version table, removed tables and
            // incompatible metadata all fail readiness rather than serving data.
            return false;
        }
    }

    public void Initialize()
    {
        using var db = Open();
        SchemaVersionGuard.Verify(db);
        using var cmd = Command(db, """
            CREATE TABLE IF NOT EXISTS packages (
                kind TEXT NOT NULL, name TEXT NOT NULL, version TEXT NOT NULL,
                description TEXT NOT NULL, origin TEXT NOT NULL, updated_at TEXT NOT NULL,
                PRIMARY KEY(kind,name,version)
            );
            CREATE INDEX IF NOT EXISTS packages_updated ON packages(updated_at DESC);
            CREATE TABLE IF NOT EXISTS npm_versions (
                name TEXT NOT NULL, version TEXT NOT NULL, json TEXT NOT NULL,
                PRIMARY KEY(name,version)
            );
            CREATE TABLE IF NOT EXISTS npm_tags (
                name TEXT NOT NULL, tag TEXT NOT NULL, version TEXT NOT NULL,
                PRIMARY KEY(name,tag)
            );
            CREATE TABLE IF NOT EXISTS skills (
                name TEXT NOT NULL, version TEXT NOT NULL, description TEXT NOT NULL,
                updated_at TEXT NOT NULL, PRIMARY KEY(name,version)
            );
            CREATE TABLE IF NOT EXISTS cargo_versions (
                name TEXT NOT NULL, version TEXT NOT NULL, json TEXT NOT NULL,
                PRIMARY KEY(name,version)
            );
            CREATE TABLE IF NOT EXISTS pypi_files (
                name TEXT NOT NULL, version TEXT NOT NULL, filename TEXT NOT NULL,
                sha256 TEXT NOT NULL, size BIGINT NOT NULL, PRIMARY KEY(name,filename)
            );
            CREATE TABLE IF NOT EXISTS oci_blobs (
                repository TEXT NOT NULL, digest TEXT NOT NULL,
                PRIMARY KEY(repository,digest)
            );
            CREATE TABLE IF NOT EXISTS oci_manifests (
                repository TEXT NOT NULL, digest TEXT NOT NULL, media_type TEXT NOT NULL,
                size BIGINT NOT NULL, PRIMARY KEY(repository,digest)
            );
            CREATE TABLE IF NOT EXISTS oci_tags (
                repository TEXT NOT NULL, tag TEXT NOT NULL, digest TEXT NOT NULL,
                PRIMARY KEY(repository,tag)
            );
            CREATE TABLE IF NOT EXISTS oci_referrers (
                repository TEXT NOT NULL, subject TEXT NOT NULL, digest TEXT NOT NULL,
                media_type TEXT NOT NULL, size BIGINT NOT NULL, artifact_type TEXT NOT NULL,
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
                id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                action TEXT NOT NULL, kind TEXT NOT NULL, name TEXT NOT NULL,
                version TEXT NOT NULL, actor TEXT NOT NULL, created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS audit_created ON audit_events(id DESC);
            """);
        cmd.ExecuteNonQuery();
        SchemaVersionGuard.Stamp(db);
        SchemaVersionGuard.Upgrade(db);
    }

    public GoModuleRevision? GetGoModule(string name, string version)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT name,version,bundle_sha256,bundle_size,zip_sha256,zip_size,
                mod_sha256,mod_size,published_at FROM go_modules
            WHERE name=@name AND version=@version
            """, null, ("name", name), ("version", version));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadGo(reader) : null;
    }

    private static GoModuleRevision ReadGo(NpgsqlDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3), reader.GetString(4), reader.GetInt64(5),
            reader.GetString(6), reader.GetInt64(7), reader.GetString(8));

    public IReadOnlyList<GoModuleRevision> GoModules()
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT name,version,bundle_sha256,bundle_size,zip_sha256,zip_size,
                mod_sha256,mod_size,published_at FROM go_modules ORDER BY name,version
            """);
        using var reader = cmd.ExecuteReader();
        var rows = new List<GoModuleRevision>();
        while (reader.Read()) rows.Add(ReadGo(reader));
        return rows;
    }

    public IReadOnlyList<string> GoModuleVersions(string name)
    {
        using var db = Open();
        using var cmd = Command(db,
            "SELECT version FROM go_modules WHERE name=@name ORDER BY version",
            null, ("name", name));
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
        using var cmd = Command(db, """
            INSERT INTO go_modules(name,version,bundle_sha256,bundle_size,
                zip_sha256,zip_size,mod_sha256,mod_size,published_at)
            VALUES(@name,@version,@bundle,@bundleSize,@zip,@zipSize,@mod,@modSize,@at)
            ON CONFLICT DO NOTHING
            """, tx, ("name", entry.Name), ("version", entry.Version),
            ("bundle", entry.BundleSha256), ("bundleSize", entry.BundleSize),
            ("zip", entry.ZipSha256), ("zipSize", entry.ZipSize),
            ("mod", entry.ModSha256), ("modSize", entry.ModSize),
            ("at", entry.PublishedAt));
        if (cmd.ExecuteNonQuery() != 1) return false;
        using var catalog = Command(db, """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('go',@name,@version,'Hosted Go module','hosted',@at)
            """, tx, ("name", entry.Name), ("version", entry.Version),
            ("at", entry.PublishedAt));
        catalog.ExecuteNonQuery();
        Audit(db, tx, "publish", "go", entry.Name, entry.Version, actor);
        tx.Commit();
        return true;
    }

    public MavenAssetEntry? GetMavenAsset(string path)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT path,group_id,artifact_id,version,sha256,size,media_type,created_at
            FROM maven_assets WHERE path=@path
            """, null, ("path", path));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new MavenAssetEntry(reader.GetString(0),
            reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5), reader.GetString(6),
            reader.GetString(7)) : null;
    }

    public IReadOnlyList<MavenAssetEntry> MavenAssets()
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT path,group_id,artifact_id,version,sha256,size,media_type,created_at
            FROM maven_assets ORDER BY path
            """);
        using var reader = cmd.ExecuteReader();
        var result = new List<MavenAssetEntry>();
        while (reader.Read())
            result.Add(new MavenAssetEntry(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5), reader.GetString(6), reader.GetString(7)));
        return result;
    }

    public IReadOnlyList<string> MavenVersions(string groupId, string artifactId)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT DISTINCT version FROM maven_assets
            WHERE group_id=@group AND artifact_id=@artifact
                AND media_type='application/xml'
            ORDER BY version
            """, null, ("group", groupId), ("artifact", artifactId));
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
        using var put = Command(db, """
            INSERT INTO maven_assets
                (path,group_id,artifact_id,version,sha256,size,media_type,created_at)
            VALUES(@path,@group,@artifact,@version,@sha,@size,@mime,@at)
            ON CONFLICT DO NOTHING
            """, tx, ("path", asset.Path), ("group", asset.GroupId),
            ("artifact", asset.ArtifactId), ("version", asset.Version),
            ("sha", asset.Sha256), ("size", asset.Size),
            ("mime", asset.MediaType), ("at", asset.CreatedAt));
        if (put.ExecuteNonQuery() != 1) return false;
        using var catalog = Command(db, """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('maven',@name,@version,'Hosted Maven release','hosted',@at)
            ON CONFLICT DO NOTHING
            """, tx, ("name", asset.GroupId + ":" + asset.ArtifactId),
            ("version", asset.Version), ("at", asset.CreatedAt));
        catalog.ExecuteNonQuery();
        Audit(db, tx, "publish", "maven",
            asset.GroupId + ":" + asset.ArtifactId, asset.Version, actor);
        tx.Commit();
        return true;
    }

    public MavenMetadataEntry? GetMavenMetadata(string scope, string path)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT scope,path,content_base64,sha256,size,updated_at
            FROM maven_metadata WHERE scope=@scope AND path=@path
            """, null, ("scope", scope), ("path", path));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new MavenMetadataEntry(reader.GetString(0),
            reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt64(4), reader.GetString(5)) : null;
    }

    public IReadOnlyList<MavenMetadataEntry> MavenMetadata()
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT scope,path,content_base64,sha256,size,updated_at
            FROM maven_metadata ORDER BY scope,path
            """);
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
        const string activeScope = """
            (@scope='' OR EXISTS(SELECT 1 FROM feeds WHERE id=@scope
                AND kind='maven' AND mode='hosted' AND visibility='private' AND enabled=1))
            """;
        var sql = expectedSha256 is null
            ? """
                INSERT INTO maven_metadata(scope,path,content_base64,sha256,size,updated_at)
                SELECT @scope,@path,@content,@sha,@size,@at WHERE
                """ + activeScope + " ON CONFLICT(scope,path) DO NOTHING"
            : """
                UPDATE maven_metadata SET content_base64=@content,sha256=@sha,size=@size,updated_at=@at
                WHERE scope=@scope AND path=@path AND sha256=@expected AND
                """ + activeScope;
        using var put = Command(db, sql, tx,
            ("scope", entry.Scope), ("path", entry.Path), ("content", entry.ContentBase64),
            ("sha", entry.Sha256), ("size", entry.Size), ("at", entry.UpdatedAt));
        if (expectedSha256 is not null) put.Parameters.AddWithValue("expected", expectedSha256);
        if (put.ExecuteNonQuery() != 1) return false;
        Audit(db, tx, "metadata-update", "maven",
            (entry.Scope.Length == 0 ? "global" : "feeds/" + entry.Scope) + "/" + entry.Path,
            entry.Sha256, actor);
        tx.Commit();
        return true;
    }

    public RawArtifactEntry? GetRawArtifact(string name, string version)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT name,version,media_type,sha256,size,created_at FROM raw_artifacts
            WHERE name=@name AND version=@version
            """, null, ("name", name), ("version", version));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new RawArtifactEntry(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5)) : null;
    }

    public IReadOnlyList<RawArtifactEntry> RawArtifacts()
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT name,version,media_type,sha256,size,created_at
            FROM raw_artifacts ORDER BY name,version
            """);
        using var reader = cmd.ExecuteReader();
        var result = new List<RawArtifactEntry>();
        while (reader.Read())
            result.Add(new RawArtifactEntry(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5)));
        return result;
    }

    public bool PublishRaw(string name, string version, string mediaType, string sha256, long size, string actor)
    {
        if (!RawEndpoints.ValidName(name) || !RawEndpoints.ValidVersion(version) ||
            !RawEndpoints.ValidMediaType(mediaType) ||
            !ArtifactDigests.ValidSha256(sha256) || size < 0)
            throw new ArgumentException("Invalid raw artifact metadata.");
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var catalog = Command(db, """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('raw',@name,@version,'Generic immutable artifact','hosted',@at)
            ON CONFLICT DO NOTHING
            """, tx, ("name", name), ("version", version),
            ("at", DateTimeOffset.UtcNow.ToString("O")));
        if (catalog.ExecuteNonQuery() != 1) return false;
        using var payload = Command(db, """
            INSERT INTO raw_artifacts(name,version,media_type,sha256,size,created_at)
            VALUES(@name,@version,@mime,@sha,@size,@at)
            """, tx, ("name", name), ("version", version), ("mime", mediaType),
            ("sha", sha256), ("size", size), ("at", DateTimeOffset.UtcNow.ToString("O")));
        payload.ExecuteNonQuery();
        Audit(db, tx, "publish", "raw", name, version, actor);
        tx.Commit();
        return true;
    }

    public void UpsertPackage(string kind, string name, string version, string description, string origin)
    {
        using var db = Open();
        using var cmd = Command(db, """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES(@kind,@name,@version,@description,@origin,@updated)
            ON CONFLICT(kind,name,version) DO UPDATE SET
                description=excluded.description,origin=excluded.origin,updated_at=excluded.updated_at
            WHERE packages.origin <> 'hosted'
            """, null, ("kind", kind), ("name", name), ("version", version),
            ("description", description), ("origin", origin), ("updated", DateTimeOffset.UtcNow.ToString("O")));
        cmd.ExecuteNonQuery();
    }

    public bool PublishNuget(string name, string version, string description, string actor,
        string? sha512 = null, long? size = null)
    {
        if (sha512 is null && size is not null || sha512 is not null && size is null)
            throw new InvalidDataException("NuGet checksum and size must be set together.");
        if (sha512 is not null) NugetDigest.Validate(sha512, size!.Value);
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var cmd = Command(db, """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('nuget',@name,@version,@description,'hosted',@updated)
            ON CONFLICT DO NOTHING
            """, tx, ("name", name), ("version", version),
            ("description", description), ("updated", DateTimeOffset.UtcNow.ToString("O")));
        if (cmd.ExecuteNonQuery() != 1) return false;
        if (sha512 is not null)
        {
            using var checksum = Command(db, """
                INSERT INTO nuget_digests(name,version,sha512,size,origin)
                VALUES(@name,@version,@sha512,@size,'hosted')
                """, tx, ("name", name), ("version", version),
                ("sha512", sha512), ("size", size!.Value));
            if (checksum.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("NuGet digest record could not be committed.");
        }
        Audit(db, tx, "publish", "nuget", name, version, actor);
        tx.Commit();
        return true;
    }

    public NugetDigest? GetNugetDigest(string name, string version)
    {
        using var db = Open();
        using var cmd = Command(db,
            "SELECT sha512,size,origin FROM nuget_digests WHERE name=@name AND version=@version",
            null, ("name", name), ("version", version));
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
        using (var cmd = Command(db, """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('nuget',@name,@version,@description,'mirror',@updated)
            ON CONFLICT(kind,name,version) DO UPDATE SET
                description=excluded.description,updated_at=excluded.updated_at
            WHERE packages.origin='mirror'
            """, tx, ("name", name), ("version", version), ("description", description),
            ("updated", DateTimeOffset.UtcNow.ToString("O"))))
        {
            if (cmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Hosted NuGet metadata cannot be overwritten by a mirror.");
        }
        using (var cmd = Command(db, """
            INSERT INTO nuget_digests(name,version,sha512,size,origin)
            VALUES(@name,@version,@sha512,@size,'mirror')
            ON CONFLICT(name,version) DO NOTHING
            """, tx, ("name", name), ("version", version), ("sha512", sha512),
            ("size", size)))
            cmd.ExecuteNonQuery();
        using (var cmd = Command(db,
            "SELECT sha512,size,origin FROM nuget_digests WHERE name=@name AND version=@version",
            tx, ("name", name), ("version", version)))
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
        using var cmd = Command(db, "SELECT version FROM packages WHERE kind=@kind AND name=@name ORDER BY version",
            null, ("kind", kind), ("name", name));
        using var reader = cmd.ExecuteReader();
        var versions = new List<string>();
        while (reader.Read()) versions.Add(reader.GetString(0));
        return versions;
    }

    public FeedAssetEntry? GetFeedAsset(string feedId, string kind, string path)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT feed_id,kind,path,name,version,media_type,sha256,size,created_at
            FROM feed_assets WHERE feed_id=@feed AND kind=@kind AND path=@path
            """, null, ("feed", feedId), ("kind", kind), ("path", path));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFeedAsset(reader) : null;
    }

    private static FeedAssetEntry ReadFeedAsset(NpgsqlDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetInt64(7), reader.GetString(8));

    public IReadOnlyList<FeedAssetEntry> FeedAssets()
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT feed_id,kind,path,name,version,media_type,sha256,size,created_at
            FROM feed_assets ORDER BY feed_id,kind,path
            """);
        using var reader = cmd.ExecuteReader();
        var result = new List<FeedAssetEntry>();
        while (reader.Read()) result.Add(ReadFeedAsset(reader));
        return result;
    }

    public IReadOnlyList<FeedAssetEntry> FeedPackageAssets(string feedId, string kind, string? name = null)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT feed_id,kind,path,name,version,media_type,sha256,size,created_at
            FROM feed_assets WHERE feed_id=@feed AND kind=@kind
              AND (CAST(@name AS TEXT) IS NULL OR name=@name) ORDER BY name,version,path
            """, null, ("feed", feedId), ("kind", kind), ("name", (object?)name ?? DBNull.Value));
        using var reader = cmd.ExecuteReader();
        var result = new List<FeedAssetEntry>();
        while (reader.Read()) result.Add(ReadFeedAsset(reader));
        return result;
    }

    public IReadOnlyList<FeedNpmTag> FeedNpmTags(string feedId, string name)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT tag,version,deleted FROM feed_npm_tags
            WHERE feed_id=@feed AND name=@name ORDER BY tag
            """, null, ("feed", feedId), ("name", name));
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
        using var cmd = Command(db, """
            INSERT INTO feed_npm_tags(feed_id,name,tag,version,deleted,updated_at)
            SELECT @feed,@name,@tag,@version,@deleted,@at
            WHERE EXISTS (SELECT 1 FROM feeds WHERE id=@feed AND kind='npm'
                AND mode='hosted' AND visibility='private' AND enabled=1)
              AND EXISTS (SELECT 1 FROM feed_assets WHERE feed_id=@feed
                AND kind='npm' AND name=@name AND (@deleted=1 OR version=@version))
            ON CONFLICT(feed_id,name,tag) DO UPDATE
                SET version=excluded.version,deleted=excluded.deleted,updated_at=excluded.updated_at
            """, tx, ("feed", feedId), ("name", name), ("tag", tag),
            ("version", version), ("deleted", deleted ? 1 : 0),
            ("at", DateTimeOffset.UtcNow.ToString("O")));
        if (cmd.ExecuteNonQuery() != 1) return false;
        Audit(db, tx, deleted ? "tag-delete" : "tag-set",
            "feed-npm", feedId + "/" + name, tag, actor);
        tx.Commit();
        return true;
    }

    public string? GetFeedOciTag(string feedId, string name, string tag)
    {
        using var db = Open();
        using var cmd = Command(db,
            "SELECT digest FROM feed_oci_tags WHERE feed_id=@feed AND name=@name AND tag=@tag",
            null, ("feed", feedId), ("name", name), ("tag", tag));
        return cmd.ExecuteScalar() as string;
    }

    public IReadOnlyList<string> FeedOciTags(string feedId, string name)
    {
        using var db = Open();
        using var cmd = Command(db,
            "SELECT tag FROM feed_oci_tags WHERE feed_id=@feed AND name=@name ORDER BY tag",
            null, ("feed", feedId), ("name", name));
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
        using var cmd = Command(db, """
            INSERT INTO feed_oci_tags(feed_id,name,tag,digest,updated_at)
            SELECT @feed,@name,@tag,@digest,@at
            WHERE EXISTS (SELECT 1 FROM feeds WHERE id=@feed AND kind='oci'
                AND mode='hosted' AND visibility='private' AND enabled=1)
              AND EXISTS (SELECT 1 FROM feed_assets WHERE feed_id=@feed AND kind='oci'
                AND name=@name AND version=@digest AND path=@path)
            ON CONFLICT(feed_id,name,tag) DO UPDATE SET digest=excluded.digest,updated_at=excluded.updated_at
            """, tx, ("feed", feedId), ("name", name), ("tag", tag), ("digest", digest),
                ("path", FeedOciEndpoints.AssetPath(name, digest, true)), ("at", DateTimeOffset.UtcNow.ToString("O")));
        if (cmd.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("Private OCI tag requires an active feed and committed manifest.");
        Audit(db, tx, "tag", "feed-oci", feedId + "/" + name, tag, actor);
        tx.Commit();
    }

    public IReadOnlyList<string> FeedMavenVersions(string feedId, string groupId, string artifactId)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT DISTINCT version FROM feed_assets
            WHERE feed_id=@feed AND kind='maven' AND name=@name
            ORDER BY version
            """, null, ("feed", feedId), ("name", groupId + ":" + artifactId));
        using var reader = cmd.ExecuteReader();
        var versions = new List<string>();
        while (reader.Read()) versions.Add(reader.GetString(0));
        return versions;
    }

    public IReadOnlyList<string> FeedGoVersions(string feedId, string module)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT version FROM feed_assets
            WHERE feed_id=@feed AND kind='go' AND name=@module
            ORDER BY version
            """, null, ("feed", feedId), ("module", module));
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
        using var cmd = Command(db, """
            INSERT INTO feed_assets
                (feed_id,kind,path,name,version,media_type,sha256,size,created_at)
            SELECT @feed,@kind,@path,@name,@version,@mime,@sha,@size,@at
            WHERE EXISTS (SELECT 1 FROM feeds
                WHERE id=@feed AND kind=@kind AND mode='hosted'
                  AND visibility='private' AND enabled=1)
            ON CONFLICT DO NOTHING
            """, tx,
            ("feed", asset.FeedId), ("kind", asset.Kind), ("path", asset.Path),
            ("name", asset.Name), ("version", asset.Version), ("mime", asset.MediaType),
            ("sha", asset.Sha256), ("size", asset.Size), ("at", asset.CreatedAt));
        if (cmd.ExecuteNonQuery() != 1) return false;
        Audit(db, tx, "publish", "feed-" + asset.Kind,
            asset.FeedId + "/" + asset.Name, asset.Version, actor);
        tx.Commit();
        return true;
    }

    public IReadOnlyList<FeedDefinition> Feeds(string? after, int limit)
    {
        using var db = Open();
        var sql = """
            SELECT id,kind,mode,visibility,display_name,enabled,created_at,updated_at
            FROM feeds WHERE (@after IS NULL OR id > @after)
            ORDER BY id LIMIT @limit
            """;
        using var cmd = Command(db, sql, null, ("after", (object?)after ?? DBNull.Value),
            ("limit", Math.Clamp(limit, 1, 101)));
        // DBNull has no inferable CLR type for the first-page IS NULL predicate.
        cmd.Parameters["after"].NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text;
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
        using var cmd = Command(db, """
            SELECT id,kind,mode,visibility,display_name,enabled,created_at,updated_at
            FROM feeds WHERE id=@id
            """, null, ("id", id));
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
        using var insert = Command(db, """
            INSERT INTO feeds(id,kind,mode,visibility,display_name,enabled,created_at,updated_at)
            SELECT @id,@kind,@mode,'private',@name,0,@at,@at
            WHERE NOT EXISTS (SELECT 1 FROM audit_events
                WHERE action='feed-hosted-activate' AND kind='feed' AND name=@id)
                AND NOT EXISTS (SELECT 1 FROM managed_tokens
                    WHERE namespace IN ('feed:' || @id || ':read', 'feed:' || @id || ':publish'))
                AND NOT EXISTS (SELECT 1 FROM publication_intents
                    WHERE kind LIKE 'feed-%' AND substr(name,1,length(@id)+1)=@id || '|')
            ON CONFLICT(id) DO NOTHING
            """, tx, ("id", feed.Id), ("kind", feed.Kind), ("mode", feed.Mode),
                ("name", feed.DisplayName), ("at", feed.CreatedAt));
        if (insert.ExecuteNonQuery() != 1) return false;
        Audit(db, tx, "feed-draft-create", "feed", feed.Id, "", actor);
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
        using var delete = Command(db, """
            DELETE FROM feeds
            WHERE id=@id AND mode IN ('hosted','proxy') AND enabled=0
                AND visibility='private'
                AND NOT EXISTS (SELECT 1 FROM feed_namespaces WHERE feed_id=@id)
                AND NOT EXISTS (SELECT 1 FROM feed_policies WHERE feed_id=@id)
                AND NOT EXISTS (SELECT 1 FROM feed_upstreams WHERE feed_id=@id)
                AND NOT EXISTS (SELECT 1 FROM feed_nuget_versions WHERE feed_id=@id)
                AND NOT EXISTS (SELECT 1 FROM feed_assets WHERE feed_id=@id)
                AND NOT EXISTS (SELECT 1 FROM audit_events
                    WHERE action='feed-hosted-activate' AND kind='feed' AND name=@id)
                AND NOT EXISTS (SELECT 1 FROM managed_tokens
                    WHERE namespace IN ('feed:' || @id || ':read', 'feed:' || @id || ':publish'))
                AND NOT EXISTS (SELECT 1 FROM publication_intents
                    WHERE kind LIKE 'feed-%' AND substr(name,1,length(@id)+1)=@id || '|')
            """, tx, ("id", id));
        if (delete.ExecuteNonQuery() != 1) return false;
        Audit(db, tx, "feed-draft-delete", "feed", id, "", actor);
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
        using var cmd = Command(db, """
            UPDATE feeds SET enabled=@next,updated_at=@at
            WHERE id=@id AND kind=@kind AND mode='hosted'
              AND visibility='private' AND enabled=@previous
            """, tx, ("next", enabled ? 1 : 0), ("previous", enabled ? 0 : 1),
            ("at", DateTimeOffset.UtcNow.ToString("O")), ("id", id), ("kind", kind));
        if (cmd.ExecuteNonQuery() != 1) return false;
        Audit(db, tx, enabled ? "feed-hosted-activate" : "feed-hosted-deactivate",
            "feed", id, "", actor);
        tx.Commit();
        return true;
    }

    public IReadOnlyList<string> FeedNugetVersions(string feedId, string name)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT version FROM feed_nuget_versions
            WHERE feed_id=@feed AND name=@name ORDER BY version
            """, null, ("feed", feedId), ("name", name));
        using var reader = cmd.ExecuteReader();
        var versions = new List<string>();
        while (reader.Read()) versions.Add(reader.GetString(0));
        return versions;
    }

    public FeedNugetRevision? GetFeedNugetRevision(string feedId, string name, string version)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT feed_id,name,version,description,sha512,size
            FROM feed_nuget_versions
            WHERE feed_id=@feed AND name=@name AND version=@version
            """, null, ("feed", feedId), ("name", name), ("version", version));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new FeedNugetRevision(reader.GetString(0),
            reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5)) : null;
    }

    public IReadOnlyList<FeedNugetRevision> FeedNugetRevisions()
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT feed_id,name,version,description,sha512,size
            FROM feed_nuget_versions ORDER BY feed_id,name,version
            """);
        using var reader = cmd.ExecuteReader();
        var items = new List<FeedNugetRevision>();
        while (reader.Read()) items.Add(new FeedNugetRevision(reader.GetString(0),
            reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetInt64(5)));
        return items;
    }

    public IReadOnlyList<FeedNugetRevision> FeedNugetRevisions(string feedId)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT feed_id,name,version,description,sha512,size
            FROM feed_nuget_versions WHERE feed_id=@feed ORDER BY name,version
            """, null, ("feed", feedId));
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
        using var cmd = Command(db, """
            INSERT INTO feed_nuget_versions
                (feed_id,name,version,description,sha512,size,created_at)
            SELECT @feed,@name,@version,@description,@sha512,@size,@at
            WHERE EXISTS (
                SELECT 1 FROM feeds
                WHERE id=@feed AND kind='nuget' AND mode='hosted'
                  AND visibility='private' AND enabled=1)
            ON CONFLICT(feed_id,name,version) DO NOTHING
            """, tx, ("feed", feedId), ("name", name), ("version", version),
            ("description", description), ("sha512", sha512), ("size", size),
            ("at", DateTimeOffset.UtcNow.ToString("O")));
        if (cmd.ExecuteNonQuery() != 1) return false;
        Audit(db, tx, "publish", "feed-nuget", feedId + "/" + name, version, actor);
        tx.Commit();
        return true;
    }

    public IReadOnlyList<CatalogEntry> Search(string query, string? kind, int limit)
    {
        using var db = Open();
        var sql = """
            SELECT kind,name,version,description,origin,updated_at FROM packages
            WHERE (name ILIKE @query ESCAPE '\' OR description ILIKE @query ESCAPE '\')
            """ + (kind is null ? "" : " AND kind=@kind") +
            " ORDER BY updated_at DESC LIMIT @limit";
        var pattern = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        using var cmd = Command(db, sql, null, ("query", pattern), ("limit", Math.Clamp(limit, 1, 100)));
        if (kind is not null) cmd.Parameters.AddWithValue("kind", kind);
        using var reader = cmd.ExecuteReader();
        var entries = new List<CatalogEntry>();
        while (reader.Read())
            entries.Add(new CatalogEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return entries;
    }

    public IReadOnlyList<StoredArtifact> StoredArtifacts()
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT kind,name,version,origin FROM packages
            WHERE kind IN ('nuget','npm','skill','cargo','raw')
            """);
        using var reader = cmd.ExecuteReader();
        var list = new List<StoredArtifact>();
        while (reader.Read())
            list.Add(new StoredArtifact(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3)));
        return list;
    }

    public string? PackageOrigin(string kind, string name, string version)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT origin FROM packages WHERE kind=@kind AND name=@name AND version=@version
            """, null, ("kind", kind), ("name", name), ("version", version));
        return cmd.ExecuteScalar() as string;
    }

    public bool HasPackage(string kind, string name, string version) =>
        PackageOrigin(kind, name, version) is not null;

    public bool PutNpmVersion(string name, string version, string json, string description,
        IReadOnlyDictionary<string, string> tags, string actor)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var insert = Command(db, """
            INSERT INTO npm_versions(name,version,json) VALUES(@name,@version,@json)
            ON CONFLICT DO NOTHING
            """, tx, ("name", name), ("version", version), ("json", json));
        if (insert.ExecuteNonQuery() != 1) return false;
        using (var package = Command(db, """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('npm',@name,@version,@description,'hosted',@updated)
            ON CONFLICT(kind,name,version) DO UPDATE SET
                description=excluded.description,origin='hosted',updated_at=excluded.updated_at
            """, tx, ("name", name), ("version", version),
            ("description", description), ("updated", DateTimeOffset.UtcNow.ToString("O"))))
            package.ExecuteNonQuery();
        foreach (var (tag, taggedVersion) in tags)
        {
            using var command = Command(db, """
                INSERT INTO npm_tags(name,tag,version) VALUES(@name,@tag,@version)
                ON CONFLICT(name,tag) DO UPDATE SET version=excluded.version
                """, tx, ("name", name), ("tag", tag), ("version", taggedVersion));
            command.ExecuteNonQuery();
        }
        Audit(db, tx, "publish", "npm", name, version, actor);
        tx.Commit();
        return true;
    }

    public string? NpmVersion(string name, string version)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT json FROM npm_versions WHERE name=@name AND version=@version",
            null, ("name", name), ("version", version));
        return cmd.ExecuteScalar() as string;
    }

    public (Dictionary<string, string> Versions, Dictionary<string, string> Tags) NpmHosted(string name)
    {
        using var db = Open();
        var versions = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = Command(db, "SELECT version,json FROM npm_versions WHERE name=@name",
            null, ("name", name)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) versions[reader.GetString(0)] = reader.GetString(1);
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var cmd = Command(db, "SELECT tag,version FROM npm_tags WHERE name=@name",
            null, ("name", name)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) tags[reader.GetString(0)] = reader.GetString(1);
        return (versions, tags);
    }

    public bool AddSkill(string name, string version, string description, string actor)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var insert = Command(db, """
            INSERT INTO skills(name,version,description,updated_at)
            VALUES(@name,@version,@description,@updated) ON CONFLICT DO NOTHING
            """, tx, ("name", name), ("version", version), ("description", description),
            ("updated", DateTimeOffset.UtcNow.ToString("O")));
        if (insert.ExecuteNonQuery() != 1) return false;
        using (var package = Command(db, """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('skill',@name,@version,@description,'hosted',@updated) ON CONFLICT DO NOTHING
            """, tx, ("name", name), ("version", version), ("description", description),
            ("updated", DateTimeOffset.UtcNow.ToString("O"))))
            package.ExecuteNonQuery();
        Audit(db, tx, "publish", "skill", name, version, actor);
        tx.Commit();
        return true;
    }

    public IReadOnlyList<SkillEntry> Skills(string? name = null)
    {
        using var db = Open();
        var sql = "SELECT name,version,description,updated_at FROM skills" +
            (name is null ? "" : " WHERE name=@name") + " ORDER BY name,version";
        using var cmd = Command(db, sql);
        if (name is not null) cmd.Parameters.AddWithValue("name", name);
        using var reader = cmd.ExecuteReader();
        var list = new List<SkillEntry>();
        while (reader.Read())
            list.Add(new SkillEntry(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3)));
        return list;
    }

    public void BeginPublication(string key, string kind, string name, string version)
    {
        using var db = Open();
        using var cmd = Command(db, """
            INSERT INTO publication_intents(artifact_key,kind,name,version,started_at)
            VALUES(@key,@kind,@name,@version,@started)
            ON CONFLICT(artifact_key) DO NOTHING
            """, null, ("key", key), ("kind", kind), ("name", name),
            ("version", version), ("started", DateTimeOffset.UtcNow.ToString("O")));
        cmd.ExecuteNonQuery();
    }

    public void CompletePublication(string key)
    {
        using var db = Open();
        using var cmd = Command(db,
            "DELETE FROM publication_intents WHERE artifact_key=@key", null, ("key", key));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<PendingPublication> PendingPublications()
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT artifact_key,kind,name,version,started_at
            FROM publication_intents ORDER BY started_at
            """);
        using var reader = cmd.ExecuteReader();
        var result = new List<PendingPublication>();
        while (reader.Read()) result.Add(new PendingPublication(reader.GetString(0),
            reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return result;
    }

    public QuarantineEvidence? FindQuarantine(string key)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT artifact_key,recovery_key,sha256,source_etag,created_at,completed FROM recovery_quarantines WHERE artifact_key=@key",
            null, ("key", key));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new QuarantineEvidence(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt32(5) != 0) : null;
    }

    public void PrepareQuarantine(QuarantineEvidence evidence)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var cmd = Command(db, """
            INSERT INTO recovery_quarantines(artifact_key,recovery_key,sha256,source_etag,created_at,completed)
            SELECT @key,@archive,@sha,@etag,@created,0
            WHERE EXISTS(SELECT 1 FROM publication_intents WHERE artifact_key=@key)
            ON CONFLICT(artifact_key) DO NOTHING
            """, tx, ("key", evidence.Key), ("archive", evidence.RecoveryKey),
            ("sha", evidence.Sha256), ("etag", evidence.SourceEtag), ("created", evidence.CreatedAt)))
        {
            if (cmd.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("Quarantine evidence already exists or journal is missing");
        }
        Audit(db, tx, "prepare", "recovery", evidence.Key, "", "admin-token");
        tx.Commit();
    }

    public void FinishQuarantine(string key)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var cmd = Command(db,
            "UPDATE recovery_quarantines SET completed=1 WHERE artifact_key=@key AND completed=0",
            tx, ("key", key)))
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Prepared quarantine not found");
        using (var cmd = Command(db,
            "DELETE FROM publication_intents WHERE artifact_key=@key", tx, ("key", key)))
            if (cmd.ExecuteNonQuery() != 1) throw new InvalidOperationException("Pending intent not found");
        Audit(db, tx, "quarantine", "recovery", key, "", "admin-token");
        tx.Commit();
    }

    public void CreateManagedToken(ManagedTokenRecord token)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var cmd = Command(db, """
            INSERT INTO managed_tokens(id,hash,role,namespace,expires_at,revoked)
            VALUES(@id,@hash,@role,@namespace,@expiry,0)
            """, tx,
            ("id", token.Id), ("hash", token.Hash), ("role", token.Role),
            ("namespace", token.Namespace), ("expiry", token.ExpiresAt)))
            cmd.ExecuteNonQuery();
        Audit(db, tx, "create", "managed-token", token.Id, "", "admin-token");
        tx.Commit();
    }

    public ManagedTokenRecord? FindManagedToken(string sha256)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT id,hash,role,namespace,expires_at FROM managed_tokens
            WHERE hash=@hash AND revoked=0
            """, null, ("hash", sha256));
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
        using var cmd = Command(db,
            "UPDATE managed_tokens SET revoked=1 WHERE id=@id AND revoked=0",
            tx, ("id", id));
        if (cmd.ExecuteNonQuery() != 1) return false;
        Audit(db, tx, "revoke", "managed-token", id, "", "admin-token");
        tx.Commit();
        return true;
    }

    public IReadOnlyList<AuditEntry> AuditEvents(long? before, int limit)
    {
        using var db = Open();
        var sql = """
            SELECT id,action,kind,name,version,actor,created_at FROM audit_events
            """ + (before is null ? "" : " WHERE id < @before") + " ORDER BY id DESC LIMIT @limit";
        using var cmd = Command(db, sql, null, ("limit", Math.Clamp(limit, 1, 100)));
        if (before is not null) cmd.Parameters.AddWithValue("before", before.Value);
        using var reader = cmd.ExecuteReader();
        var list = new List<AuditEntry>();
        while (reader.Read())
            list.Add(new AuditEntry(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        return list;
    }

    public IReadOnlyList<string> CargoIndex(string name)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT json FROM cargo_versions WHERE name=@name ORDER BY version",
            null, ("name", name));
        using var reader = cmd.ExecuteReader();
        var items = new List<string>();
        while (reader.Read()) items.Add(reader.GetString(0));
        return items;
    }

    /// <summary>Change only the yanked flag under a row lock, with transactional audit.</summary>
    public bool SetCargoYanked(string name, string version, bool yanked, string actor)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var find = Command(db, """
            SELECT json FROM cargo_versions WHERE name=@name AND version=@version FOR UPDATE
            """, tx, ("name", name), ("version", version));
        if (find.ExecuteScalar() is not string raw) return false;
        var index = System.Text.Json.Nodes.JsonNode.Parse(raw) as System.Text.Json.Nodes.JsonObject
            ?? throw new InvalidDataException("Invalid stored Cargo index metadata");
        index["yanked"] = yanked;
        using var update = Command(db, """
            UPDATE cargo_versions SET json=@json WHERE name=@name AND version=@version
            """, tx, ("json", index.ToJsonString()), ("name", name), ("version", version));
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("Cargo index update lost its metadata row");
        Audit(db, tx, yanked ? "yank" : "unyank", "cargo", name, version, actor);
        tx.Commit();
        return true;
    }

    public bool PublishCargo(string name, string version, string json, string description, string actor)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var insert = Command(db, """
            INSERT INTO cargo_versions(name,version,json) VALUES(@name,@version,@json)
            ON CONFLICT DO NOTHING
            """, tx, ("name", name), ("version", version), ("json", json));
        if (insert.ExecuteNonQuery() != 1) return false;
        using (var package = Command(db, """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('cargo',@name,@version,@description,'hosted',@updated)
            """, tx, ("name", name), ("version", version), ("description", description),
            ("updated", DateTimeOffset.UtcNow.ToString("O"))))
            package.ExecuteNonQuery();
        Audit(db, tx, "publish", "cargo", name, version, actor);
        tx.Commit();
        return true;
    }

    public IReadOnlyList<string> PyProjects()
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT DISTINCT name FROM pypi_files ORDER BY name");
        using var reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    public IReadOnlyList<PyFileEntry> PyFiles(string? name = null)
    {
        using var db = Open();
        var sql = "SELECT name,version,filename,sha256,size FROM pypi_files" +
            (name is null ? "" : " WHERE name=@name") + " ORDER BY name,filename";
        using var cmd = Command(db, sql);
        if (name is not null) cmd.Parameters.AddWithValue("name", name);
        using var reader = cmd.ExecuteReader();
        var files = new List<PyFileEntry>();
        while (reader.Read())
            files.Add(new PyFileEntry(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetInt64(4)));
        return files;
    }

    public bool PublishPyFile(PyFileEntry file, string description, string actor)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using var insert = Command(db, """
            INSERT INTO pypi_files(name,version,filename,sha256,size)
            VALUES(@name,@version,@filename,@sha256,@size) ON CONFLICT DO NOTHING
            """, tx, ("name", file.Name), ("version", file.Version), ("filename", file.Filename),
            ("sha256", file.Sha256), ("size", file.Size));
        if (insert.ExecuteNonQuery() != 1) return false;
        using (var package = Command(db, """
            INSERT INTO packages(kind,name,version,description,origin,updated_at)
            VALUES('pypi',@name,@version,@description,'hosted',@updated)
            ON CONFLICT DO NOTHING
            """, tx, ("name", file.Name), ("version", file.Version),
            ("description", description), ("updated", DateTimeOffset.UtcNow.ToString("O"))))
            package.ExecuteNonQuery();
        Audit(db, tx, "publish", "pypi", file.Name, file.Version, actor);
        tx.Commit();
        return true;
    }

    public bool OciHasBlob(string repository, string digest)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT 1 FROM oci_blobs WHERE repository=@repo AND digest=@digest
            """, null, ("repo", repository), ("digest", digest));
        return cmd.ExecuteScalar() is not null;
    }

    public void OciAddBlob(string repository, string digest)
    {
        using var db = Open();
        using var cmd = Command(db, """
            INSERT INTO oci_blobs(repository,digest) VALUES(@repo,@digest) ON CONFLICT DO NOTHING
            """, null, ("repo", repository), ("digest", digest));
        cmd.ExecuteNonQuery();
    }

    public OciManifestEntry? OciManifest(string repository, string reference)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT m.digest,m.media_type,m.size FROM oci_manifests m
            WHERE m.repository=@repo AND
              (m.digest=@reference OR EXISTS(
                  SELECT 1 FROM oci_tags t
                  WHERE t.repository=m.repository AND t.digest=m.digest AND t.tag=@reference))
            LIMIT 1
            """, null, ("repo", repository), ("reference", reference));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new OciManifestEntry(reader.GetString(0),
            reader.GetString(1), reader.GetInt64(2)) : null;
    }

    public bool OciPutManifest(string repository, string reference, OciManifestEntry manifest,
        string? subject, string? artifactType, string actor)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        using (var insert = Command(db, """
            INSERT INTO oci_manifests(repository,digest,media_type,size)
            VALUES(@repo,@digest,@media,@size) ON CONFLICT DO NOTHING
            """, tx, ("repo", repository), ("digest", manifest.Digest),
            ("media", manifest.MediaType), ("size", manifest.Size)))
            insert.ExecuteNonQuery();
        if (!reference.StartsWith("sha256:", StringComparison.Ordinal))
        {
            using (var tag = Command(db, """
                INSERT INTO oci_tags(repository,tag,digest) VALUES(@repo,@tag,@digest)
                ON CONFLICT(repository,tag) DO UPDATE SET digest=excluded.digest
                """, tx, ("repo", repository), ("tag", reference), ("digest", manifest.Digest)))
                tag.ExecuteNonQuery();
            using (var package = Command(db, """
                INSERT INTO packages(kind,name,version,description,origin,updated_at)
                VALUES('oci',@name,@version,@description,'hosted',@updated)
                ON CONFLICT(kind,name,version) DO UPDATE SET
                    description=excluded.description,updated_at=excluded.updated_at
                """, tx, ("name", repository), ("version", reference),
                ("description", manifest.MediaType), ("updated", DateTimeOffset.UtcNow.ToString("O"))))
                package.ExecuteNonQuery();
        }
        if (subject is not null)
        {
            using var refer = Command(db, """
                INSERT INTO oci_referrers(repository,subject,digest,media_type,size,artifact_type)
                VALUES(@repo,@subject,@digest,@media,@size,@artifact) ON CONFLICT DO NOTHING
                """, tx, ("repo", repository), ("subject", subject),
                ("digest", manifest.Digest), ("media", manifest.MediaType), ("size", manifest.Size),
                ("artifact", artifactType ?? ""));
            refer.ExecuteNonQuery();
        }
        Audit(db, tx, "publish", "oci", repository, reference, actor);
        tx.Commit();
        return true;
    }

    public IReadOnlyList<string> OciTags(string repository)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT tag FROM oci_tags WHERE repository=@repo ORDER BY tag",
            null, ("repo", repository));
        using var reader = cmd.ExecuteReader();
        var tags = new List<string>();
        while (reader.Read()) tags.Add(reader.GetString(0));
        return tags;
    }

    public IReadOnlyList<OciReferrerEntry> OciReferrers(string repository, string subject)
    {
        using var db = Open();
        using var cmd = Command(db, """
            SELECT digest,media_type,size,artifact_type FROM oci_referrers
            WHERE repository=@repo AND subject=@subject ORDER BY digest
            """, null, ("repo", repository), ("subject", subject));
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
        using var cmd = Command(db, "SELECT DISTINCT digest FROM oci_blobs");
        using var reader = cmd.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    public IReadOnlyList<string> OciAllManifestDigests()
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT DISTINCT digest FROM oci_manifests");
        using var reader = cmd.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }
}
