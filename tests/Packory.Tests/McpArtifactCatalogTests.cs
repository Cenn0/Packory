using System.Text.Json;
using ModelContextProtocol;
using Npgsql;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class McpArtifactCatalogTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "packory-mcp-catalog-" + Guid.NewGuid().ToString("N"));
        public Settings Settings { get; }
        public Database Database { get; }
        public SkillSecurityStore Security { get; }

        public Fixture()
        {
            Settings = new Settings { DataDirectory = _path, SkillSecurityEnforced = true };
            Database = new Database(Settings);
            Database.Initialize();
            Security = new SkillSecurityStore(Settings);
            Security.Initialize();
        }

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(_path)) Directory.Delete(_path, true);
        }
    }

    [Fact]
    public void TraversesMoreThanOneHundredResultsAndFindsMavenAndRawVersions()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 137; i++)
            fixture.Database.UpsertPackage("raw", "fixture", $"1.0.{i:D3}", "Fixture", "hosted");
        fixture.Database.UpsertPackage("maven", "org.example:fixture", "1.0.0", "Fixture", "hosted");
        string? cursor = null;
        var found = new List<CatalogEntry>();
        do
        {
            var page = McpArtifactCatalog.Search(fixture.Settings, "fixture", limit: 25, cursor: cursor);
            Assert.InRange(page.Items.Count, 1, 25);
            found.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(138, found.Count);
        Assert.Equal(138, found.Select(x => (x.Kind, x.Name, x.Version)).Distinct().Count());
        Assert.Equal("org.example:fixture", found[0].Name);
        var first = McpArtifactCatalog.Versions(fixture.Settings, "raw", "fixture");
        Assert.Equal(100, first.Versions.Count);
        var second = McpArtifactCatalog.Versions(fixture.Settings, "raw", "fixture", cursor: first.NextCursor);
        Assert.Equal(37, second.Versions.Count);
        Assert.Null(second.NextCursor);
        Assert.Single(McpArtifactCatalog.Versions(fixture.Settings, "maven", "org.example:fixture").Versions);
    }

    [Fact]
    public void ApprovalIsAppliedBeforePageLimitAndEachFollowingPage()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 140; i++)
            fixture.Database.AddSkill("hidden-" + i, "1.0.0", "fixture", "synthetic");
        const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        foreach (var name in new[] { "visible-alpha", "visible-beta", "visible-gamma" })
        {
            fixture.Database.AddSkill(name, "1.0.0", "fixture", "synthetic");
            Assert.True(fixture.Security.TryStage(name, "1.0.0", "fixture", digest, "skill-staging/fixture.zip"));
            Assert.True(fixture.Security.TryTransition(name, "1.0.0", digest, "QUEUED", "APPROVED",
                "synthetic", "Synthetic metadata-only fixture"));
        }
        var first = McpArtifactCatalog.Search(fixture.Settings, "fixture", "skill", limit: 1);
        Assert.Equal("visible-alpha", Assert.Single(first.Items).Name);
        Assert.NotNull(first.NextCursor);
        Assert.True(fixture.Security.TryTransition("visible-beta", "1.0.0", digest, "APPROVED", "REVOKED",
            "synthetic", "Synthetic page revocation"));
        var second = McpArtifactCatalog.Search(fixture.Settings, "fixture", "skill", limit: 1, cursor: first.NextCursor);
        Assert.Equal("visible-gamma", Assert.Single(second.Items).Name);
        Assert.Null(second.NextCursor);
    }

    [Fact]
    public void InsertionsBeforeCursorDoNotRepeatEarlierRows()
    {
        using var fixture = new Fixture();
        foreach (var name in new[] { "beta", "delta", "gamma" })
            fixture.Database.UpsertPackage("raw", name, "1.0.0", "fixture", "hosted");
        var first = McpArtifactCatalog.Search(fixture.Settings, "fixture", limit: 1);
        fixture.Database.UpsertPackage("raw", "alpha", "1.0.0", "fixture", "hosted");
        var next = McpArtifactCatalog.Search(fixture.Settings, "fixture", limit: 100, cursor: first.NextCursor);
        Assert.Equal(new[] { "delta", "gamma" }, next.Items.Select(x => x.Name));
        Assert.Null(next.NextCursor);
    }

    [Fact]
    public void CursorsAreScopedToQueryMethodKindAndApprovalMode()
    {
        using var fixture = new Fixture();
        fixture.Database.UpsertPackage("raw", "fixture", "1.0.0", "fixture", "hosted");
        fixture.Database.UpsertPackage("raw", "fixture", "2.0.0", "fixture", "hosted");
        var cursor = McpArtifactCatalog.Search(fixture.Settings, "fixture", limit: 1).NextCursor;
        Assert.NotNull(cursor);
        AssertInvalid(() => McpArtifactCatalog.Search(fixture.Settings, "other", cursor: cursor));
        AssertInvalid(() => McpArtifactCatalog.Search(fixture.Settings, "fixture", "raw", cursor: cursor));
        AssertInvalid(() => McpArtifactCatalog.Versions(fixture.Settings, "raw", "fixture", cursor: cursor));
        fixture.Settings.SkillSecurityEnforced = false;
        AssertInvalid(() => McpArtifactCatalog.Search(fixture.Settings, "fixture", cursor: cursor));
    }

    [Theory]
    [InlineData("")]
    [InlineData("25")]
    [InlineData("../unsafe")]
    [InlineData("e30")]
    public void RejectsInvalidCursors(string cursor)
    {
        using var fixture = new Fixture();
        AssertInvalid(() => McpArtifactCatalog.Search(fixture.Settings, "", cursor: cursor));
    }

    [Fact]
    public void SearchTreatsWildcardsLiterallyAndExcludesPrivateFeedKinds()
    {
        using var fixture = new Fixture();
        fixture.Database.UpsertPackage("raw", "literal", "1.0.0", "marker%_value", "hosted");
        fixture.Database.UpsertPackage("raw", "ordinary", "1.0.0", "markerABCvalue", "hosted");
        fixture.Database.UpsertPackage("feed-nuget", "hidden", "1.0.0", "marker%_value", "hosted");
        Assert.Equal("literal", Assert.Single(McpArtifactCatalog.Search(fixture.Settings, "%_").Items).Name);
        AssertInvalid(() => McpArtifactCatalog.Search(fixture.Settings, "", "feed-nuget"));
        AssertInvalid(() => McpArtifactCatalog.Search(fixture.Settings, "", limit: 0));
        AssertInvalid(() => McpArtifactCatalog.Search(fixture.Settings, "", limit: 101));
        AssertInvalid(() => McpArtifactCatalog.Search(fixture.Settings, new string('q', 201)));
        AssertInvalid(() => McpArtifactCatalog.Search(fixture.Settings, "invalid\nquery"));
    }

    [Fact]
    public void CapabilityResourceTracksImplementedEcosystemsWithoutGatewayClaim()
    {
        var info = JsonDocument.Parse(PackoryMcpResources.CatalogInfo()).RootElement;
        Assert.Equal(RegistryCapabilities.All.Select(x => x.Ecosystem),
            info.GetProperty("ecosystems").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("no remote MCP gateway", info.GetProperty("mode").GetString());
    }

    [CatalogPostgresFact]
    public void PostgreSqlCatalogUsesSamePagingAndReleaseGates()
    {
        var shared = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES");
        var schema = "mcp_catalog_" + Guid.NewGuid().ToString("N")[..16];
        using var admin = new NpgsqlConnection(shared);
        admin.Open();
        using (var create = new NpgsqlCommand("CREATE SCHEMA " + schema, admin)) create.ExecuteNonQuery();
        try
        {
            var settings = new Settings { MetadataProvider = "postgresql", SkillSecurityEnforced = true,
                PostgresConnectionString = new NpgsqlConnectionStringBuilder(shared) { SearchPath = schema }.ConnectionString };
            var database = new PostgresDatabase(settings);
            database.Initialize();
            var security = new SkillSecurityStore(settings);
            security.Initialize();
            for (var i = 0; i < 130; i++) database.UpsertPackage("raw", "fixture", $"1.0.{i:D3}", "fixture", "hosted");
            database.AddSkill("hidden", "1.0.0", "fixture", "synthetic");
            var first = McpArtifactCatalog.Search(settings, "fixture", limit: 100);
            Assert.Equal(100, first.Items.Count);
            var second = McpArtifactCatalog.Search(settings, "fixture", cursor: first.NextCursor);
            Assert.Equal(30, second.Items.Count);
            Assert.Null(second.NextCursor);
            Assert.Empty(McpArtifactCatalog.Search(settings, "", "skill").Items);
            Assert.Single(McpArtifactCatalog.Versions(settings, "raw", "fixture", limit: 1).Versions);
        }
        finally
        {
            using var drop = new NpgsqlCommand("DROP SCHEMA " + schema + " CASCADE", admin);
            drop.ExecuteNonQuery();
        }
    }

    private static void AssertInvalid(Action action) =>
        Assert.Equal(McpErrorCode.InvalidParams, Assert.Throws<McpProtocolException>(action).ErrorCode);
}

public sealed class CatalogPostgresFactAttribute : FactAttribute
{
    public CatalogPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES")))
            Skip = "Set PACKORY_TEST_POSTGRES to run against an isolated PostgreSQL database.";
    }
}
