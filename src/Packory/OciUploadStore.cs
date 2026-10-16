using System.Collections.Concurrent;

namespace Packory;

/// <summary>
/// Single-process OCI chunk upload sessions. Bytes are staged on disk (never held in
/// a global upload buffer); abandoned sessions expire. Multi-node sharing is unsupported.
/// </summary>
public sealed class OciUploadStore
{
    public sealed class Ticket(string repository, string path)
    {
        public string Repository { get; } = repository;
        public string Path { get; } = path;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset LastTouched { get; set; } = DateTimeOffset.UtcNow;
    }

    private readonly ConcurrentDictionary<string, Ticket> _sessions = new(StringComparer.Ordinal);
    private readonly string _directory;

    public OciUploadStore(Settings settings)
    {
        _directory = System.IO.Path.Combine(System.IO.Path.GetFullPath(settings.DataDirectory), "oci-staging");
        Directory.CreateDirectory(_directory);
        // Any previous process's sessions are invalid after a restart.
        foreach (var file in Directory.EnumerateFiles(_directory, "*.tmp", SearchOption.TopDirectoryOnly))
            File.Delete(file);
    }

    public string Begin(string repository)
    {
        CleanupExpired();
        var id = Guid.NewGuid().ToString("N");
        var path = System.IO.Path.Combine(_directory, id + ".tmp");
        using (File.Create(path)) { }
        if (!_sessions.TryAdd(id, new Ticket(repository, path)))
            throw new IOException("OCI upload UUID collision");
        return id;
    }

    public bool TryGet(string repository, string id, out Ticket? ticket)
    {
        ticket = null;
        if (id.Length != 32 || !id.All(Uri.IsHexDigit) ||
            !_sessions.TryGetValue(id, out var value) ||
            value.Repository != repository ||
            value.LastTouched < DateTimeOffset.UtcNow.AddMinutes(-30))
            return false;
        ticket = value;
        return true;
    }

    public void End(string id)
    {
        if (_sessions.TryRemove(id, out var session) && File.Exists(session.Path))
            File.Delete(session.Path);
    }

    private void CleanupExpired()
    {
        foreach (var entry in _sessions)
            if (entry.Value.LastTouched < DateTimeOffset.UtcNow.AddMinutes(-30))
                End(entry.Key);
    }
}
