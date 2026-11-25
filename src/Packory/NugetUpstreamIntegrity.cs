using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Packory;

/// <summary>
/// Verifies mirrored NuGet bytes against integrity evidence published separately
/// by the upstream. Never accepts a digest computed solely from downloaded bytes.
/// </summary>
public static class NugetUpstreamIntegrity
{
    private const int MaxMetadataBytes = 4 * 1024 * 1024;
    private const int MaxRegistrationPages = 32;
    private const string NugetFlatContainer = "https://api.nuget.org/v3-flatcontainer/";
    private const string NugetRegistration = "https://api.nuget.org/v3/registration5-gz-semver2/";

    public static async Task VerifyAsync(Settings settings, HttpClient http,
        string id, string version, byte[] package, CancellationToken ct)
    {
        var actual = SHA512.HashData(package);
        var checksumUrl = settings.NugetUpstreamUrl.TrimEnd('/') +
            $"/{id}/{version}/{id}.{version}.nupkg.sha512";
        using var response = await http.GetAsync(checksumUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        string expected;
        if (response.IsSuccessStatusCode)
        {
            if (response.Content.Headers.ContentLength > 1024)
                throw new InvalidDataException("NuGet upstream checksum response too large");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var bytes = await BlobStore.ReadBoundedAsync(stream, 1024, ct);
            expected = Encoding.UTF8.GetString(bytes).Trim();
        }
        else if (response.StatusCode == HttpStatusCode.NotFound && IsOfficialNugetOrg(settings))
        {
            // The NuGet V3 flat-container specification does not require a .sha512
            // resource. nuget.org publishes an independent SHA512 in its catalog.
            // Restrict this fallback to the known official registry, and fail
            // closed if catalog evidence is absent, untrusted, or malformed.
            expected = await ReadNugetOrgCatalogHashAsync(
                settings, http, id, version, package.LongLength, ct);
        }
        else
        {
            throw new InvalidDataException("NuGet upstream SHA512 evidence unavailable");
        }

        if (!NugetDigest.Valid(expected) ||
            !CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(expected)))
            throw new InvalidDataException("NuGet upstream SHA512 checksum mismatch");
    }

    private static bool IsOfficialNugetOrg(Settings settings) =>
        string.Equals(settings.NugetUpstreamUrl.TrimEnd('/') + "/", NugetFlatContainer,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(settings.NugetRegistrationUrl.TrimEnd('/') + "/", NugetRegistration,
            StringComparison.OrdinalIgnoreCase);

    private static Uri TrustedUri(string link, string pathPrefix)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.Equals(uri.IdnHost, "api.nuget.org", StringComparison.OrdinalIgnoreCase) ||
            uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 ||
            uri.Fragment.Length != 0 ||
            !uri.AbsolutePath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NuGet catalog reference outside trusted registry");
        return uri;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpClient http, Uri url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException("NuGet catalog metadata unavailable");
        if (response.Content.Headers.ContentLength > MaxMetadataBytes)
            throw new InvalidDataException("NuGet catalog metadata too large");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = await BlobStore.ReadBoundedAsync(stream, MaxMetadataBytes, ct);
        var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidDataException("NuGet catalog metadata must be a JSON object");
        }
        return document;
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        value = "";
        if (obj.ValueKind != JsonValueKind.Object ||
            !obj.TryGetProperty(name, out var field) ||
            field.ValueKind != JsonValueKind.String) return false;
        value = field.GetString() ?? "";
        return value.Length != 0;
    }

    private static string? FindCatalogUrl(JsonElement leaves, string id, string version)
    {
        if (leaves.ValueKind != JsonValueKind.Array) return null;
        foreach (var leaf in leaves.EnumerateArray())
        {
            if (leaf.ValueKind != JsonValueKind.Object ||
                !leaf.TryGetProperty("catalogEntry", out var catalog)) continue;
            // NuGet registrations may inline catalog metadata or link to it.
            if (catalog.ValueKind != JsonValueKind.Object ||
                !TryGetString(catalog, "@id", out var catalogUrl)) continue;
            if (TryGetString(catalog, "id", out var actualId) &&
                !string.Equals(actualId, id, StringComparison.OrdinalIgnoreCase)) continue;
            if (TryGetString(catalog, "version", out var actualVersion))
            {
                if (!string.Equals(actualVersion, version, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            else
            {
                // Require the canonical registration leaf URL when the
                // inlined catalog lacks a version field.
                if (!TryGetString(leaf, "@id", out var leafUrl)) continue;
                var trustedLeaf = TrustedUri(leafUrl,
                    $"/v3/registration5-gz-semver2/{id}/");
                if (!trustedLeaf.AbsolutePath.EndsWith("/" + version + ".json",
                    StringComparison.OrdinalIgnoreCase)) continue;
            }
            return catalogUrl;
        }
        return null;
    }

    private static async Task<string> ReadNugetOrgCatalogHashAsync(Settings settings,
        HttpClient http, string id, string version, long packageSize, CancellationToken ct)
    {
        var root = $"/v3/registration5-gz-semver2/{id}/";
        var indexUrl = TrustedUri(settings.NugetRegistrationUrl.TrimEnd('/') +
            $"/{id}/index.json", root);
        using var index = await ReadJsonAsync(http, indexUrl, ct);
        if (!index.RootElement.TryGetProperty("items", out var pages) ||
            pages.ValueKind != JsonValueKind.Array ||
            pages.GetArrayLength() > MaxRegistrationPages)
            throw new InvalidDataException("NuGet registration index invalid or oversized");

        string? catalogUrl = null;
        foreach (var page in pages.EnumerateArray())
        {
            if (page.ValueKind != JsonValueKind.Object) continue;
            if (page.TryGetProperty("items", out var inlined))
                catalogUrl = FindCatalogUrl(inlined, id, version);
            else if (TryGetString(page, "@id", out var pageUrl))
            {
                var trustedPage = TrustedUri(pageUrl, root);
                using var pageDoc = await ReadJsonAsync(http, trustedPage, ct);
                if (pageDoc.RootElement.TryGetProperty("items", out var leafItems))
                    catalogUrl = FindCatalogUrl(leafItems, id, version);
            }
            if (catalogUrl is not null) break;
        }
        if (catalogUrl is null)
            throw new InvalidDataException("NuGet registration catalog entry unavailable");

        var trustedCatalog = TrustedUri(catalogUrl, "/v3/catalog");
        using var doc = await ReadJsonAsync(http, trustedCatalog, ct);
        var entry = doc.RootElement;
        if (!TryGetString(entry, "id", out var catalogId) ||
            !TryGetString(entry, "version", out var catalogVersion) ||
            !string.Equals(catalogId, id, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(catalogVersion, version, StringComparison.OrdinalIgnoreCase) ||
            !TryGetString(entry, "packageHashAlgorithm", out var algorithm) ||
            !string.Equals(algorithm, "SHA512", StringComparison.OrdinalIgnoreCase) ||
            !TryGetString(entry, "packageHash", out var hash) ||
            !NugetDigest.Valid(hash) ||
            !entry.TryGetProperty("packageSize", out var length) ||
            length.ValueKind != JsonValueKind.Number ||
            !length.TryGetInt64(out var expectedLength) ||
            expectedLength != packageSize)
            throw new InvalidDataException("NuGet catalog package identity, length or checksum invalid");
        return hash;
    }
}
