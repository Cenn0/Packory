using System.Globalization;

namespace Packory;

/// <summary>Single-session chunk transaction; caller must own ticket.Gate.</summary>
public static class OciChunkWriter
{
    public static async Task<long> AppendAsync(OciUploadStore sessions, OciUploadStore.Ticket ticket,
        Stream input, long maximumBytes, string? contentRange, long? contentLength, CancellationToken ct)
    {
        var start = ticket.CommittedLength;
        if (start < 0 || start > maximumBytes || contentLength < 0)
            throw new OciChunkRangeException();
        long? expected = contentLength;
        if (contentRange is not null)
        {
            var parts = contentRange.Split('-');
            if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p.Any(c => c is < '0' or > '9')) ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first) ||
                !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var last) ||
                first != start || last < first || last == long.MaxValue)
                throw new OciChunkRangeException();
            expected = last - first + 1;
            if (contentLength.HasValue && contentLength != expected) throw new OciChunkRangeException();
        }
        if (expected > maximumBytes - start) throw new PayloadTooLargeException();
        var id = Path.GetFileNameWithoutExtension(ticket.Path);
        if (!sessions.IsCurrent(ticket.Repository, id, ticket))
            throw new InvalidOperationException("OCI upload session is no longer active");
        var appended = false;
        try
        {
            await using (var output = new FileStream(ticket.Path, FileMode.Open, FileAccess.Write,
                             FileShare.None, 81920, FileOptions.Asynchronous))
            {
                if (output.Length != start) throw new InvalidDataException("OCI staging offset disagrees with commit");
                appended = true;
                output.Position = start;
                var buffer = new byte[81920];
                long written = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    if (count > maximumBytes - start - written) throw new PayloadTooLargeException();
                    if (expected.HasValue && count > expected.Value - written) throw new OciChunkRangeException();
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                    written += count;
                }
                if (expected.HasValue && written != expected.Value) throw new OciChunkRangeException();
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            sessions.Touch(ticket);
            return ticket.CommittedLength;
        }
        catch
        {
            if (!appended)
            {
                ticket.Unusable = true;
                throw;
            }
            try
            {
                // Rollback is deliberately independent of the disconnected client's
                // cancellation token. Never acknowledge or resume a partial chunk.
                using var rollback = new FileStream(ticket.Path, FileMode.Open, FileAccess.Write, FileShare.None);
                if (rollback.Length < start) throw new IOException("OCI committed bytes are missing");
                rollback.SetLength(start);
                rollback.Flush(flushToDisk: true);
            }
            catch
            {
                ticket.Unusable = true;
                throw new IOException("OCI chunk rollback failed; session requires recovery");
            }
            throw;
        }
    }
}

public sealed class OciChunkRangeException : Exception { }
