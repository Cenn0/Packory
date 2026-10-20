using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class McpSkillKeysetProviderTests
{
    private static void Seed(DbConnection database, int total, int approvedFrom)
    {
        using var transaction = database.BeginTransaction();
        using var skill = database.CreateCommand();
        skill.Transaction = transaction;
        skill.CommandText = """
            INSERT INTO skills(name,version,description,updated_at)
            VALUES(@name,@version,@description,@updated)
            """;
        using var security = database.CreateCommand();
        security.Transaction = transaction;
        security.CommandText = """
            INSERT INTO skill_security_revisions
                (name,version,description,sha256,staging_key,state,updated_at)
            VALUES(@name,@version,@description,@digest,@key,@state,@updated)
            """;

        static DbParameter Add(DbCommand command, string name)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = "";
            command.Parameters.Add(parameter);
            return parameter;
        }
        var name1 = Add(skill, "name");
        var version1 = Add(skill, "version");
        var description1 = Add(skill, "description");
        var updated1 = Add(skill, "updated");
        var name2 = Add(security, "name");
        var version2 = Add(security, "version");
        var description2 = Add(security, "description");
        var digest2 = Add(security, "digest");
        var key2 = Add(security, "key");
        var state2 = Add(security, "state");
        var updated2 = Add(security, "updated");

        for (var i = 0; i < total; i++)
        {
            var name = "perf-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            name1.Value = name;
            name2.Value = name;
            version1.Value = "1.0.0";
            version2.Value = "1.0.0";
            description1.Value = "Synthetic bounded catalog fixture";
            description2.Value = "Synthetic bounded catalog fixture";
            digest2.Value = new string('a', 64);
            key2.Value = "synthetic-staging";
            state2.Value = i < approvedFrom ? "QUEUED" : "APPROVED";
            updated1.Value = "2026-01-01T00:00:00.0000000+00:00";
            updated2.Value = "2026-01-01T00:00:00.0000000+00:00";
            skill.ExecuteNonQuery();
            security.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void Exercise(McpSkillKeysetProvider fixture, DbConnection database)
    {
        // A large initial run of queued records precedes the first approved
        // record. Filtering after a SQL page LIMIT would return no results.
        Seed(database, total: 800, approvedFrom: 630);
        var first = fixture.Store.ReadablePage(null, null, 7, true);
        Assert.Equal(7, first.Count);
        Assert.Equal("perf-0630", first[0].Name);
        Assert.Equal("perf-0636", first[^1].Name);
        Assert.Null(fixture.Store.Readable("perf-0001", "1.0.0", true));
        Assert.NotNull(fixture.Store.Readable("perf-0630", "1.0.0", true));
        Assert.NotNull(fixture.Store.Readable("perf-0001", "1.0.0", false));
        Assert.Equal("perf-0000", fixture.Store.ReadablePage(null, null, 3, false)[0].Name);
        Assert.Throws<ArgumentException>(() =>
            fixture.Store.ReadablePage(null, null, 101, true));
        Assert.Throws<ArgumentException>(() =>
            fixture.Store.ReadablePage("perf-0000", null, 5, true));

        // Revoking the cursor row and an earlier approved revision cannot
        // shift a subsequent keyset page or resurrect either revision.
        using (var command = database.CreateCommand())
        {
            command.CommandText = """
                UPDATE skill_security_revisions SET state='REVOKED'
                WHERE name IN ('perf-0631','perf-0636')
                """;
            Assert.Equal(2, command.ExecuteNonQuery());
        }
        var continuation = fixture.Store.ReadablePage(first[^1].Name,
            first[^1].Version, 7, true);
        Assert.Equal("perf-0637", continuation[0].Name);
        Assert.DoesNotContain(continuation, row => row.Name == "perf-0631");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? afterName = null, afterVersion = null;
        var calls = 0;
        while (true)
        {
            var page = fixture.Store.ReadablePage(afterName, afterVersion, 17, true);
            Assert.InRange(page.Count, 0, 17);
            if (page.Count == 0) break;
            foreach (var item in page)
                Assert.True(seen.Add(item.Name + "/" + item.Version));
            var last = page[^1];
            afterName = last.Name;
            afterVersion = last.Version;
            Assert.InRange(++calls, 1, 11);
        }
        Assert.Equal(168, seen.Count);
        Assert.DoesNotContain("perf-0631/1.0.0", seen);
        Assert.DoesNotContain("perf-0636/1.0.0", seen);
    }

    private sealed record McpSkillKeysetProvider(SkillSecurityStore Store);

    [Fact]
    public void SqliteUsesBoundedOrderedApprovedQuery()
    {
        var root = Path.Combine(Path.GetTempPath(), "packory-skill-p7-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var settings = new Settings { DataDirectory = root };
            new Database(settings).Initialize();
            var store = new SkillSecurityStore(settings);
            store.Initialize();
            using var database = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "packory.sqlite") }.ToString());
            database.Open();
            Exercise(new McpSkillKeysetProvider(store), database);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [PostgresRegistryFact]
    public void PostgreSqlMatchesBoundedApprovedKeysetAndRevocationSemantics()
    {
        var shared = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES")!;
        var schema = "packory_p7_" + Guid.NewGuid().ToString("N");
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
                {
                    SearchPath = schema
                }.ConnectionString
            };
            new PostgresDatabase(settings).Initialize();
            var store = new SkillSecurityStore(settings);
            store.Initialize();
            using var database = new NpgsqlConnection(settings.PostgresConnectionString);
            database.Open();
            Exercise(new McpSkillKeysetProvider(store), database);
        }
        finally
        {
            using var drop = new NpgsqlCommand("DROP SCHEMA " + schema + " CASCADE", administrator);
            drop.ExecuteNonQuery();
        }
    }
}
