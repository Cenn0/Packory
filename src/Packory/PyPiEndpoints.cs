using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Packory;

/// <summary>
/// Hosted Python Simple Repository API (PEP 503 / PEP 691) and legacy Twine upload.
/// Includes HTML and JSON representations. No implicit PyPI upstream access.
/// </summary>
public static class PyPiEndpoints
{
    private static readonly Regex ProjectName = new(
        "^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,198}[A-Za-z0-9])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Filename = new(
        "^[A-Za-z0-9][A-Za-z0-9._+-]{0,254}\\.(?:whl|zip|tar\\.gz)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string? Normalize(string name)
    {
        if (name.Length is < 1 or > 200 || !ProjectName.IsMatch(name)) return null;
        return Regex.Replace(name.ToLowerInvariant(), "[-_.]+", "-");
    }

    public static string Key(string normalizedName, string filename) => "pypi/" + normalizedName + "/" + filename;

    public static void Map(WebApplication app)
    {
        app.MapGet("/pypi/simple/", (Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.Require(ctx, cfg) is { } denied) return denied;
            var names = db.PyProjects();
            if (WantsJson(ctx))
                return Results.Json(new
                {
                    meta = new Dictionary<string, string> { ["api-version"] = "1.1" },
                    projects = names.Select(name => new { name })
                }, contentType: "application/vnd.pypi.simple.v1+json");
            var html = "<!DOCTYPE html><html><head><meta name=\"pypi:repository-version\" content=\"1.1\"></head><body>" +
                string.Join("", names.Select(name => "<a href=\"" + WebUtility.HtmlEncode(name) + "/\">" +
                    WebUtility.HtmlEncode(name) + "</a>\n")) + "</body></html>";
            return Results.Text(html, "application/vnd.pypi.simple.v1+html");
        });

        app.MapGet("/pypi/simple/{name}/", (string name, Settings cfg, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.Require(ctx, cfg) is { } denied) return denied;
            var normalized = Normalize(name);
            if (normalized is null) return Results.NotFound();
            var files = db.PyFiles(normalized);
            if (files.Count == 0) return Results.NotFound();
            if (WantsJson(ctx))
                return Results.Json(new
                {
                    meta = new Dictionary<string, string> { ["api-version"] = "1.1" },
                    name = normalized,
                    files = files.Select(file => new
                    {
                        filename = file.Filename,
                        url = FileUrl(cfg, file),
                        hashes = new { sha256 = file.Sha256 },
                        size = file.Size
                    }),
                    versions = files.Select(file => file.Version).Distinct().ToArray()
                }, contentType: "application/vnd.pypi.simple.v1+json");
            var anchors = files.Select(file => "<a href=\"" + WebUtility.HtmlEncode(FileUrl(cfg, file)) +
                "#sha256=" + file.Sha256 + "\">" + WebUtility.HtmlEncode(file.Filename) + "</a>\n");
            var html = "<!DOCTYPE html><html><head><meta name=\"pypi:repository-version\" content=\"1.1\"></head><body>\n" +
                string.Join("", anchors) + "</body></html>";
            return Results.Text(html, "application/vnd.pypi.simple.v1+html");
        });

        app.MapGet("/pypi/files/{name}/{filename}", (string name, string filename,
            Settings cfg, IPackageDatabase db, BlobStore blobs, HttpContext ctx) =>
        {
            if (Access.Require(ctx, cfg) is { } denied) return denied;
            var normalized = Normalize(name);
            if (normalized != name || !Filename.IsMatch(filename)) return Results.NotFound();
            var entry = db.PyFiles(name).FirstOrDefault(x => x.Filename == filename);
            if (entry is null) return Results.NotFound();
            var path = blobs.Existing(Key(name, filename));
            if (path is null) return Results.NotFound();
            ctx.Response.Headers.ETag = "\"" + entry.Sha256 + "\"";
            return Results.File(path, "application/octet-stream", enableRangeProcessing: true);
        });

        app.MapPost("/pypi/legacy/", async (Settings cfg, IPackageDatabase db, BlobStore blobs,
            ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.Require(ctx, cfg, write: true) is { } denied) return denied;
            if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart form" });
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var name = Normalize(form["name"].ToString());
            var version = form["version"].ToString();
            if (name is null || !Names.Version(version) || form.Files.Count != 1)
                return Results.BadRequest(new { error = "Missing or invalid name, version, or distribution file" });
            var file = form.Files[0];
            var filename = file.FileName;
            if (!Filename.IsMatch(filename) ||
                !(filename.StartsWith(name + "-", StringComparison.OrdinalIgnoreCase) ||
                  filename.StartsWith(name.Replace('-', '_') + "-", StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest(new { error = "Invalid or mismatched distribution filename" });
            if (file.Length > cfg.MaxArtifactBytes) return Results.StatusCode(413);
            await using var input = file.OpenReadStream();
            var bytes = await BlobStore.ReadBoundedAsync(input, cfg.MaxArtifactBytes, ctx.RequestAborted);
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var claimed = form["sha256_digest"].ToString();
            if (claimed.Length > 0 && !string.Equals(claimed, sha, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Distribution SHA256 digest mismatch" });
            var key = Key(name, filename);
            using var lease = await locks.AcquireAsync("pypi", name, filename, ctx.RequestAborted);
            if (db.PyFiles(name).Any(f => f.Filename == filename) || blobs.Existing(key) is not null)
                return Results.Conflict(new { error = "Distribution filename already exists" });
            if (!await blobs.PutAsync(key, new MemoryStream(bytes, writable: false),
                cfg.MaxArtifactBytes, ctx.RequestAborted)) return Results.Conflict();
            var description = form["summary"].ToString();
            if (!db.PublishPyFile(new PyFileEntry(name, version, filename, sha, bytes.Length),
                    description[..Math.Min(2000, description.Length)], Access.Actor(ctx)))
                return Results.Conflict();
            return Results.Text("Distribution uploaded", "text/plain", statusCode: StatusCodes.Status200OK);
        }).RequireRateLimiting("publish");
    }

    private static bool WantsJson(HttpContext ctx) =>
        ctx.Request.Headers.Accept.ToString().Contains("application/vnd.pypi.simple.v1+json", StringComparison.OrdinalIgnoreCase);

    private static string FileUrl(Settings cfg, PyFileEntry file) => cfg.Base + "/pypi/files/" +
        Uri.EscapeDataString(file.Name) + "/" + Uri.EscapeDataString(file.Filename);
}
