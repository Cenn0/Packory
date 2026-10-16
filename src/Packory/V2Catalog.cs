using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Packory;

public sealed record Feed(string Slug, string Protocol, string Mode, string Visibility, string? Upstream,
    string CreatedAt);
public sealed record StoredAsset(string Feed, string Kind, string Name, string Version, string Filename,
    string Digest, long Size, string ContentType, string Origin, string State, string CreatedAt);
public sealed record McpRecord(string Name, string Version, string ManifestJson, string State, string CreatedAt);
public sealed record AuditEntry(long Id, string At, string Actor, string Action, string Target, string Details);
public sealed record JobEntry(long Id, string Type, string State, string? Repository, string Payload, int Attempts,
    string NextRun, string? Error);

// V2 schema is additive; the old packages, npm_versions and skills tables are never destroyed.
public sealed class V2Catalog(Database db)
{
    public void Initialize()
    {
        using var conn = db.Open();
        using var command = conn.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS repositories (
                slug TEXT PRIMARY KEY COLLATE BINARY,
                protocol TEXT NOT NULL CHECK (protocol IN ('raw','nuget','npm','skills','mcp','cargo','pypi')),
                mode TEXT NOT NULL CHECK (mode IN ('hosted','proxy')),
                visibility TEXT NOT NULL CHECK (visibility IN ('private','public')),
                upstream TEXT,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS v2_blobs (
                digest TEXT PRIMARY KEY, bytes INTEGER NOT NULL CHECK(bytes >= 0),
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS v2_assets (
                feed TEXT NOT NULL REFERENCES repositories(slug),
                kind TEXT NOT NULL, name TEXT NOT NULL, version TEXT NOT NULL,
                filename TEXT NOT NULL, digest TEXT NOT NULL REFERENCES v2_blobs(digest),
                bytes INTEGER NOT NULL, content_type TEXT NOT NULL,
                origin TEXT NOT NULL CHECK (origin IN ('hosted','mirror')),
                state TEXT NOT NULL CHECK (state IN ('published','quarantined','yanked')),
                created_at TEXT NOT NULL,
                PRIMARY KEY(feed,kind,name,version,filename)
            );
            CREATE INDEX IF NOT EXISTS v2_assets_find ON v2_assets(feed,kind,name,version);
            CREATE INDEX IF NOT EXISTS v2_assets_digest ON v2_assets(digest);
            CREATE TABLE IF NOT EXISTS v2_npm_versions (
                feed TEXT NOT NULL REFERENCES repositories(slug),
                name TEXT NOT NULL, version TEXT NOT NULL, manifest TEXT NOT NULL,
                PRIMARY KEY(feed,name,version)
            );
            CREATE TABLE IF NOT EXISTS v2_tokens (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, token_hash TEXT NOT NULL UNIQUE,
                feed TEXT REFERENCES repositories(slug), scopes TEXT NOT NULL,
                expires_at TEXT, revoked_at TEXT, created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS v2_tokens_hash ON v2_tokens(token_hash);
            CREATE TABLE IF NOT EXISTS v2_audit (
                id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL,
                actor TEXT NOT NULL, action TEXT NOT NULL, target TEXT NOT NULL, details TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS v2_jobs (
                id INTEGER PRIMARY KEY AUTOINCREMENT, type TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('queued','running','completed','failed')),
                repository TEXT REFERENCES repositories(slug), payload TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0, next_run TEXT NOT NULL,
                error TEXT, created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS v2_jobs_schedule ON v2_jobs(state,next_run);
            CREATE TABLE IF NOT EXISTS v2_mcp_servers (
                name TEXT NOT NULL, version TEXT NOT NULL,
                manifest TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN ('approved','quarantined')),
                created_at TEXT NOT NULL, PRIMARY KEY(name,version)
            );
            INSERT OR IGNORE INTO schema_migrations(version,applied_at) VALUES (2,strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """;
        command.ExecuteNonQuery();
        // Legacy API remains available; no migration of existing package data is performed implicitly.
    }

    public IReadOnlyList<Feed> Feeds()
    {
        using var c = db.Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT slug,protocol,mode,visibility,upstream,created_at FROM repositories ORDER BY slug";
        using var r = q.ExecuteReader(); var list = new List<Feed>();
        while (r.Read()) list.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),
            r.IsDBNull(4)?null:r.GetString(4),r.GetString(5)));
        return list;
    }

    public Feed? FeedBySlug(string slug)
    {
        using var c = db.Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT slug,protocol,mode,visibility,upstream,created_at FROM repositories WHERE slug=$slug";
        q.Parameters.AddWithValue("$slug", slug);
        using var r = q.ExecuteReader();
        return r.Read() ? new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),
            r.IsDBNull(4)?null:r.GetString(4),r.GetString(5)) : null;
    }

    public bool CreateFeed(string slug, string protocol, string mode, string visibility, string? upstream)
    {
        using var c = db.Open(); using var q = c.CreateCommand();
        q.CommandText = "INSERT OR IGNORE INTO repositories(slug,protocol,mode,visibility,upstream,created_at) VALUES($s,$p,$m,$v,$u,$at)";
        q.Parameters.AddWithValue("$s",slug);q.Parameters.AddWithValue("$p",protocol);
        q.Parameters.AddWithValue("$m",mode);q.Parameters.AddWithValue("$v",visibility);
        q.Parameters.AddWithValue("$u",(object?)upstream??DBNull.Value);
        q.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));
        return q.ExecuteNonQuery()==1;
    }

    public bool SetVisibility(string slug,string visibility)
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="UPDATE repositories SET visibility=$v WHERE slug=$s";
        q.Parameters.AddWithValue("$v",visibility);q.Parameters.AddWithValue("$s",slug);
        return q.ExecuteNonQuery()==1;
    }

    public StoredAsset? Asset(string feed,string kind,string name,string version,string filename)
    {
        using var c=db.Open(); using var q=c.CreateCommand();
        q.CommandText="""
            SELECT feed,kind,name,version,filename,digest,bytes,content_type,origin,state,created_at
            FROM v2_assets WHERE feed=$f AND kind=$k AND name=$n AND version=$v AND filename=$file
            """;
        q.Parameters.AddWithValue("$f",feed);q.Parameters.AddWithValue("$k",kind);
        q.Parameters.AddWithValue("$n",name);q.Parameters.AddWithValue("$v",version);
        q.Parameters.AddWithValue("$file",filename);
        using var r=q.ExecuteReader();return r.Read()?ReadAsset(r):null;
    }

    private static StoredAsset ReadAsset(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetString(2),
        r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt64(6),r.GetString(7),
        r.GetString(8),r.GetString(9),r.GetString(10));

    public IReadOnlyList<StoredAsset> Assets(string feed,string? kind=null,string? name=null)
    {
        using var c=db.Open(); using var q=c.CreateCommand();
        q.CommandText="""
            SELECT feed,kind,name,version,filename,digest,bytes,content_type,origin,state,created_at
            FROM v2_assets WHERE feed=$f AND ($k IS NULL OR kind=$k) AND ($n IS NULL OR name=$n)
            ORDER BY created_at DESC LIMIT 500
            """;
        q.Parameters.AddWithValue("$f",feed);q.Parameters.AddWithValue("$k",(object?)kind??DBNull.Value);
        q.Parameters.AddWithValue("$n",(object?)name??DBNull.Value);
        using var r=q.ExecuteReader();var result=new List<StoredAsset>();
        while(r.Read())result.Add(ReadAsset(r));return result;
    }

    public IReadOnlyList<StoredAsset> Search(string? query,string? kind,int limit)
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="""
            SELECT a.feed,a.kind,a.name,a.version,a.filename,a.digest,a.bytes,a.content_type,a.origin,a.state,a.created_at
            FROM v2_assets a JOIN repositories r ON r.slug=a.feed
            WHERE ($kind IS NULL OR a.kind=$kind)
              AND (a.name LIKE $query ESCAPE '\' OR a.filename LIKE $query ESCAPE '\')
            ORDER BY a.created_at DESC LIMIT $limit
            """;
        q.Parameters.AddWithValue("$kind",(object?)kind??DBNull.Value);
        q.Parameters.AddWithValue("$query","%"+(query??"").Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_")+"%");
        q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,100));
        using var r=q.ExecuteReader();var result=new List<StoredAsset>();
        while(r.Read())result.Add(ReadAsset(r));return result;
    }

    // Blob is committed first; one DB transaction makes a valid fully written release visible.
    // Duplicate identity is immutable. An unused CAS blob is recoverable by later reconciliation.
    public bool Publish(StoredAsset asset)
    {
        using var c=db.Open();using var tx=c.BeginTransaction();using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="INSERT OR IGNORE INTO v2_blobs(digest,bytes,created_at) VALUES($digest,$bytes,$created)";
        q.Parameters.AddWithValue("$digest",asset.Digest);q.Parameters.AddWithValue("$bytes",asset.Size);
        q.Parameters.AddWithValue("$created",asset.CreatedAt);q.ExecuteNonQuery();
        q.CommandText="""
            INSERT OR IGNORE INTO v2_assets(feed,kind,name,version,filename,digest,bytes,content_type,origin,state,created_at)
            VALUES($feed,$kind,$name,$version,$filename,$digest,$bytes,$content,$origin,$state,$created)
            """;
        q.Parameters.AddWithValue("$feed",asset.Feed);q.Parameters.AddWithValue("$kind",asset.Kind);
        q.Parameters.AddWithValue("$name",asset.Name);q.Parameters.AddWithValue("$version",asset.Version);
        q.Parameters.AddWithValue("$filename",asset.Filename);q.Parameters.AddWithValue("$content",asset.ContentType);
        q.Parameters.AddWithValue("$origin",asset.Origin);q.Parameters.AddWithValue("$state",asset.State);
        var inserted=q.ExecuteNonQuery()==1;
        if(inserted)tx.Commit();return inserted;
    }

    public bool PublishNpm(StoredAsset asset,string manifest)
    {
        using var c=db.Open();using var tx=c.BeginTransaction();using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="INSERT OR IGNORE INTO v2_blobs(digest,bytes,created_at) VALUES($digest,$bytes,$created)";
        q.Parameters.AddWithValue("$digest",asset.Digest);q.Parameters.AddWithValue("$bytes",asset.Size);
        q.Parameters.AddWithValue("$created",asset.CreatedAt);q.ExecuteNonQuery();
        q.CommandText="""
            INSERT OR IGNORE INTO v2_assets(feed,kind,name,version,filename,digest,bytes,content_type,origin,state,created_at)
            VALUES($feed,$kind,$name,$version,$filename,$digest,$bytes,$content,$origin,$state,$created)
            """;
        q.Parameters.AddWithValue("$feed",asset.Feed);q.Parameters.AddWithValue("$kind",asset.Kind);
        q.Parameters.AddWithValue("$name",asset.Name);q.Parameters.AddWithValue("$version",asset.Version);
        q.Parameters.AddWithValue("$filename",asset.Filename);q.Parameters.AddWithValue("$content",asset.ContentType);
        q.Parameters.AddWithValue("$origin",asset.Origin);q.Parameters.AddWithValue("$state",asset.State);
        if(q.ExecuteNonQuery()!=1)return false;
        q.CommandText="INSERT INTO v2_npm_versions(feed,name,version,manifest) VALUES($feed,$name,$version,$manifest)";
        q.Parameters.AddWithValue("$manifest",manifest);q.ExecuteNonQuery();
        tx.Commit();return true;
    }

    public IReadOnlyDictionary<string,string> NpmManifests(string feed,string name)
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="SELECT version,manifest FROM v2_npm_versions WHERE feed=$feed AND name=$name ORDER BY version";
        q.Parameters.AddWithValue("$feed",feed);q.Parameters.AddWithValue("$name",name);
        using var r=q.ExecuteReader();var result=new Dictionary<string,string>(StringComparer.Ordinal);
        while(r.Read())result[r.GetString(0)]=r.GetString(1);
        return result;
    }

    public bool SetAssetState(string feed,string kind,string name,string version,string filename,string state)
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="UPDATE v2_assets SET state=$state WHERE feed=$f AND kind=$k AND name=$n AND version=$v AND filename=$file";
        q.Parameters.AddWithValue("$state",state);q.Parameters.AddWithValue("$f",feed);
        q.Parameters.AddWithValue("$k",kind);q.Parameters.AddWithValue("$n",name);
        q.Parameters.AddWithValue("$v",version);q.Parameters.AddWithValue("$file",filename);
        return q.ExecuteNonQuery()==1;
    }

    public void Audit(string actor,string action,string target,string details="")
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="INSERT INTO v2_audit(at,actor,action,target,details) VALUES($at,$actor,$action,$target,$details)";
        q.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O"));q.Parameters.AddWithValue("$actor",actor);
        q.Parameters.AddWithValue("$action",action);q.Parameters.AddWithValue("$target",target);
        q.Parameters.AddWithValue("$details",details);q.ExecuteNonQuery();
    }

    public IReadOnlyList<AuditEntry> AuditEntries(int limit=100)
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="SELECT id,at,actor,action,target,details FROM v2_audit ORDER BY id DESC LIMIT $limit";
        q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,500));using var r=q.ExecuteReader();
        var list=new List<AuditEntry>();while(r.Read())list.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5)));
        return list;
    }

    public (string Id,string Secret) CreateToken(string name,string? feed,string scopes,DateTimeOffset? expiry)
    {
        var id=Guid.NewGuid().ToString("N");
        var token="pk_"+Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var hash=Hash(token);
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="""
            INSERT INTO v2_tokens(id,name,token_hash,feed,scopes,expires_at,created_at)
            VALUES($id,$name,$hash,$feed,$scopes,$expires,$created)
            """;
        q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$name",name);q.Parameters.AddWithValue("$hash",hash);
        q.Parameters.AddWithValue("$feed",(object?)feed??DBNull.Value);q.Parameters.AddWithValue("$scopes",scopes);
        q.Parameters.AddWithValue("$expires",expiry is null?DBNull.Value:expiry.Value.ToString("O"));
        q.Parameters.AddWithValue("$created",DateTimeOffset.UtcNow.ToString("O"));q.ExecuteNonQuery();
        return(id,token);
    }

    public static string Hash(string token)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public (string Id,string? Feed,string Scopes)? ResolveToken(string provided)
    {
        // Tokens have 256 bits of entropy; indexed SHA-256 is sufficient for constant-work lookup.
        if(provided.Length is < 35 or > 256)return null;
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="""
            SELECT id,feed,scopes FROM v2_tokens
            WHERE token_hash=$hash AND revoked_at IS NULL AND (expires_at IS NULL OR expires_at>$now)
            """;
        q.Parameters.AddWithValue("$hash",Hash(provided));q.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        using var r=q.ExecuteReader();return r.Read()?(r.GetString(0),r.IsDBNull(1)?null:r.GetString(1),r.GetString(2)):null;
    }

    public bool RevokeToken(string id)
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="UPDATE v2_tokens SET revoked_at=$now WHERE id=$id AND revoked_at IS NULL";
        q.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));q.Parameters.AddWithValue("$id",id);
        return q.ExecuteNonQuery()==1;
    }

    public bool AddMcp(string name,string version,string manifest,string state)
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="""
            INSERT OR IGNORE INTO v2_mcp_servers(name,version,manifest,state,created_at)
            VALUES($name,$version,$manifest,$state,$now)
            """;
        q.Parameters.AddWithValue("$name",name);q.Parameters.AddWithValue("$version",version);
        q.Parameters.AddWithValue("$manifest",manifest);q.Parameters.AddWithValue("$state",state);
        q.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));return q.ExecuteNonQuery()==1;
    }

    public IReadOnlyList<McpRecord> Mcp(string? name=null,bool includeQuarantined=false)
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="""
            SELECT name,version,manifest,state,created_at FROM v2_mcp_servers
            WHERE ($name IS NULL OR name=$name) AND ($admin=1 OR state='approved') ORDER BY name,version LIMIT 200
            """;
        q.Parameters.AddWithValue("$name",(object?)name??DBNull.Value);q.Parameters.AddWithValue("$admin",includeQuarantined?1:0);
        using var r=q.ExecuteReader();var list=new List<McpRecord>();while(r.Read())list.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4)));
        return list;
    }

    public bool ChangeMcpState(string name,string version,string state)
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="UPDATE v2_mcp_servers SET state=$state WHERE name=$name AND version=$version";
        q.Parameters.AddWithValue("$state",state);q.Parameters.AddWithValue("$name",name);
        q.Parameters.AddWithValue("$version",version);return q.ExecuteNonQuery()==1;
    }

    public long Enqueue(string type,string? repository,string payload)
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="""
            INSERT INTO v2_jobs(type,state,repository,payload,next_run,created_at)
            VALUES($type,'queued',$repo,$payload,$now,$now);
            SELECT last_insert_rowid();
            """;
        q.Parameters.AddWithValue("$type",type);q.Parameters.AddWithValue("$repo",(object?)repository??DBNull.Value);
        q.Parameters.AddWithValue("$payload",payload);q.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        return (long)(q.ExecuteScalar()??0L);
    }

    public bool IsReferencedDigest(string digest)
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="SELECT 1 FROM v2_assets WHERE digest=$digest LIMIT 1";
        q.Parameters.AddWithValue("$digest",digest);
        return q.ExecuteScalar() is not null;
    }

    public void RecoverJobs()
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="UPDATE v2_jobs SET state='queued' WHERE state='running'";
        q.ExecuteNonQuery();
    }

    public JobEntry? ClaimJob()
    {
        using var c=db.Open();using var tx=c.BeginTransaction();using var q=c.CreateCommand();q.Transaction=tx;
        q.CommandText="""
            SELECT id,type,state,repository,payload,attempts,next_run,error FROM v2_jobs
            WHERE state='queued' AND next_run<=$now ORDER BY id LIMIT 1
            """;
        q.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));
        JobEntry? job=null;
        using(var r=q.ExecuteReader())
            if(r.Read())job=new(r.GetInt64(0),r.GetString(1),r.GetString(2),
                r.IsDBNull(3)?null:r.GetString(3),r.GetString(4),r.GetInt32(5),r.GetString(6),
                r.IsDBNull(7)?null:r.GetString(7));
        if(job is null)return null;
        q.CommandText="UPDATE v2_jobs SET state='running',attempts=attempts+1 WHERE id=$id AND state='queued'";
        q.Parameters.AddWithValue("$id",job.Id);if(q.ExecuteNonQuery()!=1)return null;
        tx.Commit();return job;
    }

    public void FinishJob(long id,string? error)
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="""
            UPDATE v2_jobs SET state=CASE WHEN $err IS NULL THEN 'completed' ELSE 'failed' END,
            error=$err WHERE id=$id
            """;
        q.Parameters.AddWithValue("$err",(object?)error??DBNull.Value);q.Parameters.AddWithValue("$id",id);
        q.ExecuteNonQuery();
    }

    public IReadOnlyList<JobEntry> Jobs(int limit=100)
    {
        using var c=db.Open();using var q=c.CreateCommand();
        q.CommandText="SELECT id,type,state,repository,payload,attempts,next_run,error FROM v2_jobs ORDER BY id DESC LIMIT $limit";
        q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,500));using var r=q.ExecuteReader();var list=new List<JobEntry>();
        while(r.Read())list.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.GetString(4),r.GetInt32(5),r.GetString(6),r.IsDBNull(7)?null:r.GetString(7)));
        return list;
    }
}
