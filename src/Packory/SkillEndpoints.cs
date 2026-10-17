using System.IO.Compression;
using System.Text;

namespace Packory;

public static class SkillEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/skills", (IPackageDatabase db, Settings settings, HttpContext ctx) =>
            Access.Require(ctx, settings) is { } denied ? denied : Results.Json(new { items = db.Skills() }));

        app.MapGet("/api/skills/{name}/{version}", (string name, string version, IPackageDatabase db, Settings settings, HttpContext ctx) =>
        {
            if (Access.Require(ctx, settings) is { } denied) return denied;
            if (!Names.Skill(name) || !Names.Version(version)) return Results.BadRequest();
            var skill = db.Skills(name).FirstOrDefault(x => x.Version == version);
            return skill is null ? Results.NotFound() : Results.Json(skill);
        });

        app.MapGet("/api/skills/{name}/{version}/download", (string name, string version, IPackageDatabase db, BlobStore blobs, Settings settings, HttpContext ctx) =>
        {
            if (Access.Require(ctx, settings) is { } denied) return denied;
            if (!Names.Skill(name) || !Names.Version(version)) return Results.BadRequest();
            if (!db.Skills(name).Any(x => x.Version == version)) return Results.NotFound();
            var file = blobs.Existing(SkillKey(name, version));
            return file is null ? Results.NotFound() : Results.File(file, "application/zip", fileDownloadName: name + "-" + version + ".zip");
        });

        app.MapGet("/api/skills/{name}/{version}/skill.md", (string name, string version, IPackageDatabase db, BlobStore blobs, Settings settings, HttpContext ctx) =>
        {
            if (Access.Require(ctx, settings) is { } denied) return denied;
            if (!Names.Skill(name) || !Names.Version(version)) return Results.BadRequest();
            if (!db.Skills(name).Any(x => x.Version == version)) return Results.NotFound();
            var path = blobs.Existing(SkillKey(name, version));
            if (path is null) return Results.NotFound();
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.Entries.Single(e => e.FullName == "SKILL.md" || e.FullName == name + "/SKILL.md");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return Results.Text(reader.ReadToEnd(), "text/markdown; charset=utf-8");
        });

        app.MapPost("/api/skills", async (Settings settings, IPackageDatabase db, BlobStore blobs, ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.Require(ctx, settings, write: true) is { } denied) return denied;
            if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart form-data" });
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var version = form["version"].ToString();
            if (!Names.Version(version) || form.Files.Count != 1) return Results.BadRequest(new { error = "Expected version and one ZIP file" });
            var file = form.Files[0];
            if (file.Length > settings.MaxArtifactBytes) return Results.StatusCode(413);
            await using var input = file.OpenReadStream();
            var bytes = await BlobStore.ReadBoundedAsync(input, settings.MaxArtifactBytes, ctx.RequestAborted);
            SkillManifest manifest;
            try { manifest = InspectZip(bytes); }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
            if (Access.RequireNamespace(ctx, settings, "skill", manifest.Name) is { } forbidden) return forbidden;
            var key = SkillKey(manifest.Name, version);
            using var gate = await locks.AcquireAsync("skill", manifest.Name, version, ctx.RequestAborted);
            if (db.Skills(manifest.Name).Any(x => x.Version == version) || blobs.Existing(key) is not null)
                return Results.Conflict(new { error = "Skill version already exists" });
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

    public sealed record SkillManifest(string Name, string Description);

    public static SkillManifest InspectZip(byte[] data)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read);
            if (zip.Entries.Count is < 1 or > 250) throw new InvalidDataException("ZIP has an invalid number of files");
            const long maximumUncompressed = 20 * 1024 * 1024;
            long uncompressed = 0;
            // ZIP archives can contain duplicate/case-conflicting paths. They are
            // ambiguous on common client filesystems even without traversal.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    !names.Add(entryName))
                    throw new InvalidDataException("Unsafe or ambiguous ZIP entry");
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
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            if (lines.Length < 4 || lines[0].Trim() != "---") throw new InvalidDataException("Missing YAML frontmatter");
            var end = Array.FindIndex(lines, 1, x => x.Trim() == "---");
            if (end < 3 || end > 60) throw new InvalidDataException("Invalid YAML frontmatter");
            string? name = null; string? description = null;
            foreach (var line in lines.Skip(1).Take(end - 1))
            {
                if (line.StartsWith("name:", StringComparison.Ordinal))
                {
                    if (name is not null) throw new InvalidDataException("Duplicate skill name");
                    name = Unquote(line[5..].Trim());
                }
                if (line.StartsWith("description:", StringComparison.Ordinal))
                {
                    if (description is not null) throw new InvalidDataException("Duplicate skill description");
                    description = Unquote(line[12..].Trim());
                }
            }
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
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException)
        { throw new InvalidDataException("Invalid ZIP archive"); }
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"') return value[1..^1];
        return value;
    }
}
