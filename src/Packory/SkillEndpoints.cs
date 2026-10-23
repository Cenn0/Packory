using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Packory;

public static class SkillEndpoints
{
    public const long MaxUncompressedBytes = 20L * 1024 * 1024;
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/skills", (IPackageDatabase db, SkillSecurityStore security, Settings settings, HttpContext ctx) =>
            Access.Require(ctx, settings) is { } denied ? denied :
            Results.Json(new { items = db.Skills().Where(x => !settings.SkillSecurityEnforced ||
                security.IsApproved(x.Name, x.Version)).ToList() }));

        app.MapGet("/api/skills/{name}/{version}", (string name, string version, IPackageDatabase db, SkillSecurityStore security, Settings settings, HttpContext ctx) =>
        {
            if (Access.Require(ctx, settings) is { } denied) return denied;
            if (!Names.Skill(name) || !Names.Version(version)) return Results.BadRequest();
            if (settings.SkillSecurityEnforced && !security.IsApproved(name, version)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-store";
            var skill = db.Skills(name).FirstOrDefault(x => x.Version == version);
            return skill is null ? Results.NotFound() : Results.Json(skill);
        });

        app.MapGet("/api/skills/{name}/{version}/download", (string name, string version, IPackageDatabase db, BlobStore blobs, SkillSecurityStore security, Settings settings, HttpContext ctx) =>
        {
            if (Access.Require(ctx, settings) is { } denied) return denied;
            if (!Names.Skill(name) || !Names.Version(version)) return Results.BadRequest();
            if (settings.SkillSecurityEnforced && !security.IsApproved(name, version)) return Results.NotFound();
            if (!db.Skills(name).Any(x => x.Version == version)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-store";
            var bytes = ReadVerifiedPublishedBytes(name, version, blobs, security, settings);
            // Materialize and authenticate the exact response bytes. Sending a file
            // path after checking its hash would permit a TOCTOU modification.
            return bytes is null ? Results.NotFound() : Results.Bytes(bytes,
                "application/zip", fileDownloadName: name + "-" + version + ".zip");
        });

        app.MapGet("/api/skills/{name}/{version}/skill.md", (string name, string version, IPackageDatabase db, BlobStore blobs, SkillSecurityStore security, Settings settings, HttpContext ctx) =>
        {
            if (Access.Require(ctx, settings) is { } denied) return denied;
            if (!Names.Skill(name) || !Names.Version(version)) return Results.BadRequest();
            if (settings.SkillSecurityEnforced && !security.IsApproved(name, version)) return Results.NotFound();
            if (!db.Skills(name).Any(x => x.Version == version)) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-store";
            var bytes = ReadVerifiedPublishedBytes(name, version, blobs, security, settings);
            if (bytes is null) return Results.NotFound();
            try
            {
                using var archive = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
                var entries = archive.Entries.Where(e =>
                    e.FullName == "SKILL.md" || e.FullName == name + "/SKILL.md").ToArray();
                if (entries.Length != 1 || entries[0].Length > 256 * 1024)
                    return Results.NotFound();
                using var reader = new StreamReader(entries[0].Open(), new UTF8Encoding(false, true));
                return Results.Text(reader.ReadToEnd(), "text/markdown; charset=utf-8");
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException
                or IOException or DecoderFallbackException)
            {
                return Results.NotFound();
            }
        });

        app.MapPost("/api/skills", async (Settings settings, IPackageDatabase db, BlobStore blobs, ArtifactLocks locks,
            SkillSecurityStore security, HttpContext ctx) =>
        {
            if (Access.Require(ctx, settings, write: true) is { } denied) return denied;
            if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart form-data" });
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var version = form["version"].ToString();
            if (!Names.Version(version) || form.Files.Count != 1) return Results.BadRequest(new { error = "Expected version and one ZIP file" });
            var file = form.Files[0];
            var maxSkillBytes = Math.Min(settings.MaxArtifactBytes, 20L * 1024 * 1024);
            if (file.Length > maxSkillBytes) return Results.StatusCode(413);
            await using var input = file.OpenReadStream();
            var bytes = await BlobStore.ReadBoundedAsync(input, maxSkillBytes, ctx.RequestAborted);
            SkillManifest manifest;
            try { manifest = InspectZip(bytes); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (Access.RequireNamespace(ctx, settings, "skill", manifest.Name) is { } forbidden) return forbidden;
            var key = SkillKey(manifest.Name, version);
            using var gate = await locks.AcquireAsync("skill", manifest.Name, version, ctx.RequestAborted);
            if (db.Skills(manifest.Name).Any(x => x.Version == version) ||
                security.Get(manifest.Name, version) is not null || blobs.Existing(key) is not null)
                return Results.Conflict(new { error = "Skill version already exists" });

            if (settings.SkillSecurityEnforced)
            {
                var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                var stagingKey = $"skill-staging/{digest}.zip";
                var staged = blobs.Existing(stagingKey);
                if (staged is not null &&
                    SkillScanCoordinator.HashFile(staged) != digest)
                    return Results.StatusCode(503);
                if (staged is null && !await blobs.PutAsync(stagingKey, new MemoryStream(bytes, false),
                    settings.MaxArtifactBytes, ctx.RequestAborted))
                    return Results.Conflict();
                if (!security.TryStage(manifest.Name, version, manifest.Description, digest, stagingKey))
                    return Results.Conflict();
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Json(new { name = manifest.Name, version,
                    sha256 = digest, status = "QUEUED", approvalRequired = true },
                    statusCode: StatusCodes.Status202Accepted);
            }
            db.BeginPublication(key, "skill", manifest.Name, version);
            if (!await blobs.PutAsync(key, new MemoryStream(bytes, false), settings.MaxArtifactBytes, ctx.RequestAborted))
                return Results.Conflict();
            if (!db.AddSkill(manifest.Name, version, manifest.Description, Access.Actor(ctx))) return Results.Conflict();
            db.CompletePublication(key);
            return Results.Json(new { name = manifest.Name, version, description = manifest.Description },
                statusCode: StatusCodes.Status201Created);
        }).RequireRateLimiting("publish");
    }

    public static string SkillKey(string name, string version) => $"skills/{name}/{version}.zip";

    /// <summary>
    /// Authenticates *the exact bytes* to be served against the immutable,
    /// digest-bound administrator approval. This is separate from a passing
    /// scanner verdict: approved means reviewed, not safe to execute.
    /// Fail closed if a filesystem/S3 materialization changes after approval.
    /// </summary>
    public static byte[]? ReadVerifiedPublishedBytes(string name, string version,
        BlobStore blobs, SkillSecurityStore security, Settings settings)
    {
        if (!Names.Skill(name) || !Names.Version(version)) return null;
        var revision = settings.SkillSecurityEnforced ? security.Get(name, version) : null;
        if (settings.SkillSecurityEnforced &&
            (revision is null || revision.State != "APPROVED" ||
             !ArtifactDigests.ValidSha256(revision.Sha256)))
            return null;
        try
        {
            var path = blobs.Existing(SkillKey(name, version));
            if (path is null) return null;
            var limit = Math.Min(settings.MaxArtifactBytes, 20L * 1024 * 1024);
            if (new FileInfo(path).Length is <= 0 || new FileInfo(path).Length > limit)
                return null;
            var bytes = File.ReadAllBytes(path);
            if (bytes.LongLength is <= 0 || bytes.LongLength > limit) return null;
            if (revision is not null &&
                !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(bytes), Convert.FromHexString(revision.Sha256)))
                return null;
            return bytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    public sealed record SkillManifest(string Name, string Description);

    public static SkillManifest InspectZip(byte[] data)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
            if (zip.Entries.Count is < 1 or > 250) throw new InvalidDataException("ZIP has an invalid number of files");
            const long maximumUncompressed = MaxUncompressedBytes;
            long uncompressed = 0;
            // ZIP archives can contain duplicate/case-conflicting paths. They are
            // ambiguous on common client filesystems even without traversal.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries)
            {
                var entryName = e.FullName;
                var unixType = (e.ExternalAttributes >> 16) & 0xF000;
                if (entryName.Length is < 1 or > 512 || entryName.Contains('\\') ||
                    entryName.StartsWith('/') || entryName.Contains(':') ||
                    entryName.Contains("//", StringComparison.Ordinal) ||
                    entryName.Any(char.IsControl) ||
                    entryName.Split('/').Any(p => p is "." or "..") ||
                    unixType is not (0 or 0x4000 or 0x8000) ||
                    !names.Add(entryName.Normalize(NormalizationForm.FormC)))
                    throw new InvalidDataException("Unsafe or ambiguous ZIP entry");
                var canonical = entryName.TrimEnd('/').Normalize(NormalizationForm.FormC);
                var directory = entryName.EndsWith('/');
                if (files.Contains(canonical) || (!directory && directories.Contains(canonical)))
                    throw new InvalidDataException("ZIP file/directory collision");
                for (var slash = canonical.IndexOf('/'); slash >= 0; slash = canonical.IndexOf('/', slash + 1))
                {
                    var parent = canonical[..slash];
                    if (files.Contains(parent)) throw new InvalidDataException("ZIP file/directory collision");
                    directories.Add(parent);
                }
                if (directory) directories.Add(canonical);
                else files.Add(canonical);
                if (entryName.EndsWith('/'))
                {
                    if (e.Length != 0) throw new InvalidDataException("Directory ZIP entry contains bytes");
                    continue;
                }
                // Length in the central directory is attacker-controlled. Both
                // the declared total and actual decompressed bytes are bounded.
                if (e.Length > maximumUncompressed - uncompressed)
                    throw new InvalidDataException("Uncompressed ZIP exceeds 20 MiB");
                using var stream = e.Open();
                var buffer = new byte[81920];
                long actual = 0;
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    if (count > e.Length - actual ||
                        count > maximumUncompressed - uncompressed - actual)
                        throw new InvalidDataException("ZIP entry expanded beyond declared limits");
                    actual += count;
                }
                if (actual != e.Length) throw new InvalidDataException("ZIP entry length mismatch");
                uncompressed += actual;
            }
            var manifests = zip.Entries.Where(e => e.FullName == "SKILL.md" ||
                (e.FullName.EndsWith("/SKILL.md", StringComparison.Ordinal) && e.FullName.Count(c => c == '/') == 1)).ToList();
            if (manifests.Count != 1 || manifests[0].Length is < 12 or > 256 * 1024)
                throw new InvalidDataException("Exactly one SKILL.md is required at root or one folder below root");
            using var reader = new StreamReader(manifests[0].Open(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
            var markdown = reader.ReadToEnd();
            var frontmatter = SkillFrontmatter.Parse(markdown);
            static string? Text(System.Text.Json.Nodes.JsonNode? node) =>
                node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            var name = Text(frontmatter["name"]);
            var description = Text(frontmatter["description"]);
            if (name is null || !Names.Skill(name) || description is null || description.Length is < 1 or > 1024)
                throw new InvalidDataException("SKILL.md requires valid name and description frontmatter fields");
            if (manifests[0].FullName.Contains('/') && !manifests[0].FullName.StartsWith(name + "/", StringComparison.Ordinal))
                throw new InvalidDataException("Skill folder must match the declared name");
            // Disallow archives mixing root files with a named skill folder, or files in sibling folders.
            var prefix = manifests[0].FullName.Contains('/') ? name + "/" : "";
            if (zip.Entries.Any(e => !e.FullName.StartsWith(prefix, StringComparison.Ordinal)))
                throw new InvalidDataException("ZIP contains files outside the skill folder");
            return new SkillManifest(name, description);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or YamlDotNet.Core.YamlException)
        { throw new InvalidDataException("Invalid ZIP archive"); }
    }
}
