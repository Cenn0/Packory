namespace Packory;

/// <summary>
/// Machine-readable, deliberately conservative capability inventory.
/// Implemented protocols are not blanket vendor certification or production approval.
/// </summary>
public static class RegistryCapabilities
{
    public sealed record Capability(
        string Ecosystem, string Transport, bool Hosted, bool Proxy,
        bool NativePublish, bool IsolatedHostedFeed, string Level,
        string? Limitations);

    public static readonly IReadOnlyList<Capability> All =
    [
        new("nuget", "NuGet V3", true, true, true, true, "experimental",
            "Private hosted feeds include scoped search and paginated registration; no symbols, unlisting or broad client matrix"),
        new("npm", "npm registry", true, true, true, true, "experimental",
            "Private hosted feeds with mutable scoped dist-tags; no private proxy, unpublish or broader native-client qualification"),
        new("cargo", "Cargo sparse registry", true, false, true, true, "experimental",
            "Private hosted sparse feeds; no private yank or crates.io proxy; bounded client matrix"),
        new("pypi", "PyPI Simple API", true, false, true, true, "experimental",
            "Private hosted Simple API and Twine uploads; canonical bounded PEP 440 profile; no upstream proxy"),
        new("oci", "OCI Distribution v2", true, false, true, true, "experimental",
            "Private feeds use reserved feeds/{feedId} repository prefix; pinned Helm client; no Bearer token service"),
        new("maven", "Maven 2 repository layout", true, false, true, true, "experimental",
            "Hosted releases and timestamped SNAPSHOT deploy with scoped metadata, parent-inherited POM coordinates and plugin-prefix metadata; no proxy or atomic multi-file deployment"),
        new("go", "Go GOPROXY hosted", true, false, false, true, "native-client-tested",
            "Packory-specific import and Go native consumer; isolated hosted feeds; no mirror, pseudo-versions or full ZIP parity"),
        new("raw", "Packory generic byte API", true, false, false, true, "experimental",
            "Global and private hosted immutable assets; not a native package protocol"),
        new("skill", "Versioned Agent Skill ZIP import", true, false, false, false, "experimental",
            "Requires approval; static analysis cannot prove runtime safety")
    ];

    public static void Map(WebApplication app) =>
        app.MapGet("/api/v1/registry-capabilities", (Settings settings, HttpContext ctx) =>
        {
            // Only immutable public capability descriptions, not feed names or tenant data.
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new
            {
                apiVersion = "v1",
                releaseQualification = "experimental",
                productionRecommended = false,
                protocols = All
            });
        });
}
