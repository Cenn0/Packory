using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class RawFeedCatalogTests
{
    [Fact]
    public async Task PagesAreBoundedFeedAndQueryScopedAndReauthorizeEveryRequest()
    {
        using var host = new FeedEcosystemHost();
        using var client = host.CreateClient();
        foreach (var feed in new[] { "raw-catalog-a", "raw-catalog-b" })
            await FeedEcosystemHost.Create(client, feed, "raw");
        var a = await FeedEcosystemHost.Grant(client, "raw-catalog-a", false);
        var b = await FeedEcosystemHost.Grant(client, "raw-catalog-b", false);
        var db = host.Services.GetRequiredService<IPackageDatabase>();
        var at = DateTimeOffset.UtcNow.ToString("O");
        foreach (var feed in new[] { "raw-catalog-a", "raw-catalog-b" })
        for (var i = 0; i < 130; i++)
            Assert.True(db.PublishFeedAsset(new FeedAssetEntry(feed, "raw", $"asset-{i:D3}/1.0.0",
                $"asset-{i:D3}", "1.0.0", "application/octet-stream", new string('a', 64), 1, at), "fixture"));
        const string root = "/feeds/raw-catalog-a/raw/";
        Assert.Equal(HttpStatusCode.Unauthorized, await FeedEcosystemHost.Status(client, HttpMethod.Get, root));
        Assert.Equal(HttpStatusCode.Forbidden, await FeedEcosystemHost.Status(client, HttpMethod.Get, root, b.Token));
        Assert.Equal(HttpStatusCode.Forbidden, await FeedEcosystemHost.Status(client, HttpMethod.Get, root, FeedEcosystemHost.Reader));
        foreach (var suffix in new[] { "?limit=0", "?limit=101", "?cursor=bad", "?prefix=%25" })
            Assert.Equal(HttpStatusCode.BadRequest, await FeedEcosystemHost.Status(client, HttpMethod.Get, root + suffix, a.Token));
        string? cursor = null, first = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            using var response = await client.SendAsync(FeedEcosystemHost.Request(HttpMethod.Get,
                root + "?limit=37" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), a.Token));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.InRange(items.Length, 1, 37);
            foreach (var item in items)
            {
                Assert.True(seen.Add(item.GetProperty("name").GetString()!));
                Assert.Contains(root, item.GetProperty("url").GetString());
                Assert.False(item.TryGetProperty("path", out _));
            }
            cursor = json.RootElement.GetProperty("nextCursor").GetString();
            first ??= cursor;
        } while (cursor is not null);
        Assert.Equal(130, seen.Count);
        Assert.NotNull(first);
        Assert.Equal(HttpStatusCode.BadRequest, await FeedEcosystemHost.Status(client, HttpMethod.Get,
            "/feeds/raw-catalog-b/raw/?cursor=" + first, b.Token));
        Assert.Equal(HttpStatusCode.BadRequest, await FeedEcosystemHost.Status(client, HttpMethod.Get,
            root + "?prefix=asset-0&cursor=" + first, a.Token));
        using (var response = await client.SendAsync(FeedEcosystemHost.Request(HttpMethod.Get, root + "?prefix=asset-12", a.Token)))
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(10, json.RootElement.GetProperty("items").GetArrayLength());
        }
        await FeedEcosystemHost.Status(client, HttpMethod.Post, "/api/v1/admin/feeds/raw-catalog-a/deactivate", FeedEcosystemHost.Admin);
        Assert.Equal(HttpStatusCode.NotFound, await FeedEcosystemHost.Status(client, HttpMethod.Get, root + "?cursor=" + first, a.Token));
        await FeedEcosystemHost.Status(client, HttpMethod.Post, "/api/v1/admin/feeds/raw-catalog-a/activate", FeedEcosystemHost.Admin);
        await FeedEcosystemHost.Status(client, HttpMethod.Delete, "/api/admin/tokens/" + a.Id, FeedEcosystemHost.Admin);
        Assert.Equal(HttpStatusCode.Unauthorized, await FeedEcosystemHost.Status(client, HttpMethod.Get, root + "?cursor=" + first, a.Token));
    }

    [Fact]
    public void PostgreSqlPagesUseSameByteOrderAndLiteralPrefix()
    {
        var connection = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var db = new PostgresDatabase(new Settings { MetadataProvider = "postgresql", PostgresConnectionString = connection });
        db.Initialize();
        var feed = "raw-page-" + Guid.NewGuid().ToString("N")[..12];
        var at = DateTimeOffset.UtcNow.ToString("O");
        Assert.True(db.TryCreateDraftFeed(new FeedDefinition(feed, "raw", "hosted", "private", "Synthetic", false, at, at), "fixture"));
        Assert.True(db.TrySetHostedFeedEnabled(feed, "raw", true, "fixture"));
        foreach (var name in new[] { "a-0", "a.0", "a_0", "b-0" })
            Assert.True(db.PublishFeedAsset(new FeedAssetEntry(feed, "raw", name + "/1.0", name, "1.0",
                "application/octet-stream", new string('b', 64), 1, at), "fixture"));
        var first = db.RawFeedPage(feed, null, "", 2);
        Assert.Equal(new[] { "a-0", "a.0" }, first.Select(x => x.Name));
        Assert.Equal(new[] { "a_0", "b-0" }, db.RawFeedPage(feed, first[^1].Path, "", 2).Select(x => x.Name));
        Assert.Equal("a_0", Assert.Single(db.RawFeedPage(feed, null, "a_", 2)).Name);
        Assert.Empty(db.RawFeedPage("another-feed", null, "", 2));
    }
}
