using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class DatabaseConnectorPostgresFactAttribute : FactAttribute
{
    public DatabaseConnectorPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES")))
            Skip = "Requires the explicit disposable PACKORY_TEST_POSTGRES service; PostgreSQL metadata persistence was not tested in this run.";
    }
}

/// <summary>Real PostgreSQL metadata transactions, independent of outbound database execution.</summary>
public sealed class DatabaseConnectorPostgresProviderTests
{
    private const string Password = "synthetic-database-password-not-real";
    private const string Actor = "synthetic-admin";

    private static DatabaseConnectorDefinition Definition(string id = "reporting-db") => new(
        id, "Synthetic reporting database", "postgresql", "db.example.invalid", 5432,
        "reporting", "packory_reader", "db.example.invalid", null, true,
        [new("approved_query", "Synthetic approved query", "SELECT @id AS value",
            [new("id", "int64", true, 64)], 10, 5)], 0);

    [DatabaseConnectorPostgresFact]
    public void Persisted_credentials_revisions_target_changes_and_tombstones_survive_reopening()
    {
        using var fixture = new MetadataFixture();
        var store = fixture.DatabaseStore();
        store.Initialize();
        var created = store.Save(new(Definition(), Password, false), Actor);
        Assert.Equal(1, created.Definition.Revision);
        Assert.NotNull(created.SecretCiphertext);
        Assert.DoesNotContain(Password, created.SecretCiphertext);
        Assert.Equal(Password, store.Password(created));

        var persistedCipher = fixture.Scalar<string>(
            "SELECT secret_ciphertext FROM database_connectors WHERE id=@id", ("id", created.Definition.Id));
        Assert.Equal(created.SecretCiphertext, persistedCipher);
        Assert.DoesNotContain(Password, fixture.Scalar<string>(
            "SELECT definition_json FROM database_connectors WHERE id=@id", ("id", created.Definition.Id)));
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(created.Summary(), SkillAiJson.Options));

        var reopened = fixture.DatabaseStore();
        reopened.Initialize();
        var persisted = Assert.Single(reopened.List());
        Assert.Equal(created.Definition.Id, persisted.Definition.Id);
        Assert.Equal(created.Definition.Revision, persisted.Definition.Revision);
        Assert.Equal(created.SecretCiphertext, persisted.SecretCiphertext);
        Assert.Equal(Password, reopened.Password(persisted));
        Assert.Equal("created", Assert.Single(reopened.History(persisted.Definition.Id)).Action);

        Assert.Throws<InvalidOperationException>(() => reopened.Save(
            new(Definition() with { Name = "Stale configuration" }, null, false), Actor));
        Assert.Equal(1, reopened.Get(persisted.Definition.Id)!.Definition.Revision);
        Assert.Single(reopened.History(persisted.Definition.Id));

        var moved = reopened.Save(new(persisted.Definition with
        { Host = "other.example.invalid", TlsServerName = "other.example.invalid", Enabled = false },
            null, false), Actor);
        Assert.Equal(2, moved.Definition.Revision);
        Assert.Null(moved.SecretCiphertext);
        Assert.Throws<UnauthorizedAccessException>(() => reopened.Password(moved));
        Assert.Throws<CryptographicException>(() => fixture.Secrets.Unprotect(persisted.SecretCiphertext!,
            "db:" + moved.Definition.Id, DatabaseConnectorPolicy.CredentialContext(moved.Definition)));
        Assert.Throws<InvalidDataException>(() => reopened.Save(
            new(moved.Definition with { Enabled = true }, null, false), Actor));
        Assert.Equal(2, reopened.Get(moved.Definition.Id)!.Definition.Revision);
        Assert.Equal(2, reopened.History(moved.Definition.Id).Count);

