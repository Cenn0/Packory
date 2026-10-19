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
            "Partial client/protocol matrix; only isolated private hosted NuGet feeds"),
        new("npm", "npm registry", true, true, true, false, "experimental",
            "Partial npm client matrix; no feed-scoped isolation"),
        new("cargo", "Cargo sparse registry", true, false, true, false, "experimental",
            "Hosted only; no crates.io proxy or complete yank/client matrix"),
        new("pypi", "PyPI Simple API", true, false, true, false, "experimental",
            "Hosted only; no PyPI proxy or complete Twine/pip matrix"),
        new("oci", "OCI Distribution v2", true, false, true, false, "experimental",
            "Docker smoke; Helm native compatibility independently gated; Bearer challenge not implemented"),
        new("maven", "Maven 2 repository layout", true, false, false, false, "experimental",
            "Native Maven consumption of hosted release files only; no native deploy, SNAPSHOT or proxy"),
        new("raw", "Packory generic byte API", true, false, false, false, "experimental",
            "Global versioned immutable assets; not a native package protocol or isolated feed"),
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
