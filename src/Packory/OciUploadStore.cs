using System.Collections.Concurrent;
using System.Text.Json;

namespace Packory;

/// <summary>
/// Single-writer OCI staging sessions persisted alongside their bounded upload bytes.
/// A process restart can resume a session within its TTL on the SAME data directory.
/// This does not enable shared sessions, writer failover, active-active or cluster fencing.
/// </summary>
public sealed class OciUploadStore
{
    private sealed record UploadSnapshot(string Repository, DateTimeOffset LastTouched);

    public sealed class Ticket(string repository, string path, DateTimeOffset touched)
    {
        public string Repository { get; } = repository;
        public string Path { get; } = path;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset LastTouched { get; internal set; } = touched;
    }

    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, Ticket> _sessions = new(StringComparer.Ordinal);
    private readonly string _directory;
    private readonly long _maximumBytes;

    public OciUploadStore(Settings settings)
    {
        _directory = System.IO.Path.Combine(System.IO.Path.GetFullPath(settings.DataDirectory), "oci-staging");
        _maximumBytes = settings.MaxArtifactBytes;
        Directory.CreateDirectory(_directory);
        // Older .tmp files have no trusted repository binding: leave for
        // operator retention handling instead of inventing an upload identity.
        foreach (var metadata in Directory.EnumerateFiles(_directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            var id = System.IO.Path.GetFileNameWithoutExtension(metadata);
            if (!ValidId(id)) continue;
            var payload = TempPath(id);
            if (!SafeRegularFile(metadata) || !SafeRegularFile(payload)) continue;
            UploadSnapshot? snapshot;
            try { snapshot = JsonSerializer.Deserialize<UploadSnapshot>(File.ReadAllText(metadata)); }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            { continue; }
            if (snapshot is null || !OciEndpoints.ValidRepository(snapshot.Repository) ||
                snapshot.LastTouched > DateTimeOffset.UtcNow.AddMinutes(1))
                continue;
            if (snapshot.LastTouched < DateTimeOffset.UtcNow - SessionTtl)
            {
                // Only paired, expired files under this exact private directory
                // are eligible for deletion; other orphaned files are preserved.
                File.Delete(metadata);
                File.Delete(payload);
                continue;
            }
            if (new FileInfo(payload).Length > _maximumBytes) continue;
            _sessions.TryAdd(id, new Ticket(snapshot.Repository, payload, snapshot.LastTouched));
        }
    }

    private static bool ValidId(string id) => id.Length == 32 &&
        id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool SafeRegularFile(string path) => File.Exists(path) &&
        !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private string TempPath(string id) => System.IO.Path.Combine(_directory, id + ".tmp");
    private string SnapshotPath(string id) => System.IO.Path.Combine(_directory, id + ".json");

    private void Persist(string id, Ticket ticket)
    {
        var destination = SnapshotPath(id);
        var temporary = System.IO.Path.Combine(_directory, id + "." + Guid.NewGuid().ToString("N") + ".new");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream,
                    new UploadSnapshot(ticket.Repository, ticket.LastTouched));
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
        CleanupExpired();
        var id = Guid.NewGuid().ToString("N");
        var path = TempPath(id);
        using (File.Create(path)) { }
        var ticket = new Ticket(repository, path, DateTimeOffset.UtcNow);
        try
        {
            Persist(id, ticket);
            if (!_sessions.TryAdd(id, ticket))
                throw new IOException("OCI upload UUID collision");
            return id;
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(SnapshotPath(id))) File.Delete(SnapshotPath(id));
            throw;
        }
    }

    public bool TryGet(string repository, string id, out Ticket? ticket)
    {
        ticket = null;
        if (!ValidId(id) || !_sessions.TryGetValue(id, out var found) ||
            found.Repository != repository ||
            found.LastTouched < DateTimeOffset.UtcNow - SessionTtl ||
            !SafeRegularFile(found.Path) || new FileInfo(found.Path).Length > _maximumBytes)
            return false;
        ticket = found;
        return true;
    }

    /// <summary>Call only under the upload ticket Gate after bytes are flushed.</summary>
    public void Touch(Ticket ticket)
    {
        var id = System.IO.Path.GetFileNameWithoutExtension(ticket.Path);
        if (!ValidId(id) || !_sessions.TryGetValue(id, out var expected) ||
            !ReferenceEquals(expected, ticket))
            throw new InvalidOperationException("Unknown OCI upload session");
        ticket.LastTouched = DateTimeOffset.UtcNow;
        Persist(id, ticket);
    }

    public void End(string id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            if (File.Exists(session.Path)) File.Delete(session.Path);
            var metadata = SnapshotPath(id);
            if (File.Exists(metadata)) File.Delete(metadata);
        }
    }

    private void CleanupExpired()
    {
        foreach (var entry in _sessions)
            if (entry.Value.LastTouched < DateTimeOffset.UtcNow - SessionTtl)
                End(entry.Key);
    }
}