        Assert.False(reopened.Delete(moved.Definition.Id, 1, Actor));
        Assert.True(reopened.Delete(moved.Definition.Id, moved.Definition.Revision, Actor));
        Assert.Null(reopened.Get(moved.Definition.Id));
        Assert.Equal(1, fixture.Scalar<long>(
            "SELECT COUNT(*) FROM database_connector_ids WHERE id=@id", ("id", moved.Definition.Id)));
        var afterDelete = fixture.DatabaseStore();
        afterDelete.Initialize();
        Assert.Throws<InvalidOperationException>(() => afterDelete.Save(
            new(Definition(), Password, false), Actor));
        Assert.Empty(afterDelete.List());
        var history = afterDelete.History(moved.Definition.Id);
        Assert.Equal(3, history.Count);
        Assert.Single(history, entry => entry.Action == "deleted");
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(history));
    }

    [DatabaseConnectorPostgresFact]
    public async Task Concurrent_store_instances_commit_one_revision_and_only_the_winning_audit()
    {
        using var fixture = new MetadataFixture();
        var first = fixture.DatabaseStore();
        var second = fixture.DatabaseStore();
        first.Initialize();
        var created = first.Save(new(Definition(), Password, false), Actor);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<(DatabaseConnectorRecord? Saved, Exception? Failure)> Attempt(
            DatabaseConnectorStore store, string suffix)
        {
            await start.Task;
            try
            {
                return (store.Save(new(created.Definition with { Name = suffix },
                    Password + "-" + suffix, false), Actor), null);
            }
            catch (InvalidOperationException exception) { return (null, exception); }
        }
        var attempts = new[] { Attempt(first, "first"), Attempt(second, "second") };
        start.SetResult();
        var outcomes = await Task.WhenAll(attempts);
        var winner = Assert.Single(outcomes, outcome => outcome.Saved is not null).Saved!;
        Assert.IsType<InvalidOperationException>(Assert.Single(outcomes,
            outcome => outcome.Failure is not null).Failure);
        Assert.Equal(2, winner.Definition.Revision);
        var persisted = fixture.DatabaseStore().Get(created.Definition.Id)!;
        Assert.Equal(winner.Definition.Name, persisted.Definition.Name);
        Assert.Equal(2, persisted.Definition.Revision);
        Assert.Equal(Password + "-" + winner.Definition.Name, first.Password(persisted));
        var audit = first.History(created.Definition.Id);
        Assert.Equal(2, audit.Count);
        Assert.Single(audit, entry => entry.Action == "created");
        Assert.Single(audit, entry => entry.Action == "updated");
    }

    [DatabaseConnectorPostgresFact]
    public void Audit_failure_rolls_back_creation_identity_reservation_update_and_deletion()
    {
        using var fixture = new MetadataFixture();
        var store = fixture.DatabaseStore();
        store.Initialize();
        // A database constraint provides a deterministic failure inside the same
        // transaction, after the definition/identity write and before commit.
        fixture.Execute("ALTER TABLE database_connector_audit ADD CONSTRAINT reject_created CHECK(action <> 'created')");
        var createFailure = Assert.Throws<PostgresException>(() => store.Save(
            new(Definition(), Password, false), Actor));
        Assert.Equal(PostgresErrorCodes.CheckViolation, createFailure.SqlState);
        Assert.Null(store.Get("reporting-db"));
        Assert.Equal(0, fixture.Scalar<long>("SELECT COUNT(*) FROM database_connector_ids"));
        Assert.Empty(store.History("reporting-db"));
        fixture.Execute("ALTER TABLE database_connector_audit DROP CONSTRAINT reject_created");
        var created = store.Save(new(Definition(), Password, false), Actor);

        fixture.Execute("ALTER TABLE database_connector_audit ADD CONSTRAINT reject_updated CHECK(action <> 'updated')");
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.Throws<PostgresException>(() => store.Save(
            new(created.Definition with { Name = "Should roll back" }, Password + "-replacement", false), Actor)).SqlState);
        var afterUpdate = store.Get(created.Definition.Id)!;
        Assert.Equal(created.Definition.Name, afterUpdate.Definition.Name);
        Assert.Equal(created.Definition.Revision, afterUpdate.Definition.Revision);
        Assert.Equal(created.SecretCiphertext, afterUpdate.SecretCiphertext);
        Assert.Equal(Password, store.Password(afterUpdate));
        Assert.Single(store.History(created.Definition.Id));
        fixture.Execute("ALTER TABLE database_connector_audit DROP CONSTRAINT reject_updated");

        fixture.Execute("ALTER TABLE database_connector_audit ADD CONSTRAINT reject_deleted CHECK(action <> 'deleted')");
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.Throws<PostgresException>(() =>
            store.Delete(created.Definition.Id, created.Definition.Revision, Actor)).SqlState);
        Assert.NotNull(store.Get(created.Definition.Id));
        Assert.Single(store.History(created.Definition.Id));
        fixture.Execute("ALTER TABLE database_connector_audit DROP CONSTRAINT reject_deleted");
        Assert.True(store.Delete(created.Definition.Id, created.Definition.Revision, Actor));
        Assert.Equal(2, store.History(created.Definition.Id).Count);
        Assert.Equal(1, fixture.Scalar<long>("SELECT COUNT(*) FROM database_connector_ids"));
    }

    [DatabaseConnectorPostgresFact]
    public void A_future_module_schema_fails_without_committing_partial_bootstrap_changes()
    {
        using var fixture = new MetadataFixture();
        fixture.Execute("""
            CREATE TABLE database_connector_schema(slot INTEGER PRIMARY KEY CHECK(slot=1),version INTEGER NOT NULL);
            INSERT INTO database_connector_schema(slot,version) VALUES(1,2);
            CREATE TABLE future_module_state(id INTEGER PRIMARY KEY,payload TEXT NOT NULL);
            INSERT INTO future_module_state(id,payload) VALUES(1,'synthetic future state');
            """);
        var tablesBefore = fixture.TableNames();
        Assert.Throws<InvalidOperationException>(() => fixture.DatabaseStore().Initialize());
        Assert.Equal(tablesBefore, fixture.TableNames());
        Assert.Equal(2, fixture.Scalar<int>("SELECT version FROM database_connector_schema WHERE slot=1"));
        Assert.Equal("synthetic future state", fixture.Scalar<string>("SELECT payload FROM future_module_state WHERE id=1"));
        Assert.Equal(0, fixture.Scalar<long>(
            "SELECT COUNT(*) FROM pg_indexes WHERE schemaname=@schema AND indexname='database_connector_audit_latest'",
            ("schema", fixture.Schema)));
    }

    [DatabaseConnectorPostgresFact]
    public void Remote_mcp_upgrade_reserves_both_live_and_historically_deleted_authorization_ids()
    {
        using var fixture = new MetadataFixture();
        const string endpoint = "https://mcp.example.invalid/mcp";
        const string bearer = "synthetic-remote-bearer-not-real";
        var cipher = fixture.Secrets.Protect(bearer, "legacy-live", endpoint);
        // This is the pre-tombstone schema from the parent feature, including an
        // already-deleted identity which exists only in its retained audit.
        fixture.Execute("""
            CREATE TABLE mcp_connectors (
              id TEXT PRIMARY KEY,name TEXT NOT NULL,endpoint TEXT NOT NULL,enabled INTEGER NOT NULL,
              allowed_tools TEXT NOT NULL,secret_ciphertext TEXT,revision BIGINT NOT NULL,updated_at BIGINT NOT NULL);
            CREATE TABLE mcp_connector_audit (
              id TEXT PRIMARY KEY,connector_id TEXT NOT NULL,action TEXT NOT NULL,actor TEXT NOT NULL,
              tool TEXT,created_at BIGINT NOT NULL);
            """);
        fixture.Execute("""
            INSERT INTO mcp_connectors(id,name,endpoint,enabled,allowed_tools,secret_ciphertext,revision,updated_at)
            VALUES('legacy-live','Synthetic legacy remote',@endpoint,0,'[]',@cipher,3,1);
            INSERT INTO mcp_connector_audit(id,connector_id,action,actor,tool,created_at)
            VALUES('synthetic-deletion','legacy-deleted','delete','synthetic-admin',NULL,1);
            """, ("endpoint", endpoint), ("cipher", cipher));
        var policy = new McpConnectorPolicy(["https://mcp.example.invalid"]);
        var store = new McpConnectorStore(fixture.Settings, policy, fixture.Secrets);
        store.Initialize();
        store.Initialize();
        var live = store.Get("legacy-live")!;
        Assert.Equal(3, live.Revision);
        Assert.Equal(bearer, fixture.Secrets.Unprotect(live.SecretCiphertext!, live.Id, live.Endpoint));
        Assert.Equal(2, fixture.Scalar<long>("SELECT COUNT(*) FROM mcp_connector_ids"));
        Assert.Throws<InvalidOperationException>(() => store.Save(
            new("legacy-deleted", "Synthetic replacement", endpoint, false, [], null, false, 0), Actor));
        Assert.True(store.Delete("legacy-live", live.Revision, Actor));
        var reopened = new McpConnectorStore(fixture.Settings, policy, fixture.Secrets);
        reopened.Initialize();
        Assert.Throws<InvalidOperationException>(() => reopened.Save(
            new("legacy-live", "Synthetic replacement", endpoint, false, [], bearer, false, 0), Actor));
        Assert.Empty(reopened.List());
        Assert.Equal(2, fixture.Scalar<long>("SELECT COUNT(*) FROM mcp_connector_ids"));
        Assert.Equal(2, fixture.Scalar<long>("SELECT COUNT(*) FROM mcp_connector_audit"));
    }

    private sealed class MetadataFixture : IDisposable
    {
        private readonly NpgsqlConnection administrator;
        private readonly string directory = Path.Combine(Path.GetTempPath(),
            "packory-db-metadata-" + Guid.NewGuid().ToString("N"));
        public string Schema { get; } = "packory_db_connector_" + Guid.NewGuid().ToString("N");
        public Settings Settings { get; }
        public McpConnectorSecrets Secrets { get; }
        private readonly DatabaseConnectorPolicy policy = new(
            ["postgresql://db.example.invalid:5432", "postgresql://other.example.invalid:5432"]);

        public MetadataFixture()
        {
            var shared = Environment.GetEnvironmentVariable("PACKORY_TEST_POSTGRES")!;
            administrator = new NpgsqlConnection(shared);
            administrator.Open();
            var schemaCreated = false;
            try
            {
                using (var create = new NpgsqlCommand("CREATE SCHEMA " + Schema, administrator))
                    create.ExecuteNonQuery();
                schemaCreated = true;
                Settings = new Settings
                {
                    MetadataProvider = "postgresql",
                    PostgresConnectionString = new NpgsqlConnectionStringBuilder(shared)
                    { SearchPath = Schema, Pooling = false }.ConnectionString,
                    DataDirectory = directory
                };
                Directory.CreateDirectory(directory);
                var keyring = Path.Combine(directory, "keyring.json");
                File.WriteAllText(keyring, JsonSerializer.Serialize(new
                {
                    active = "fixture-key",
                    keys = new Dictionary<string, string>
                    { ["fixture-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
                }));
                Secrets = new McpConnectorSecrets(keyring);
            }
            catch
            {
                try
                {
                    if (schemaCreated)
                    {
                        using var drop = new NpgsqlCommand("DROP SCHEMA " + Schema + " CASCADE", administrator);
                        drop.ExecuteNonQuery();
                    }
                }
                finally
                {
                    administrator.Dispose();
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }
                throw;
            }
        }

        public DatabaseConnectorStore DatabaseStore() => new(Settings, policy, Secrets);

        public int Execute(string statement, params (string Name, object Value)[] parameters)
        {
            using var connection = new NpgsqlConnection(Settings.PostgresConnectionString);
            connection.Open();
            using var command = new NpgsqlCommand(statement, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return command.ExecuteNonQuery();
        }

        public T Scalar<T>(string statement, params (string Name, object Value)[] parameters)
        {
            using var connection = new NpgsqlConnection(Settings.PostgresConnectionString);
            connection.Open();
            using var command = new NpgsqlCommand(statement, connection);
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return (T)command.ExecuteScalar()!;
        }

        public string[] TableNames()
        {
            using var connection = new NpgsqlConnection(Settings.PostgresConnectionString);
            connection.Open();
            using var command = new NpgsqlCommand(
                "SELECT tablename FROM pg_tables WHERE schemaname=@schema ORDER BY tablename", connection);
            command.Parameters.AddWithValue("schema", Schema);
            using var rows = command.ExecuteReader();
            var result = new List<string>();
            while (rows.Read()) result.Add(rows.GetString(0));
            return result.ToArray();
        }

        public void Dispose()
        {
            try
            {
                using var drop = new NpgsqlCommand("DROP SCHEMA " + Schema + " CASCADE", administrator);
                drop.ExecuteNonQuery();
            }
            finally
            {
                administrator.Dispose();
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }
    }
}
