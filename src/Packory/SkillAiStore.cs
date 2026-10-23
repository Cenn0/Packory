using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Packory;

/// <summary>Durable single-writer AI jobs, configuration revisions, cost admission and audit.
/// The existing Packory writer lease remains mandatory; this is not multi-writer fencing.</summary>
public sealed class SkillAiStore(Settings settings, SkillAiDeployment deployment, SkillAiSecrets secrets)
{
    public sealed record StoredConfiguration(SkillAiConfiguration Configuration, string? Ciphertext);
    public sealed record Job(string Id, string Name, string Version, string Sha256, string StagingKey,
        string Kind, long Revision, string Snapshot, string? Ciphertext, string State,
        long CreatedAt, long? Deadline, string? ReportJson, int? RiskScore,
        string? Recommendation, string? ErrorCode, int Requests);
    public sealed record Usage(string Month, long ReservedMicroUsd, long InputTokens, long OutputTokens, long Requests, long RequestsWithMissingUsage);
    private readonly object sync = new();
    internal SemaphoreSlim PolicyGate { get; } = new(1, 1);
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private const string Columns = "id,name,version,sha256,staging_key,kind,revision,snapshot_json,secret_ciphertext,state,created_at,deadline,report_json,risk_score,recommendation,error_code,requests";

