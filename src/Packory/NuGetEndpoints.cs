using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Security.Cryptography;

namespace Packory;

public static class NuGetEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/nuget/v3/index.json", (Settings settings, HttpContext ctx) =>
        {
            var denied = Access.RequireNativeNuget(ctx, settings);
            if (denied is not null) return denied;
            var root = settings.Base + "/nuget/v3/";
            return Results.Json(new
            {
                version = "3.0.0",
                resources = new object[]
                {
                    new Dictionary<string,string> { ["@id"] = root + "flatcontainer/", ["@type"] = "PackageBaseAddress/3.0.0" },
                    new Dictionary<string,string> { ["@id"] = root + "registration/", ["@type"] = "RegistrationsBaseUrl/3.6.0" },
                    new Dictionary<string,string> { ["@id"] = root + "search", ["@type"] = "SearchQueryService/3.5.0" },
                    new Dictionary<string,string> { ["@id"] = root + "push", ["@type"] = "PackagePublish/2.0.0" }
                }
            });
        });

        app.MapGet("/nuget/v3/flatcontainer/{id}/index.json", async (string id, Settings settings, IPackageDatabase db, IHttpClientFactory clients, HttpContext ctx) =>
        {
            if (Access.RequireNativeNuget(ctx, settings) is { } denied) return denied;
            id = id.ToLowerInvariant();
            if (!Names.Nuget(id)) return Results.BadRequest(new { error = "Invalid NuGet package ID" });
            if (settings.DeniesNuget(id)) return Results.StatusCode(403);
            var versions = new HashSet<string>(db.Versions("nuget", id), StringComparer.OrdinalIgnoreCase);
            if (settings.IsReservedNuget(id))
                return versions.Count == 0 ? Results.NotFound() : Results.Json(new { versions = versions.OrderBy(v => v, StringComparer.Ordinal).ToArray() });
            var uri = settings.NugetUpstreamUrl.TrimEnd('/') + "/" + id + "/index.json";
            var client = clients.CreateClient("upstream");
            using var deadline = UpstreamDeadline.Start(client, ctx.RequestAborted);
            try
            {
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.IsSuccessStatusCode)
                {
                    if (response.Content.Headers.ContentLength > settings.MaxMetadataBytes)
                        throw new PayloadTooLargeException();
                    await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
                    var bytes = await BlobStore.ReadBoundedAsync(body, settings.MaxMetadataBytes, deadline.Token);
                    using var doc = JsonDocument.Parse(bytes);
                    if (doc.RootElement.TryGetProperty("versions", out var collection) && collection.ValueKind == JsonValueKind.Array)
                        foreach (var v in collection.EnumerateArray())
                            if (v.ValueKind == JsonValueKind.String && v.GetString() is { } version && Names.Version(version)) versions.Add(version.ToLowerInvariant());
                }
                else if (response.StatusCode != System.Net.HttpStatusCode.NotFound && versions.Count == 0) return Results.StatusCode(502);
            }
            catch (HttpRequestException) when (versions.Count > 0) { }
            catch (OperationCanceledException) when (versions.Count > 0 && !ctx.RequestAborted.IsCancellationRequested) { }
            catch (HttpRequestException) { return Results.StatusCode(502); }
            catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested) { return Results.StatusCode(504); }
            return versions.Count == 0 ? Results.NotFound() : Results.Json(new { versions = versions.OrderBy(v => v, StringComparer.Ordinal).ToArray() });
        });

        app.MapGet("/nuget/v3/flatcontainer/{id}/{version}/{file}", async (
            string id, string version, string file, Settings settings, IPackageDatabase db, BlobStore blobs,
            IHttpClientFactory clients, ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.RequireNativeNuget(ctx, settings) is { } denied) return denied;
            id = id.ToLowerInvariant(); version = version.ToLowerInvariant();
            if (!Names.Nuget(id) || !Names.Version(version)) return Results.BadRequest();
            if (settings.DeniesNuget(id)) return Results.StatusCode(403);
            var prefix = $"{id}.{version}";
            if (!string.Equals(file, prefix + ".nupkg", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(file, prefix + ".nupkg.sha512", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(file, id + ".nuspec", StringComparison.OrdinalIgnoreCase)) return Results.NotFound();
            var path = await EnsureNuget(id, version, settings, db, blobs, clients.CreateClient("upstream"), locks, ctx.RequestAborted);
            if (path is null) return Results.NotFound();
            if (file.EndsWith(".sha512", StringComparison.OrdinalIgnoreCase))
            {
                // SHA-512 is persisted for new revisions at publication time.
                // Historical revisions without metadata preserve the legacy
                // computed response but are explicitly NOT independently proven.
                var recorded = db.GetNugetDigest(id, version);
                if (recorded is not null)
                    return Results.Text(recorded.Sha512, "text/plain");
                await using var fs = File.OpenRead(path);
                return Results.Text(Convert.ToBase64String(await SHA512.HashDataAsync(fs, ctx.RequestAborted)), "text/plain");
            }
            if (file.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            {
                var nuspec = ReadNuspec(path);
                return Results.Bytes(nuspec.Xml, "application/xml");
            }
            return Results.File(path, "application/octet-stream", enableRangeProcessing: true);
        });

        app.MapGet("/nuget/v3/registration/{id}/index.json", async (string id, Settings settings, IPackageDatabase db,
            BlobStore blobs, IHttpClientFactory clients, HttpContext ctx) =>
        {
            if (Access.RequireNativeNuget(ctx, settings) is { } denied) return denied;
            if (!Names.Nuget(id)) return Results.BadRequest();
            if (settings.DeniesNuget(id)) return Results.StatusCode(403);
            var result = await NugetRegistration.GetIndex(id.ToLowerInvariant(), settings, db, blobs,
                clients.CreateClient("upstream"), ctx.RequestAborted);
            return result is null ? Results.NotFound() : Results.Json(result);
        });

        app.MapGet("/nuget/v3/registration/{id}/{**suffix}", async (string id, string? suffix, Settings settings,
            IPackageDatabase db, BlobStore blobs, IHttpClientFactory clients, HttpContext ctx) =>
        {
            if (Access.RequireNativeNuget(ctx, settings) is { } denied) return denied;
            if (!Names.Nuget(id) || string.IsNullOrEmpty(suffix)) return Results.BadRequest();
            if (settings.DeniesNuget(id)) return Results.StatusCode(403);
            var resource = await NugetRegistration.GetResource(id.ToLowerInvariant(), Uri.UnescapeDataString(suffix),
                settings, db, blobs, clients.CreateClient("upstream"), ctx.RequestAborted);
            return resource is null ? Results.NotFound() : Results.Json(resource);
        });

        app.MapGet("/nuget/v3/search", (string? q, int? skip, int? take, Settings settings, IPackageDatabase db, HttpContext ctx) =>
        {
            if (Access.RequireNativeNuget(ctx, settings) is { } denied) return denied;
            var matching = db.Search(q ?? "", "nuget", 100).Where(x => !settings.DeniesNuget(x.Name)).ToList();
            var groups = matching.GroupBy(x => x.Name).Skip(Math.Clamp(skip ?? 0, 0, 10000)).Take(Math.Clamp(take ?? 20, 1, 100));
            var records = groups.Select(g => new
            {
                id = g.Key, version = g.First().Version, description = g.First().Description,
                versions = g.Select(v => new { version = v.Version,
                    @id = settings.Base + $"/nuget/v3/registration/{g.Key}/{v.Version}.json" }).ToArray()
            }).ToArray();
            return Results.Json(new { totalHits = matching.Select(x => x.Name).Distinct().Count(), data = records });
        });

        app.MapMethods("/nuget/v3/push", ["PUT", "POST"], async (Settings settings, IPackageDatabase db, BlobStore blobs, ArtifactLocks locks, HttpContext ctx) =>
        {
            if (Access.RequireNativeNuget(ctx, settings, write: true) is { } denied) return denied;
            if (!ctx.Request.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart NuGet push" });
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            if (form.Files.Count != 1) return Results.BadRequest(new { error = "Expected exactly one nupkg" });
            var file = form.Files[0];
            if (file.Length > settings.MaxArtifactBytes) return Results.StatusCode(413);
            await using var source = file.OpenReadStream();
            var bytes = await BlobStore.ReadBoundedAsync(source, settings.MaxArtifactBytes, ctx.RequestAborted);
            Nuspec spec;
            try
            {
                using var mem = new MemoryStream(bytes, writable: false);
                spec = ReadNuspec(mem);
            }
            catch (Exception e) when (e is InvalidDataException or XmlException or InvalidOperationException)
            { return Results.BadRequest(new { error = "Invalid nupkg or nuspec" }); }
            var id = spec.Id.ToLowerInvariant(); var version = spec.Version.ToLowerInvariant();
            if (settings.DeniesNuget(id)) return Results.StatusCode(403);
            if (Access.RequireNamespace(ctx, settings, "nuget", id) is { } forbidden) return forbidden;
            var key = NugetKey(id, version);
            using var gate = await locks.AcquireAsync("nuget", id, version, ctx.RequestAborted);
            if (db.HasPackage("nuget", id, version) || blobs.Existing(key) is not null)
                return Results.Conflict(new { error = "Version already exists" });
            db.BeginPublication(key, "nuget", id, version);
            using var content = new MemoryStream(bytes, writable: false);
            if (!await blobs.PutAsync(key, content, settings.MaxArtifactBytes, ctx.RequestAborted)) return Results.Conflict();
            var digest = Convert.ToBase64String(SHA512.HashData(bytes));
            if (!db.PublishNuget(id, version, spec.Description, Access.Actor(ctx),
                digest, bytes.LongLength)) return Results.Conflict();
            db.CompletePublication(key);
            return Results.StatusCode(StatusCodes.Status201Created);
        }).RequireRateLimiting("publish");
    }

    public static string NugetKey(string id, string version) => $"nuget/{id}/{version}/{id}.{version}.nupkg";

    public static async Task<string?> EnsureNuget(string id, string version, Settings settings, IPackageDatabase db,
        BlobStore blobs, HttpClient http, ArtifactLocks locks, CancellationToken ct)
    {
        using var gate = await locks.AcquireAsync("nuget", id, version, ct);
        var key = NugetKey(id, version);
        var origin = db.PackageOrigin("nuget", id, version);
        if (blobs.Existing(key) is { } available)
        {
            if (origin is null)
                throw new InvalidDataException("Unindexed NuGet blob requires recovery");
            var digest = db.GetNugetDigest(id, version);
            // V1/V2 legacy packages have no authoritative publication digest:
            // preserve compatibility but NEVER classify these as verified.
            if (digest is not null &&
                (!string.Equals(digest.Origin, origin, StringComparison.Ordinal) ||
                 !await NugetDigest.MatchesAsync(available, digest, ct)))
                throw new InvalidDataException("Cached NuGet bytes conflict with recorded checksum");
            return available;
        }
        // A missing hosted release must never be substituted with a package from an upstream.
        if (origin == "hosted") throw new InvalidDataException("Hosted NuGet blob is missing");
        if (settings.IsReservedNuget(id)) return null;
        using var deadline = UpstreamDeadline.Start(http, ct);
        ct = deadline.Token;
        var url = settings.NugetUpstreamUrl.TrimEnd('/') + $"/{id}/{version}/{id}.{version}.nupkg";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length > settings.MaxArtifactBytes)
            throw new PayloadTooLargeException();
        // Validate an upstream artifact BEFORE the immutable cache key is published.
        // A concurrent reader must never observe a partially verified or mismatched package.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = await BlobStore.ReadBoundedAsync(stream, settings.MaxArtifactBytes, ct);
        var checksumUrl = settings.NugetUpstreamUrl.TrimEnd('/') + $"/{id}/{version}/{id}.{version}.nupkg.sha512";
        using var checksumResponse = await http.GetAsync(checksumUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        // Do not silently cache a mirror artifact without upstream integrity evidence.
        if (!checksumResponse.IsSuccessStatusCode)
            throw new InvalidDataException("NuGet upstream SHA512 checksum unavailable");
        {
            // Bound the checksum document; a Base64-encoded SHA-512 checksum is 88 chars.
            await using var checksumStream = await checksumResponse.Content.ReadAsStreamAsync(ct);
            var checksumBytes = await BlobStore.ReadBoundedAsync(checksumStream, 1024, ct);
            var expected = System.Text.Encoding.UTF8.GetString(checksumBytes).Trim();
            var actual = Convert.ToBase64String(SHA512.HashData(bytes));
            if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(expected), System.Text.Encoding.UTF8.GetBytes(actual)))
                throw new InvalidDataException("NuGet upstream SHA512 checksum mismatch");
        }
        Nuspec spec;
        using (var archive = new MemoryStream(bytes, writable: false))
        {
            spec = ReadNuspec(archive);
            if (!string.Equals(spec.Id, id, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(spec.Version, version, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("NuGet package identity does not match request");
        }
        var verifiedHash = Convert.ToBase64String(SHA512.HashData(bytes));
        if (db.GetNugetDigest(id, version) is { } prior &&
            (!string.Equals(prior.Origin, "mirror", StringComparison.Ordinal) ||
             !string.Equals(prior.Sha512, verifiedHash, StringComparison.Ordinal) ||
             prior.Size != bytes.LongLength))
            throw new InvalidDataException("Upstream NuGet bytes conflict with a persisted revision digest");
        using var package = new MemoryStream(bytes, writable: false);
        if (!await blobs.PutAsync(key, package, settings.MaxArtifactBytes, ct))
            throw new InvalidDataException("NuGet cache blob was concurrently replaced");
        db.UpsertNugetMirror(id, version, spec.Description, verifiedHash, bytes.LongLength);
        return blobs.Existing(key);
    }

    public sealed record Nuspec(string Id, string Version, string Description, byte[] Xml);

    public static Nuspec ReadNuspec(string path)
    {
        using var file = File.OpenRead(path);
        return ReadNuspec(file);
    }

    public static Nuspec ReadNuspec(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var entries = archive.Entries.Where(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/')).ToList();
        if (entries.Count != 1 || entries[0].Length > 1024 * 1024) throw new InvalidDataException("Expected one root nuspec");
        using var input = entries[0].Open();
        using var bounded = new MemoryStream();
        input.CopyTo(bounded);
        if (bounded.Length > 1024 * 1024) throw new InvalidDataException("Nuspec too large");
        var xml = bounded.ToArray();
        using var reader = XmlReader.Create(new MemoryStream(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var doc = XDocument.Load(reader);
        var metadata = doc.Root?.Elements().FirstOrDefault(x => x.Name.LocalName == "metadata");
        string value(string key) => metadata?.Elements().FirstOrDefault(x => x.Name.LocalName == key)?.Value.Trim() ?? "";
        var id = value("id"); var version = value("version");
        if (!Names.Nuget(id) || !Names.Version(version)) throw new InvalidDataException("Invalid NuGet identity");
        return new Nuspec(id, version, value("description")[..Math.Min(value("description").Length, 2000)], xml);
    }
}
