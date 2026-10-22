using System.Text.Json;
using System.Text.Json.Nodes;

namespace Packory;

/// <summary>Explicit Packory management API; npm tarballs remain immutable.</summary>
public static class FeedNpmDeprecationEndpoints
{
    public static bool ValidMessage(string? value) =>
        value is { Length: <= 2048 } && !value.Any(char.IsControl);

    public static void Apply(JsonObject versions, IReadOnlyDictionary<string, string> overrides)
    {
        foreach (var (version, message) in overrides)
        {
            if (!FeedNpmEndpoints.ValidVersion(version) || !ValidMessage(message) ||
                versions[version] is not JsonObject manifest)
                throw new InvalidDataException("Invalid private npm deprecation reference.");
            if (message.Length == 0) manifest.Remove("deprecated");
            else manifest["deprecated"] = message;
        }
    }

    public static void Map(WebApplication app) =>
        app.MapPut("/feeds/{feedId}/npm/-/deprecations/{version}/{**name}", async (
            string feedId, string version, string? name, Settings cfg,
            IPackageDatabase db, HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "private, no-store";
            if (Access.RequireFeed(ctx, cfg, feedId, publish: true) is { } denied) return denied;
            if (db.Feed(feedId) is not { Kind: "npm", Mode: "hosted", Visibility: "private", Enabled: true })
                return Results.NotFound();
            name = FeedNpmEndpoints.DecodePath(name);
            if (!Names.Npm(name) || !FeedNpmEndpoints.ValidVersion(version)) return Results.BadRequest();
            if (cfg.DeniesNpm(name!)) return Results.StatusCode(403);
            if (!ctx.Request.HasJsonContentType()) return Results.StatusCode(415);
            try
            {
                var bytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body, 16 * 1024, ctx.RequestAborted);
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                    !root.TryGetProperty("message", out var field) || field.ValueKind != JsonValueKind.String ||
                    !ValidMessage(field.GetString())) return Results.BadRequest();
                if (!db.SetFeedNpmDeprecation(feedId, name!, version, field.GetString()!, Access.Actor(ctx)))
                    return Results.NotFound();
                return Results.Json(new { ok = true, feedId, name, version });
            }
            catch (JsonException) { return Results.BadRequest(); }
            catch (PayloadTooLargeException) { return Results.StatusCode(413); }
        }).RequireRateLimiting("publish");
}
