namespace Packory;

/// <summary>
/// RFC 9728 discovery for the OIDC-backed remote MCP resource.
/// Static Packory API tokens are a separate compatibility mode; without an
/// HTTPS OIDC resource server there is no OAuth authorization server to
/// advertise, and Packory must not fabricate one.
/// </summary>
public static class McpOAuthResourceMetadata
{
    public const string WellKnownPath = "/.well-known/oauth-protected-resource/mcp";
    public const string MinimalScope = "packory.read";

    public static bool Enabled(Settings settings) =>
        settings.McpEnabled && settings.OidcEnabled &&
        Uri.TryCreate(settings.Base, UriKind.Absolute, out var publicBase) &&
        publicBase.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(publicBase.UserInfo) &&
        publicBase.AbsolutePath == "/" &&
        string.IsNullOrEmpty(publicBase.Query) &&
        string.IsNullOrEmpty(publicBase.Fragment) &&
        Uri.TryCreate(settings.OidcAuthority, UriKind.Absolute, out var issuer) &&
        issuer.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(issuer.UserInfo) &&
        string.IsNullOrEmpty(issuer.Fragment) &&
        string.IsNullOrEmpty(issuer.Query);

    public static string Resource(Settings settings) => settings.Base + "/mcp";
    public static string DocumentUrl(Settings settings) => settings.Base + WellKnownPath;

    public static object? Describe(Settings settings) =>
        !Enabled(settings) ? null : new
        {
            resource = Resource(settings),
            authorization_servers = new[] { settings.OidcAuthority! },
            bearer_methods_supported = new[] { "header" },
            scopes_supported = new[] { MinimalScope }
        };

    public static string Challenge(Settings settings) => Enabled(settings)
        ? "Bearer realm=\"Packory MCP\", resource_metadata=\"" + DocumentUrl(settings) +
          "\", scope=\"" + MinimalScope + "\""
        : "Bearer realm=\"Packory MCP\"";

    public static void Map(WebApplication app)
    {
        // The path-specific resource identifier is /mcp, not the entire Packory
        // origin. Serving the metadata publicly is required by RFC 9728.
        app.MapGet(WellKnownPath, (Settings settings, HttpContext ctx) =>
        {
            var description = Describe(settings);
            if (description is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Json(description);
        });
    }
}