    private DbConnection Open()
    {
        DbConnection connection;
        if (settings.MetadataProvider == "postgresql") connection = new NpgsqlConnection(settings.PostgresConnectionString);
        else connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetFullPath(settings.DataDirectory), "packory.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared
        }.ToString());
        connection.Open();
        if (connection is SqliteConnection)
        { using var pragma = Command(connection, "PRAGMA busy_timeout=5000;"); pragma.ExecuteNonQuery(); }
        return connection;
    }
    private static DbCommand Command(DbConnection db, string sql, params (string Name, object? Value)[] values)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in values)
        { var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value ?? DBNull.Value; cmd.Parameters.Add(p); }
        return cmd;
    }
    public void Initialize()
    {
        lock (sync)
        {
            using var db = Open();
            using var lease = db is NpgsqlConnection postgres ? PostgresSchemaLock.Acquire(postgres) : null;
            using var cmd = Command(db, """
                CREATE TABLE IF NOT EXISTS skill_ai_configuration (
                    id INTEGER PRIMARY KEY, revision BIGINT NOT NULL, config_json TEXT NOT NULL,
                    secret_ciphertext TEXT, updated_at BIGINT NOT NULL);
                CREATE TABLE IF NOT EXISTS skill_ai_jobs (
                    id TEXT PRIMARY KEY, dedupe_key TEXT NOT NULL UNIQUE,
                    name TEXT NOT NULL, version TEXT NOT NULL, sha256 TEXT NOT NULL, staging_key TEXT NOT NULL,
                    kind TEXT NOT NULL, revision BIGINT NOT NULL, snapshot_json TEXT NOT NULL, secret_ciphertext TEXT,
                    state TEXT NOT NULL, created_at BIGINT NOT NULL, deadline BIGINT,
                    report_json TEXT, risk_score INTEGER, recommendation TEXT, error_code TEXT,
                    requests INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS skill_ai_jobs_pending ON skill_ai_jobs(state,created_at);
                CREATE INDEX IF NOT EXISTS skill_ai_jobs_artifact ON skill_ai_jobs(name,version,revision,created_at);
                CREATE TABLE IF NOT EXISTS skill_ai_requests (
                    id TEXT PRIMARY KEY, job_id TEXT NOT NULL, month TEXT NOT NULL,
                    reserved_micro_usd BIGINT NOT NULL, input_tokens BIGINT, output_tokens BIGINT,
                    status TEXT NOT NULL, created_at BIGINT NOT NULL);
                CREATE INDEX IF NOT EXISTS skill_ai_requests_month ON skill_ai_requests(month);
                CREATE TABLE IF NOT EXISTS skill_ai_audit (
                    id TEXT PRIMARY KEY, action TEXT NOT NULL, revision BIGINT NOT NULL,
                    job_id TEXT, actor TEXT NOT NULL, reason TEXT NOT NULL, created_at BIGINT NOT NULL);
                """);
            cmd.ExecuteNonQuery();
            using var seed = Command(db, """
                INSERT INTO skill_ai_configuration(id,revision,config_json,updated_at)
                VALUES(1,0,@json,@now) ON CONFLICT(id) DO NOTHING
                """, ("json", JsonSerializer.Serialize(new SkillAiConfiguration(), SkillAiJson.Options)), ("now", Now));
            seed.ExecuteNonQuery();
        }
    }
    private static StoredConfiguration ReadConfiguration(DbConnection db)
    {
        using var cmd = Command(db, "SELECT config_json,secret_ciphertext FROM skill_ai_configuration WHERE id=1");
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("AI configuration is not initialized.");
        return new(SkillAiJson.Read<SkillAiConfiguration>(reader.GetString(0), 16384),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }
    public StoredConfiguration Configuration()
    { lock (sync) { using var db = Open(); return ReadConfiguration(db); } }

    // Caller holds PolicyGate when changing policy, including disable/credential rotation.
    public StoredConfiguration Save(SkillAiSettingsChange change, string actor)
    {
        if (change.Configuration is null || change.Reason?.Length is not (>= 3 and <= 512) ||
            (change.ClearApiKey && change.ApiKey is not null)) throw new InvalidDataException("Invalid AI settings change.");
        lock (sync)
        {
            using var db = Open();
            var previous = ReadConfiguration(db);
            if (change.Configuration.Revision != previous.Configuration.Revision)
                throw new InvalidOperationException("AI configuration changed; refresh before saving.");
            var cipher = change.ClearApiKey ? null : previous.Ciphertext;
            if (change.Configuration.EndpointId != previous.Configuration.EndpointId) cipher = null;
            if (change.ApiKey is not null) cipher = secrets.Protect(change.ApiKey, change.Configuration.EndpointId);
            var config = change.Configuration with { Revision = checked(previous.Configuration.Revision + 1) };
            config.Validate(deployment, cipher is not null);
            if (config.Enabled && cipher is not null) cipher = secrets.Protect(secrets.Unprotect(cipher, config.EndpointId), config.EndpointId);
            using var tx = db.BeginTransaction();
            using var write = Command(db, """
                UPDATE skill_ai_configuration SET revision=@revision,config_json=@json,secret_ciphertext=@secret,updated_at=@now
                WHERE id=1 AND revision=@previous
                """, ("revision", config.Revision), ("json", JsonSerializer.Serialize(config, SkillAiJson.Options)),
                ("secret", cipher), ("now", Now), ("previous", previous.Configuration.Revision));
            write.Transaction = tx;
            if (write.ExecuteNonQuery() != 1) throw new InvalidOperationException("AI configuration changed.");
            using var cancel = Command(db, """
                UPDATE skill_ai_jobs SET state='CANCELLED',error_code='configuration_changed'
                WHERE state IN ('QUEUED','RUNNING')
                """);
            cancel.Transaction = tx; cancel.ExecuteNonQuery();
            Audit(db, tx, "configuration_changed", config.Revision, null, actor, change.Reason);
            tx.Commit();
            return new(config, cipher);
        }
    }
    private static void Audit(DbConnection db, DbTransaction? tx, string action, long revision,
        string? job, string actor, string reason)
    {
        using var cmd = Command(db, """
            INSERT INTO skill_ai_audit(id,action,revision,job_id,actor,reason,created_at)
            VALUES(@id,@action,@revision,@job,@actor,@reason,@now)
            """, ("id", Guid.NewGuid().ToString("N")), ("action", action), ("revision", revision),
            ("job", job), ("actor", actor), ("reason", reason), ("now", Now));
        cmd.Transaction = tx; cmd.ExecuteNonQuery();
    }
    private static Job ReadJob(DbDataReader r) => new(
        r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetInt64(6),
        r.GetString(7),r.IsDBNull(8)?null:r.GetString(8),r.GetString(9),r.GetInt64(10),
        r.IsDBNull(11)?null:r.GetInt64(11),r.IsDBNull(12)?null:r.GetString(12),
        r.IsDBNull(13)?null:r.GetInt32(13),r.IsDBNull(14)?null:r.GetString(14),
        r.IsDBNull(15)?null:r.GetString(15),r.GetInt32(16));
    private static Job? Find(DbConnection db, string id)
    {
        using var cmd = Command(db, $"SELECT {Columns} FROM skill_ai_jobs WHERE id=@id", ("id", id));
        using var r = cmd.ExecuteReader(); return r.Read() ? ReadJob(r) : null;
    }
    public Job? Get(string id) { lock (sync) { using var db = Open(); return Find(db, id); } }
    public IReadOnlyList<Job> List(string? name = null, string? version = null, bool pending = false, int limit = 50)
    {
        lock (sync)
        {
            using var db = Open();
            var where = pending ? "WHERE state IN ('QUEUED','RUNNING')" : name is null ? "" : "WHERE name=@name AND version=@version";
            using var cmd = Command(db, $"SELECT {Columns} FROM skill_ai_jobs {where} ORDER BY created_at DESC,id DESC LIMIT @limit",
                ("limit", Math.Clamp(limit, 1, 100)), ("name", name), ("version", version));
            using var r = cmd.ExecuteReader(); var jobs = new List<Job>();
            while (r.Read()) jobs.Add(ReadJob(r)); return jobs;
        }
    }
    public Job Queue(SkillSecurityStore.Revision? skill, string requestId, string actor, string reason, bool automatic = false)
    {
        if (reason?.Length is not (>= 3 and <= 512) || (!automatic && !Guid.TryParseExact(requestId, "N", out _)))
            throw new InvalidDataException("An idempotency ID and review reason are required.");
        lock (sync)
        {
            using var db = Open(); var stored = ReadConfiguration(db); var config = stored.Configuration;
            config.Validate(deployment, stored.Ciphertext is not null);
            if (!config.Enabled) throw new InvalidOperationException("AI assessment is disabled.");
            if (skill is not null && skill.State is not ("NEEDS_REVIEW" or "QUARANTINED" or "APPROVED" or "REVOKED" or "PUBLISHING"))
                throw new InvalidOperationException("A completed static scan is required first.");
            var dedupe = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{skill?.Name}\n{skill?.Version}\n{skill?.Sha256}\n{config.Revision}\n{(automatic ? "automatic" : requestId)}")));
            using (var existing = Command(db, $"SELECT {Columns} FROM skill_ai_jobs WHERE dedupe_key=@key", ("key", dedupe)))
            { using var r = existing.ExecuteReader(); if (r.Read()) return ReadJob(r); }
            using (var count = Command(db, "SELECT COUNT(*) FROM skill_ai_jobs WHERE state IN ('QUEUED','RUNNING')"))
                if (Convert.ToInt64(count.ExecuteScalar()) >= 100) throw new InvalidOperationException("AI queue is full.");
            var id = Guid.NewGuid().ToString("N");
            using var timestamp = Command(db, "SELECT COALESCE(MAX(created_at),0) FROM skill_ai_jobs");
            var createdAt = Math.Max(Now, Convert.ToInt64(timestamp.ExecuteScalar()) + 1);
            using var tx = db.BeginTransaction();
            using var cmd = Command(db, """
                INSERT INTO skill_ai_jobs(id,dedupe_key,name,version,sha256,staging_key,kind,revision,snapshot_json,
                    secret_ciphertext,state,created_at)
                VALUES(@id,@dedupe,@name,@version,@sha,@staging,@kind,@revision,@snapshot,@secret,'QUEUED',@now)
                """, ("id", id), ("dedupe", dedupe), ("name", skill?.Name ?? ""), ("version", skill?.Version ?? ""),
                ("sha", skill?.Sha256 ?? ""), ("staging", skill?.StagingKey ?? ""),
                ("kind", skill is null ? "probe" : "assessment"), ("revision", config.Revision),
                ("snapshot", JsonSerializer.Serialize(config, SkillAiJson.Options)), ("secret", stored.Ciphertext), ("now", createdAt));
            cmd.Transaction = tx; cmd.ExecuteNonQuery();
            Audit(db, tx, automatic ? "automatic_queued" : "manual_queued", config.Revision, id, actor, reason);
            tx.Commit(); return Find(db, id)!;
        }
    }
    public bool Start(string id)
    {
        lock (sync)
        {
            using var db = Open(); var job = Find(db, id); var cfg = ReadConfiguration(db).Configuration;
            if (job is null || job.State != "QUEUED" || !cfg.Enabled || job.Revision != cfg.Revision) return false;
            using (var count = Command(db, "SELECT COUNT(*) FROM skill_ai_jobs WHERE state='RUNNING'"))
                if (Convert.ToInt64(count.ExecuteScalar()) >= cfg.MaxConcurrentScans) return false;
            using var cmd = Command(db, "UPDATE skill_ai_jobs SET state='RUNNING',deadline=@deadline WHERE id=@id AND state='QUEUED'",
                ("deadline", Now + cfg.TimeoutSeconds * 1000L), ("id", id));
            return cmd.ExecuteNonQuery() == 1;
        }
    }
    public void Finish(string id, string state, string? error = null, string? report = null, int? risk = null, string? recommendation = null)
    {
        if (state is not ("COMPLETED" or "BLOCKED" or "FAILED" or "TIMED_OUT" or "CANCELLED" or "BUDGET_EXCEEDED"))
            throw new ArgumentOutOfRangeException(nameof(state));
        lock (sync)
        {
            using var db = Open(); var job = Find(db, id); if (job is null) return;
            using var tx = db.BeginTransaction();
            using var cmd = Command(db, """
                UPDATE skill_ai_jobs SET state=@state,error_code=@error,report_json=@report,risk_score=@risk,recommendation=@recommendation
                WHERE id=@id AND state IN ('QUEUED','RUNNING')
                """, ("state", state), ("error", error), ("report", report), ("risk", risk), ("recommendation", recommendation), ("id", id));
            cmd.Transaction = tx;
            if (cmd.ExecuteNonQuery() == 1) Audit(db, tx, state.ToLowerInvariant(), job.Revision, id, "system", error ?? "Assessment completed; no automatic publication");
            tx.Commit();
        }
    }
    public bool Cancel(string id, string actor, string reason)
    {
        if (reason?.Length is not (>= 3 and <= 512)) throw new InvalidDataException("A cancellation reason is required.");
        lock (sync)
        {
            using var db = Open(); var job = Find(db, id); if (job is null) return false;
            using var tx = db.BeginTransaction();
            using var cmd = Command(db, "UPDATE skill_ai_jobs SET state='CANCELLED',error_code='operator_cancelled' WHERE id=@id AND state IN ('QUEUED','RUNNING')", ("id", id));
            cmd.Transaction = tx;
            if (cmd.ExecuteNonQuery() != 1) return false;
            Audit(db, tx, "cancelled", job.Revision, id, actor, reason); tx.Commit(); return true;
        }
    }
    public bool CanApprove(string name, string version, string digest)
    {
        var cfg = Configuration().Configuration;
        if (!cfg.RequireForApproval) return true;
        var latest = List(name, version, limit: 1).FirstOrDefault();
        return cfg.Enabled && latest is { State: "COMPLETED" } && latest.Revision == cfg.Revision && latest.Sha256 == digest;
    }
    public Usage Budget()
    {
        lock (sync) { using var db = Open(); return Budget(db); }
    }
    private static Usage Budget(DbConnection db)
    {
        var month = DateTimeOffset.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        using var cmd = Command(db, """
            SELECT COALESCE(SUM(reserved_micro_usd),0),COALESCE(SUM(input_tokens),0),COALESCE(SUM(output_tokens),0),COUNT(*),COALESCE(SUM(CASE WHEN input_tokens IS NULL OR output_tokens IS NULL THEN 1 ELSE 0 END),0)
            FROM skill_ai_requests WHERE month=@month
            """, ("month", month));
        using var r = cmd.ExecuteReader(); r.Read();
        return new(month, Convert.ToInt64(r.GetValue(0)), Convert.ToInt64(r.GetValue(1)), Convert.ToInt64(r.GetValue(2)), Convert.ToInt64(r.GetValue(3)), Convert.ToInt64(r.GetValue(4)));
    }
    /// <summary>Reserve before egress. Reservations are NOT refunded on crashes or ambiguous failures.
    /// This bounds configured-rate admission, not a vendor's bill or undocumented tokenizer.</summary>
    public bool AdmitRequest(string jobId, string requestId, out string error)
    {
        lock (sync)
        {
            error = "job_unavailable"; using var db = Open(); var job = Find(db, jobId);
            var cfg = ReadConfiguration(db).Configuration;
            if (job is null || job.State != "RUNNING" || (job.Deadline is null || job.Deadline <= Now) || !cfg.Enabled || job.Revision != cfg.Revision) return false;
            if (job.Requests >= cfg.MaxRequestsPerScan) { error = "request_limit"; return false; }
            var usage = Budget(db); var reserved = cfg.ReservationMicroUsd;
            if (reserved > 0 && usage.ReservedMicroUsd + reserved > cfg.MonthlyBudgetMicroUsd)
            { error = "budget_exceeded"; return false; }
            using var tx = db.BeginTransaction();
            using var cmd = Command(db, """
                INSERT INTO skill_ai_requests(id,job_id,month,reserved_micro_usd,status,created_at)
                VALUES(@id,@job,@month,@reserve,'ADMITTED',@now) ON CONFLICT(id) DO NOTHING
                """, ("id", requestId), ("job", jobId), ("month", usage.Month), ("reserve", reserved), ("now", Now));
            cmd.Transaction = tx;
            if (cmd.ExecuteNonQuery() != 1) { error = "duplicate_request"; return false; }
            using var update = Command(db, "UPDATE skill_ai_jobs SET requests=requests+1 WHERE id=@id", ("id", jobId));
            update.Transaction = tx; update.ExecuteNonQuery(); tx.Commit(); error = ""; return true;
        }
    }
    public bool HasCompletedInference(string jobId)
    {
        lock (sync)
        {
            using var db = Open();
            using var cmd = Command(db, "SELECT COUNT(*) FROM skill_ai_requests WHERE job_id=@job AND status='COMPLETED'", ("job", jobId));
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
    }
    public void RecordUsage(string requestId, string status, long? inputTokens = null, long? outputTokens = null)
    {
        lock (sync)
        {
            using var db = Open(); using var cmd = Command(db, """
                UPDATE skill_ai_requests SET status=@status,input_tokens=@input,output_tokens=@output
                WHERE id=@id AND status='ADMITTED'
                """, ("status", status), ("input", inputTokens), ("output", outputTokens), ("id", requestId));
            cmd.ExecuteNonQuery();
        }
    }
    public object[] History()
    {
        lock (sync)
        {
            using var db = Open(); using var cmd = Command(db, "SELECT action,revision,job_id,actor,reason,created_at FROM skill_ai_audit ORDER BY created_at DESC,id DESC LIMIT 100");
            using var r = cmd.ExecuteReader(); var results = new List<object>();
            while (r.Read()) results.Add(new { action = r.GetString(0), revision = r.GetInt64(1), jobId = r.IsDBNull(2) ? null : r.GetString(2), actor = r.GetString(3), reason = r.GetString(4), at = r.GetInt64(5) });
            return [.. results];
        }
    }
    public static object Summary(Job job) => new
    {
        job.Id,job.Name,job.Version,job.Sha256,job.Kind,job.Revision,job.State,job.CreatedAt,job.Deadline,
        job.RiskScore,job.Recommendation,job.ErrorCode,job.Requests,
        scannerCommit = SkillAiDeployment.ScannerCommit, profile = SkillAiDeployment.Profile,
        configuration = SkillAiJson.Read<SkillAiConfiguration>(job.Snapshot, 16384)
    };
}
