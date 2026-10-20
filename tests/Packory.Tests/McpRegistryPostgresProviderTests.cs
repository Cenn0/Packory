using System.Text.Json;
using Npgsql;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class PostgresRegistryFactAttribute : FactAttribute
{
    public PostgresRegistryFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES")))
            Skip = "Requires the isolated PostgreSQL CI service.";
    }
}

public sealed class McpRegistryPostgresProviderTests
{
    [PostgresRegistryFact]
    public void RegistryCursorOrderingVersionHistoryLatestAndRevocationMatchSqliteContract()
    {
        var shared = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES")!;
        var schema = "packory_mcp_" + Guid.NewGuid().ToString("N");
        using var administrator = new NpgsqlConnection(shared);
        administrator.Open();
        using (var create = new NpgsqlCommand("CREATE SCHEMA " + schema, administrator))
            create.ExecuteNonQuery();
        try
        {
            var settings = new Settings
            {
                MetadataProvider = "postgresql",
                PostgresConnectionString = new NpgsqlConnectionStringBuilder(shared)
                    { SearchPath = schema }.ConnectionString
            };
            var store = new McpRegistryStore(settings);
            store.Initialize();
            McpRegistryManifest Publish(string name, string version)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
                {
                    ["$schema"] = McpRegistryManifest.Schema, ["name"] = name, ["version"] = version,
                    ["description"] = "Synthetic PostgreSQL discovery fixture", ["title"] = "Registry fixture",
                    ["remotes"] = new[] { new { type = "streamable-http", url = "https://mcp.example.org/mcp" } }
                });
                var manifest = McpRegistryManifest.Parse(bytes);
                Assert.True(store.Stage(manifest));
                Assert.True(store.Decide(name, version, manifest.Sha256,
                    "publish", "Synthetic registry review", "test-admin"));
                return manifest;
            }
            var removed = Publish("org.example/a-server", "1.0.0");
            Publish("org.example/b-server", "10.0.0");
            Publish("org.example/b-server", "2.0.0");
            Publish("org.example/b-server", "10.0.0-rc.2");
            Publish("org.example/c-server", "1.0.0");
            var first = store.ListPublishedPage(limit: 1);
            Assert.Equal(removed.Name, Assert.Single(first.Items).Name);
            Assert.NotNull(first.NextCursor);
            Assert.True(store.Decide(removed.Name, removed.Version, removed.Sha256,
                "revoke", "Synthetic registry revoke", "test-admin"));
            // A cursor is portable to another store/process using the same DB.
            var anotherReader = new McpRegistryStore(settings);
            var next = anotherReader.ListPublishedPage(limit: 1, cursor: first.NextCursor);
            Assert.Equal("org.example/b-server", Assert.Single(next.Items).Name);
            Assert.Equal("10.0.0", next.Items[0].Version);
            Assert.Equal("10.0.0", store.FindLatest("org.example/b-server")?.Version);
            var latest = store.ListPublishedPage(limit: 1, version: "latest");
            Assert.Equal("10.0.0", Assert.Single(latest.Items).Version);
            Assert.NotNull(latest.NextCursor);
            Assert.Equal("org.example/c-server", Assert.Single(store.ListPublishedPage(
                limit: 1, version: "latest", cursor: latest.NextCursor).Items).Name);
            var history = store.ListPublishedPage(name: "org.example/b-server", limit: 1);
            Assert.Equal("10.0.0-rc.2", Assert.Single(history.Items).Version);
            Assert.Equal("2.0.0", Assert.Single(store.ListPublishedPage(
                name: "org.example/b-server", limit: 1, cursor: history.NextCursor).Items).Version);
            Assert.Empty(store.ListPublishedPage(search: "%").Items);
            Assert.Equal(4, store.ListPublishedPage(search: "SERVER").Items.Count);
            Assert.Throws<ArgumentException>(() => store.ListPublishedPage(
                search: "different", cursor: first.NextCursor));
            Assert.Null(store.Find(removed.Name, removed.Version));
            Assert.Null(store.FindLatest(removed.Name));
        }
        finally
        {
            using var drop = new NpgsqlCommand("DROP SCHEMA " + schema + " CASCADE", administrator);
            drop.ExecuteNonQuery();
        }
    }
}
