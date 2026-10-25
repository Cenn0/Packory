using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Packory;

/// <summary>
/// Admin-only, inert inspection of the exact staged Skill ZIP. Never serves
/// unapproved bytes from public catalog routes and never executes ZIP members.
/// </summary>
public static class SkillReviewSource
{
    private const int MaxArchiveBytes = 20 * 1024 * 1024;
    private const int MaxTextPreviewBytes = 256 * 1024;

    public static async Task<IResult> ListAsync(string name, string version,
        Settings settings, SkillSecurityStore security, BlobStore blobs, HttpContext ctx)
    {
        if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
        if (!Names.Skill(name) || !Names.Version(version)) return Results.BadRequest();
        var revision = security.Get(name, version);
        if (revision is null) return Results.NotFound();
        var bytes = await ExactStagedBytes(revision, blobs, ctx.RequestAborted);
        if (bytes is null) return Results.StatusCode(503);
        using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        ctx.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new
        {
            sha256 = revision.Sha256,
            files = zip.Entries.Where(entry => !entry.FullName.EndsWith('/'))
                .Select(entry => new
                {
                    path = entry.FullName,
                    bytes = entry.Length,
                    previewable = Previewable(entry)
                }).ToArray()
        });
    }

    public static async Task<IResult> FileAsync(string name, string version,
        Settings settings, SkillSecurityStore security, BlobStore blobs, HttpContext ctx,
        string? path)
    {
        if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
        if (!Names.Skill(name) || !Names.Version(version) ||
            string.IsNullOrWhiteSpace(path) || path.Length > 512 ||
            path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(part => part is "" or "." or ".."))
            return Results.BadRequest();
        var revision = security.Get(name, version);
        if (revision is null) return Results.NotFound();
        var bytes = await ExactStagedBytes(revision, blobs, ctx.RequestAborted);
        if (bytes is null) return Results.StatusCode(503);
        using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
        var entry = zip.Entries.SingleOrDefault(candidate =>
            string.Equals(candidate.FullName, path, StringComparison.Ordinal));
        if (entry is null || entry.FullName.EndsWith('/')) return Results.NotFound();
        if (!TextExtension(entry.FullName)) return Results.StatusCode(415);
        if (entry.Length > MaxTextPreviewBytes) return Results.StatusCode(413);
        try
        {
            using var reader = new StreamReader(entry.Open(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
            var content = await reader.ReadToEndAsync(ctx.RequestAborted);
            if (content.Contains('\0')) return Results.StatusCode(415);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { sha256 = revision.Sha256, path, bytes = entry.Length, content });
        }
        catch (DecoderFallbackException)
        {
            return Results.StatusCode(415);
        }
        catch (InvalidDataException)
        {
            return Results.StatusCode(503);
        }
    }

    private static bool Previewable(ZipArchiveEntry entry) =>
        entry.Length <= MaxTextPreviewBytes && TextExtension(entry.FullName);

    private static bool TextExtension(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".md" or ".txt" or ".py" or ".sh" or ".ps1" or ".js" or ".ts" or
            ".json" or ".yaml" or ".yml" or ".toml" or ".xml" or ".csv" or
            ".sql" or ".html" or ".css";

    private static async Task<byte[]?> ExactStagedBytes(SkillSecurityStore.Revision revision,
        BlobStore blobs, CancellationToken cancellationToken)
    {
        if (revision.StagingKey != $"skill-staging/{revision.Sha256}.zip") return null;
        try
        {
            var source = blobs.Existing(revision.StagingKey);
            if (source is null || new FileInfo(source).Length > MaxArchiveBytes) return null;
            var bytes = await File.ReadAllBytesAsync(source, cancellationToken);
            if (bytes.Length > MaxArchiveBytes ||
                Convert.ToHexStringLower(SHA256.HashData(bytes)) != revision.Sha256)
                return null;
            return SkillEndpoints.InspectZip(bytes).Name == revision.Name ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
