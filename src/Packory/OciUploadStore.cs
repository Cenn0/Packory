using System.Collections.Concurrent;
using System.Text.Json;

namespace Packory;

/// <summary>
/// Durable, bounded single-writer OCI sessions. Only an atomically persisted
/// committed offset may be advertised or resumed after a process restart.
/// This is not shared storage, multi-writer fencing, or a power-loss guarantee.
/// </summary>
public sealed class OciUploadStore
{
    private sealed record UploadSnapshot(string Repository, DateTimeOffset LastTouched,
        long? CommittedLength = null, int Schema = 1);

    public sealed class Ticket(string repository, string path, DateTimeOffset touched, long committedLength = 0)
    {
        public string Repository { get; } = repository;
        public string Path { get; } = path;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset LastTouched { get; internal set; } = touched;
        public long CommittedLength { get; internal set; } = committedLength;
        internal bool Unusable { get; set; }
    }

    public const int MaximumSessions = 1024;
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, Ticket> _sessions = new(StringComparer.Ordinal);
    private readonly object _creationGate = new();
    private readonly string _directory;
    private readonly long _maximumBytes;

    public OciUploadStore(Settings settings)
    {
        _directory = System.IO.Path.Combine(System.IO.Path.GetFullPath(settings.DataDirectory), "oci-staging");
        _maximumBytes = settings.MaxArtifactBytes;
        Directory.CreateDirectory(_directory);
        if (File.GetAttributes(_directory).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("OCI staging directory must not be a symlink");
        foreach (var metadata in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var id = System.IO.Path.GetFileNameWithoutExtension(metadata);
            if (!ValidId(id)) continue;
            var payload = TempPath(id);
            if (!SafeRegularFile(metadata) || !SafeRegularFile(payload) || new FileInfo(metadata).Length > 4096)
                continue;
            UploadSnapshot? snapshot;
            try { snapshot = JsonSerializer.Deserialize<UploadSnapshot>(File.ReadAllText(metadata)); }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            { continue; }
            // Old sessions have no authoritative acknowledged offset. Do not
            // invent one from potentially interrupted append bytes; preserve them.
            if (snapshot is null || snapshot.Schema != 2 || snapshot.CommittedLength is not { } committed ||
                committed < 0 || committed > _maximumBytes ||
                string.IsNullOrEmpty(snapshot.Repository) || !OciEndpoints.ValidRepository(snapshot.Repository) ||
                snapshot.LastTouched == default ||
                snapshot.LastTouched > DateTimeOffset.UtcNow.AddMinutes(1)) continue;
            var length = new FileInfo(payload).Length;
            if (length < committed || length > _maximumBytes) continue;
            if (snapshot.LastTouched < DateTimeOffset.UtcNow - SessionTtl)
            {
                File.Delete(metadata);
                File.Delete(payload);
                continue;
            }
            if (_sessions.Count >= MaximumSessions) continue;
            if (length > committed)
            {
                using var file = new FileStream(payload, FileMode.Open, FileAccess.Write, FileShare.None);
                file.SetLength(committed);
                file.Flush(flushToDisk: true);
            }
            _sessions.TryAdd(id, new Ticket(snapshot.Repository, payload, snapshot.LastTouched, committed));
        }
    }

    private static bool ValidId(string id) => id.Length == 32 &&
        id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool SafeRegularFile(string path) => File.Exists(path) &&
        !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private string TempPath(string id) => System.IO.Path.Combine(_directory, id + ".tmp");
    private string SnapshotPath(string id) => System.IO.Path.Combine(_directory, id + ".json");

    private void Persist(string id, UploadSnapshot snapshot)
    {
        var destination = SnapshotPath(id);
        var temporary = System.IO.Path.Combine(_directory, id + "." + Guid.NewGuid().ToString("N") + ".new");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, snapshot);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string Begin(string repository)
    {
        if (!OciEndpoints.ValidRepository(repository))
            throw new ArgumentException("Invalid OCI repository", nameof(repository));
        lock (_creationGate)
        {
            CleanupExpired();
            if (_sessions.Count >= MaximumSessions) throw new OciUploadCapacityException();
            var id = Guid.NewGuid().ToString("N");
            var path = TempPath(id);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Flush(flushToDisk: true);
            var touched = DateTimeOffset.UtcNow;
            var ticket = new Ticket(repository, path, touched);
            try
            {
                Persist(id, new UploadSnapshot(repository, touched, 0, 2));
                if (!_sessions.TryAdd(id, ticket)) throw new IOException("OCI upload UUID collision");
                return id;
            }
            catch
            {
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(SnapshotPath(id))) File.Delete(SnapshotPath(id));
                throw;
            }
        }
    }

    public bool IsCurrent(string repository, string id, Ticket ticket) =>
        ValidId(id) && _sessions.TryGetValue(id, out var found) && ReferenceEquals(found, ticket) &&
        !ticket.Unusable && ticket.Repository == repository && SafeRegularFile(ticket.Path);

    public bool TryGet(string repository, string id, out Ticket? ticket)
    {
        ticket = null;
        if (!ValidId(id) || !_sessions.TryGetValue(id, out var found) ||
            !IsCurrent(repository, id, found) ||
            found.LastTouched < DateTimeOffset.UtcNow - SessionTtl ||
            new FileInfo(found.Path).Length > _maximumBytes) return false;
        ticket = found;
        return true;
    }

    /// <summary>Caller holds Gate; bytes must be flushed and closed before committing.</summary>
    public void Touch(Ticket ticket)
    {
        var id = System.IO.Path.GetFileNameWithoutExtension(ticket.Path);
        if (!IsCurrent(ticket.Repository, id, ticket))
            throw new InvalidOperationException("Unknown OCI upload session");
        var length = new FileInfo(ticket.Path).Length;
        if (length < ticket.CommittedLength || length > _maximumBytes)
            throw new InvalidDataException("Invalid OCI committed offset");
        var touched = DateTimeOffset.UtcNow;
        Persist(id, new UploadSnapshot(ticket.Repository, touched, length, 2));
        // Publish in-memory state only AFTER the persisted commit succeeds.
        ticket.CommittedLength = length;
        ticket.LastTouched = touched;
    }

    public void End(string id)
    {
        if (!_sessions.TryGetValue(id, out var session)) return;
        session.Gate.Wait();
        try { EndLocked(id, session); }
        finally { session.Gate.Release(); }
    }

    /// <summary>Caller holds this exact ticket's Gate; never delete another ticket.</summary>
    public void EndLocked(string id, Ticket session)
    {
        if (!_sessions.TryGetValue(id, out var current) || !ReferenceEquals(current, session)) return;
        session.Unusable = true;
        // Remove the durable identity first. A crash may leave unbound bytes,
        // never a resumable identity pointing at a partially deleted payload.
        var metadata = SnapshotPath(id);
        if (File.Exists(metadata)) File.Delete(metadata);
        if (File.Exists(session.Path)) File.Delete(session.Path);
        _sessions.TryRemove(id, out _);
    }

    private void CleanupExpired()
    {
        foreach (var entry in _sessions)
        {
            var ticket = entry.Value;
            if (ticket.LastTouched >= DateTimeOffset.UtcNow - SessionTtl || !ticket.Gate.Wait(0)) continue;
            try
            {
                // A slow active upload is never deleted while it owns the gate.
                if (ticket.LastTouched < DateTimeOffset.UtcNow - SessionTtl)
                    EndLocked(entry.Key, ticket);
            }
            finally { ticket.Gate.Release(); }
        }
    }
}

public sealed class OciUploadCapacityException : Exception { }
