using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;

namespace Packory;

/// <summary>Explicit Packory management API; npm tarballs remain immutable.</summary>
public static class FeedNpmDeprecationEndpoints
{
    internal static string Revision(JsonObject packument)
    {
        static JsonNode? Canonical(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => KeyValuePair.Create(x.Key, Canonical(x.Value)))),
            JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
            _ => node?.DeepClone()
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(Canonical(packument))));
    }

    internal static async Task<IResult> Native(string feedId, string name, JsonObject submitted,
        Settings cfg, IPackageDatabase db, BlobStore blobs, HttpContext ctx)
    {
        try
        {
            var expected = db.FeedNpmDeprecations(feedId, name);
            var current = await FeedNpmEndpoints.Packument(feedId, name, cfg, db, blobs, ctx.RequestAborted);
            if (current is null) return Results.NotFound();
            if (submitted["_rev"] is not JsonValue revision ||
                !revision.TryGetValue<string>(out var value) || value != current["_rev"]!.GetValue<string>())
                return Results.Conflict(new { error = "Refetch the current npm packument before updating metadata" });
            if (submitted["versions"] is not JsonObject incoming || incoming.Count is < 1 or > 4096)
                return Results.BadRequest();
            var comparison = submitted.DeepClone().AsObject();
            var baseline = current.DeepClone().AsObject();
            var original = baseline["versions"]!.AsObject();
            var changes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (version, node) in incoming)
            {
                if (!FeedNpmEndpoints.ValidVersion(version) || node is not JsonObject manifest ||
                    original[version] is not JsonObject existing) return Results.BadRequest();
                if (manifest.ContainsKey("deprecated"))
                {
                    if (manifest["deprecated"] is not JsonValue field ||
                        !field.TryGetValue<string>(out var message) || !ValidMessage(message))
                        return Results.BadRequest();
                    var previous = existing["deprecated"]?.GetValue<string>() ?? "";
                    if (message != previous) changes.Add(version, message);
                }
                else if (existing.ContainsKey("deprecated")) return Results.BadRequest();
                comparison["versions"]![version]!.AsObject().Remove("deprecated");
                existing.Remove("deprecated");
            }
            // A native metadata PUT may change only deprecation messages. Reject deletion,
            // new versions, dist-tag edits, payload URLs and all other manifest changes.
            if (!JsonNode.DeepEquals(comparison, baseline)) return Results.BadRequest();
            if (changes.Count > 0 && !db.SetFeedNpmDeprecations(feedId, name, changes, expected, Access.Actor(ctx)))
                return Results.Conflict(new { error = "npm metadata changed; retry from a fresh packument" });
            return Results.Json(new { ok = true });
        }
        catch (PayloadTooLargeException) { return Results.StatusCode(413); }
        catch (Exception error) when (error is InvalidDataException or IOException or JsonException)
        { return Results.StatusCode(503); }
    }

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
