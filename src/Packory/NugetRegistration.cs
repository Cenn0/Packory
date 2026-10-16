using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Packory;

public static class NugetRegistration
{
    public static async Task<JsonObject?> GetIndex(string id, Settings settings, Database db,
        BlobStore blobs, HttpClient client, CancellationToken ct)
    {
        var remote = await Fetch(id, "index.json", settings, blobs, client, ct);
        var local = db.Search(id, "nuget", 100).Where(x => x.Name == id && x.Origin == "hosted").ToList();
        if (remote is null && local.Count == 0) return null;
        var origin = settings.Base + "/nuget/v3/registration/" + id + "/";
        var result = remote ?? new JsonObject
        {
            ["@id"] = origin + "index.json", ["count"] = 0, ["items"] = new JsonArray()
        };
        if (local.Count != 0)
        {
            if (result["items"] is not JsonArray pages) result["items"] = pages = new JsonArray();
            var items = new JsonArray();
            foreach (var entry in local)
                items.Add(LocalLeaf(entry, settings, blobs));
            var sorted = local.Select(x => x.Version).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            pages.Add(new JsonObject
            {
                ["@id"] = origin + "index.json#page/local",
                ["count"] = items.Count,
                ["lower"] = sorted.First(), ["upper"] = sorted.Last(), ["items"] = items
            });
            result["count"] = pages.Count;
        }
        return result;
    }

    public static async Task<JsonObject?> GetResource(string id, string suffix, Settings settings, Database db,
        BlobStore blobs, HttpClient client, CancellationToken ct)
    {
        if (suffix.Length > 256 || suffix.StartsWith('/') || suffix.Contains('\\') ||
            suffix.Contains(':') || suffix.Split('/').Any(x => x is "" or "." or "..") ||
            !suffix.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            suffix.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '/')))
            return null;
        if (!suffix.Contains('/') && Names.Version(suffix[..^5]))
        {
            var version = suffix[..^5].ToLowerInvariant();
            var local = db.Search(id, "nuget", 100).FirstOrDefault(x => x.Name == id && x.Version == version && x.Origin == "hosted");
            if (local is not null) return LocalLeaf(local, settings, blobs);
        }
        return await Fetch(id, suffix, settings, blobs, client, ct);
    }

    private static async Task<JsonObject?> Fetch(string id, string suffix, Settings settings,
        BlobStore blobs, HttpClient client, CancellationToken ct)
    {
        var key = $"nuget-registration/{id}/{Names.HashName(suffix)}.json";
        var cached = blobs.Existing(key);
        byte[]? bytes = null;
        try
        {
            var url = settings.NugetRegistrationUrl.TrimEnd('/') + "/" + id + "/" + suffix;
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                bytes = await BlobStore.ReadBoundedAsync(stream, settings.MaxMetadataBytes, ct);
                if (JsonNode.Parse(bytes) is not JsonObject) throw new InvalidDataException("Invalid upstream registration");
                await blobs.ReplaceAsync(key, new MemoryStream(bytes, false), settings.MaxMetadataBytes, ct);
            }
            else if (response.StatusCode != System.Net.HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException) when (cached is not null) { }
        catch (TaskCanceledException) when (cached is not null && !ct.IsCancellationRequested) { }
        if (bytes is null && cached is not null) bytes = await File.ReadAllBytesAsync(cached, ct);
        if (bytes is null) return null;
        var doc = JsonNode.Parse(bytes) as JsonObject;
        if (doc is not null) Rewrite(doc, settings);
        return doc;
    }

    private static void Rewrite(JsonNode? node, Settings settings)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) Rewrite(child, settings);
        }
        else if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToList())
            {
                if (pair.Value is JsonValue value && value.TryGetValue<string>(out var original) &&
                    Uri.TryCreate(original, UriKind.Absolute, out var url) && url.Scheme == "https")
                {
                    var registration = new Uri(settings.NugetRegistrationUrl);
                    var flat = new Uri(settings.NugetUpstreamUrl);
                    if (url.Host == registration.Host && url.AbsolutePath.StartsWith(registration.AbsolutePath, StringComparison.Ordinal))
                        obj[pair.Key] = settings.Base + "/nuget/v3/registration/" + url.AbsolutePath[registration.AbsolutePath.Length..] + url.Fragment;
                    else if (url.Host == flat.Host && url.AbsolutePath.StartsWith(flat.AbsolutePath, StringComparison.Ordinal))
                        obj[pair.Key] = settings.Base + "/nuget/v3/flatcontainer/" + url.AbsolutePath[flat.AbsolutePath.Length..];
                }
                else Rewrite(pair.Value, settings);
            }
        }
    }

    private static JsonObject LocalLeaf(CatalogEntry entry, Settings settings, BlobStore blobs)
    {
        var id = entry.Name; var version = entry.Version;
        var root = settings.Base + "/nuget/v3/";
        var path = blobs.Existing(NuGetEndpoints.NugetKey(id, version));
        var groups = new JsonArray();
        if (path is not null)
        {
            var nuspec = NuGetEndpoints.ReadNuspec(path);
            using var stream = new MemoryStream(nuspec.Xml);
            var xml = XDocument.Load(stream);
            var deps = xml.Descendants().FirstOrDefault(x => x.Name.LocalName == "dependencies");
            if (deps is not null)
            {
                var frames = deps.Elements().Where(x => x.Name.LocalName == "group").ToList();
                if (frames.Count == 0) frames.Add(deps);
                foreach (var frame in frames)
                {
                    var items = new JsonArray();
                    foreach (var dependency in frame.Elements().Where(x => x.Name.LocalName == "dependency"))
                        items.Add(new JsonObject
                        {
                            ["id"] = (string?)dependency.Attribute("id") ?? "",
                            ["range"] = (string?)dependency.Attribute("version") ?? ""
                        });
                    groups.Add(new JsonObject
                    {
                        ["targetFramework"] = (string?)frame.Attribute("targetFramework") ?? "",
                        ["dependencies"] = items
                    });
                }
            }
        }
        var payload = root + $"flatcontainer/{id}/{version}/{id}.{version}.nupkg";
        return new JsonObject
        {
            ["@id"] = root + $"registration/{id}/{version}.json",
            ["packageContent"] = payload,
            ["catalogEntry"] = new JsonObject
            {
                ["id"] = id, ["version"] = version, ["description"] = entry.Description,
                ["packageContent"] = payload, ["dependencyGroups"] = groups
            }
        };
    }
}
