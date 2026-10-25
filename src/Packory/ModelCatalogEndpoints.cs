using System.Text.Json;

namespace Packory;

public static class ModelCatalogEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/models", (int? limit, int? offset,
            Settings settings, ModelCatalogStore store, HttpContext ctx) =>
        {
            if (Access.RequireGlobalRaw(ctx, settings) is { } denied) return denied;
            if (limit is < 1 or > 50 || offset is < 0 or > 10000) return Results.BadRequest();
            var size = limit ?? 25; var start = offset ?? 0;
            var page = store.List(admin: false, size + 1, start);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                items = page.Take(size).Select(x => x.Summary).ToArray(),
                nextOffset = page.Count > size ? start + size : (int?)null,
                qualification = "metadata reviewed; remote model bytes and publisher unverified"
            });
        });

        app.MapGet("/api/v1/model", (string? name, string? version,
            Settings settings, ModelCatalogStore store, HttpContext ctx) =>
        {
            if (Access.RequireGlobalRaw(ctx, settings) is { } denied) return denied;
            if (!ModelDescriptor.ValidName(name) || !Names.Version(version)) return Results.BadRequest();
            var entry = store.Find(name!, version!);
            ctx.Response.Headers.CacheControl = "no-store";
            return entry is null ? Results.NotFound() : Results.Ok(entry.Summary);
        });

        app.MapPost("/api/v1/models/stage", async (Settings settings,
            ModelCatalogStore store, HttpContext ctx) =>
        {
            if (Access.Require(ctx, settings, write: true) is { } denied) return denied;
            if (!ctx.Request.HasJsonContentType() || ctx.Request.ContentLength is > 16384)
                return Results.BadRequest();
            byte[] bytes;
            try { bytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body, 16384, ctx.RequestAborted); }
            catch (PayloadTooLargeException) { return Results.StatusCode(413); }
            ModelDescriptor descriptor;
            try { descriptor = ModelDescriptor.Parse(bytes); }
            catch (InvalidDataException) { return Results.BadRequest(new { error = "Invalid or unsafe model metadata" }); }
            if (Access.RequireNamespace(ctx, settings, "model", descriptor.Name) is { } forbidden)
                return forbidden;
            if (!store.Stage(descriptor, Access.Actor(ctx)))
                return Results.Conflict(new { error = "Immutable model name/version already exists" });
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { descriptor.Name, descriptor.Version,
                descriptorSha256 = descriptor.DescriptorSha256, state = "PENDING",
                bytesVerified = false, publisherVerified = false }, statusCode: 202);
        }).RequireRateLimiting("publish");

        app.MapGet("/api/v1/admin/models", (int? limit, int? offset,
            Settings settings, ModelCatalogStore store, HttpContext ctx) =>
        {
            if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
            if (limit is < 1 or > 50 || offset is < 0 or > 10000) return Results.BadRequest();
            var size = limit ?? 25; var start = offset ?? 0;
            var page = store.List(admin: true, size + 1, start);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { items = page.Take(size).Select(x => x.Summary).ToArray(),
                nextOffset = page.Count > size ? start + size : (int?)null });
        });

        app.MapGet("/api/v1/admin/model", (string? name, string? version,
            Settings settings, ModelCatalogStore store, HttpContext ctx) =>
        {
            if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
            if (!ModelDescriptor.ValidName(name) || !Names.Version(version)) return Results.BadRequest();
            var entry = store.Find(name!, version!, admin: true);
            ctx.Response.Headers.CacheControl = "no-store";
            return entry is null ? Results.NotFound() : Results.Ok(new
            {
                entry = entry.Summary,
                history = store.History(name!, version!)
            });
        });

        app.MapPost("/api/v1/admin/models/decide", async (Settings settings,
            ModelCatalogStore store, HttpContext ctx) =>
        {
            if (Access.RequireAdmin(ctx, settings) is { } denied) return denied;
            if (!ctx.Request.HasJsonContentType() || ctx.Request.ContentLength is > 2048)
                return Results.BadRequest();
            byte[] bytes;
            try { bytes = await BlobStore.ReadBoundedAsync(ctx.Request.Body, 2048, ctx.RequestAborted); }
            catch (PayloadTooLargeException) { return Results.StatusCode(413); }
            try
            {
                using var document = JsonDocument.Parse(bytes,
                    new JsonDocumentOptions { MaxDepth = 4, AllowTrailingCommas = false });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    root.EnumerateObject().Count() != 5 ||
                    root.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 5 ||
                    root.EnumerateObject().Any(x => x.Name is not
                        ("name" or "version" or "descriptorSha256" or "action" or "reason") ||
                        x.Value.ValueKind != JsonValueKind.String)) return Results.BadRequest();
                var name = root.GetProperty("name").GetString()!;
                var version = root.GetProperty("version").GetString()!;
                var digest = root.GetProperty("descriptorSha256").GetString()!;
                var action = root.GetProperty("action").GetString()!;
                var reason = root.GetProperty("reason").GetString()!;
                if (!store.Decide(name, version, digest, action, reason, Access.Actor(ctx)))
                    return Results.Conflict(new { error = "State, descriptor digest or decision changed" });
                ctx.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new { name, version,
                    state = action == "publish" ? "PUBLISHED" : "REVOKED",
                    bytesVerified = false, publisherVerified = false });
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException)
            { return Results.BadRequest(); }
        }).RequireRateLimiting("publish");
    }
}
