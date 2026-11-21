using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol;
using Packory;
using Xunit;

namespace Packory.Tests;

public sealed class DatabaseConnectorTests
{
    private const string Root = "/api/v1/admin/database-connectors";
    private const string Admin = "synthetic-database-admin-credential-long";
    private const string Reader = "synthetic-database-reader-credential-long";
    private const string Password = "synthetic DB password;=with spaces";
    private const string Issuer = "https://issuer.example.invalid";
    private const string Audience = "packory-database-fixture";

    private static DatabaseConnectorDefinition Definition(bool enabled = false, long revision = 0) => new(
        "reporting-db", "Synthetic reporting database", "postgresql", "db.example.invalid", 5432,
        "reporting", "packory_reader", "db.example.invalid", null, enabled,
        [new("lookup", "Look up one synthetic row", "SELECT id,name FROM synthetic_view WHERE id=@id",
            [new("id", "int64", true, 100)], 20, 5)], revision);

    private sealed class Executor : IDatabaseConnectorExecutor
    {
        public int Probes;
        public int Queries;
        public string? LastPassword;
        public Exception? Failure;
        public TaskCompletionSource? Entered;
        public TaskCompletionSource? Release;
        public Task ProbeAsync(DatabaseConnectorDefinition definition, string password, CancellationToken cancellationToken)
        { Probes++; LastPassword = password; return Task.CompletedTask; }
        public async Task<DatabaseQueryResult> QueryAsync(DatabaseConnectorDefinition definition,
            DatabaseQueryDefinition query, JsonElement arguments, string password, CancellationToken cancellationToken)
        {
            Queries++; LastPassword = password;
            if (Failure is not null) throw Failure;
            Entered?.TrySetResult();
            if (Release is not null) await Release.Task.WaitAsync(cancellationToken);
            return new(["id", "name"], [new() { ["id"] = "1", ["name"] = "alpha" }], false);
        }
    }

    private sealed class Host(bool enabled = true, string? metadata = null) : WebApplicationFactory<Program>
    {
        public readonly string Data = Path.Combine(Path.GetTempPath(), "packory-database-" + Guid.NewGuid().ToString("N"));
        public Executor Executor { get; } = new();
        private readonly SymmetricSecurityKey key = new(RandomNumberGenerator.GetBytes(32));
        public Settings Configuration { get; private set; } = null!;
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            Directory.CreateDirectory(Data);
            var keyring = Path.Combine(Data, "connector-keyring.json");
            File.WriteAllText(keyring, JsonSerializer.Serialize(new { active = "test",
                keys = new Dictionary<string, string> { ["test"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) } }));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Packory:DataDirectory"] = Data, ["Packory:PublicBaseUrl"] = "http://localhost:8080",
                ["Packory:AdminToken"] = Admin, ["Packory:ReadToken"] = Reader,
                ["Packory:AnonymousRead"] = "false", ["Packory:OidcAuthority"] = Issuer, ["Packory:OidcAudience"] = Audience
            }));
            builder.ConfigureTestServices(services =>
            {
                Configuration = new Settings { DataDirectory = Data, PublicBaseUrl = "http://localhost:8080",
                    AdminToken = Admin, ReadToken = Reader, AnonymousRead = false,
                    OidcAuthority = Issuer, OidcAudience = Audience,
                    MetadataProvider = metadata is null ? "sqlite" : "postgresql", PostgresConnectionString = metadata ?? "" };
                Configuration.Validate();
                services.RemoveAll<Settings>(); services.AddSingleton(Configuration);
                services.RemoveAll<DatabaseConnectorPolicy>();
                services.AddSingleton(new DatabaseConnectorPolicy(["postgresql://db.example.invalid:5432"], enabled: enabled));
                services.RemoveAll<McpConnectorSecrets>(); services.AddSingleton(new McpConnectorSecrets(keyring));
                services.RemoveAll<IDatabaseConnectorExecutor>(); services.AddSingleton<IDatabaseConnectorExecutor>(Executor);
                // Minimal-host settings are captured before WebApplicationFactory replaces DI.
                // Register a real JWT validator for this synthetic issuer, not a trusted-principal stub.
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "DatabaseFixture";
                    options.DefaultChallengeScheme = "DatabaseFixture";
                }).AddJwtBearer("DatabaseFixture", options =>
                {
                    var config = new OpenIdConnectConfiguration { Issuer = Issuer };
                    config.SigningKeys.Add(key);
                    options.MapInboundClaims = false;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(config);
                    options.Events = new JwtBearerEvents { OnTokenValidated = context =>
                    {
                        Access.RecordValidatedJwt(context.HttpContext, context.Principal!);
                        return Task.CompletedTask;
                    } };
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true, ValidIssuer = Issuer, ValidateAudience = true, ValidAudience = Audience,
                        ValidateIssuerSigningKey = true, IssuerSigningKey = key, ValidateLifetime = true,
                        ClockSkew = TimeSpan.Zero
                    };
                });
                services.DisableBackgroundSkillScanning();
            });
        }
        public string Token(string scopes = "packory.read packory.database.read", string? grant = "reporting-db:lookup",
            string audience = Audience, bool expired = false)
        {
            var claims = new List<Claim> { new("scope", scopes), new("sub", "synthetic-agent") };
            if (grant is not null) claims.Add(new("packory_database", grant));
            var token = new JwtSecurityToken(Issuer, audience, claims, DateTime.UtcNow.AddMinutes(-10),
                DateTime.UtcNow.AddMinutes(expired ? -1 : 5), new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
            return new JwtSecurityTokenHandler().WriteToken(token);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(Data)) Directory.Delete(Data, true);
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string? token = Admin, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, SkillAiJson.Options), Encoding.UTF8, "application/json");
        return request;
    }
    private static async Task Create(HttpClient client, bool enabled = false)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Post, Root, body: new DatabaseConnectorChange(Definition(enabled), Password, false)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
    private static Task<McpClient> Connect(HttpClient client, string token, CancellationToken ct) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(client.BaseAddress!, "mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
        }, client), cancellationToken: ct);

    [Fact]
    public async Task Admin_crud_encrypts_credentials_checks_revisions_and_reserves_deleted_identity()
    {
        using var host = new Host(); using var client = host.CreateClient();
        using var anonymous = await client.SendAsync(Request(HttpMethod.Get, Root, null));
        using var read = await client.SendAsync(Request(HttpMethod.Get, Root, Reader));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode); Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
        await Create(client);
        using var listed = await client.SendAsync(Request(HttpMethod.Get, Root));
        var json = await listed.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Password, json); Assert.DoesNotContain("secretCiphertext", json); Assert.Contains("\"hasPassword\":true", json);
        var store = host.Services.GetRequiredService<DatabaseConnectorStore>();
        var record = Assert.IsType<DatabaseConnectorRecord>(store.Get("reporting-db"));
        Assert.Equal(Password, store.Password(record)); Assert.DoesNotContain(Password, record.SecretCiphertext!);
        using var stale = await client.SendAsync(Request(HttpMethod.Put, Root + "/reporting-db", body: new DatabaseConnectorChange(Definition(true, 2), null, false)));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var changed = await client.SendAsync(Request(HttpMethod.Put, Root + "/reporting-db", body:
            new DatabaseConnectorChange(Definition(false, 1) with { Username = "another_reader" }, null, false)));
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode); Assert.Null(store.Get("reporting-db")!.SecretCiphertext);
        using var deletion = await client.SendAsync(Request(HttpMethod.Delete, Root + "/reporting-db?revision=2"));
        Assert.Equal(HttpStatusCode.NoContent, deletion.StatusCode);
        using var recreated = await client.SendAsync(Request(HttpMethod.Post, Root, body: new DatabaseConnectorChange(Definition(), Password, false)));
        Assert.Equal(HttpStatusCode.Conflict, recreated.StatusCode);
        Assert.Contains(store.History("reporting-db"), a => a.Action == "deleted");
    }

    [Fact]
    public async Task Explicit_probe_can_test_disabled_database_but_queries_require_enablement()
    {
        using var host = new Host(); using var client = host.CreateClient(); await Create(client);
        using var probe = await client.SendAsync(Request(HttpMethod.Post, Root + "/reporting-db/probe"));
        Assert.Equal(HttpStatusCode.OK, probe.StatusCode); Assert.Equal(1, host.Executor.Probes);
        var payload = new { query = "lookup", arguments = new { id = 1 } };
        using var denied = await client.SendAsync(Request(HttpMethod.Post, Root + "/reporting-db/query", body: payload));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal(0, host.Executor.Queries);
        using var enable = await client.SendAsync(Request(HttpMethod.Put, Root + "/reporting-db", body: new DatabaseConnectorChange(Definition(true, 1), null, false)));
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        using var allowed = await client.SendAsync(Request(HttpMethod.Post, Root + "/reporting-db/query", body: payload));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode); Assert.Equal(Password, host.Executor.LastPassword);
        Assert.Contains(host.Services.GetRequiredService<DatabaseConnectorStore>().History("reporting-db"), e => e.Action == "query_succeeded");
    }

    [Fact]
    public async Task Typed_argument_errors_are_rejected_before_credential_access_or_runtime_admission()
    {
        using var host = new Host(); using var client = host.CreateClient(); await Create(client, enabled: true);
        // An unavailable keyring must not turn invalid caller input into a
        // credential/provider failure or cause an external attempt.
        File.Delete(Path.Combine(host.Data, "connector-keyring.json"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var mcp = await Connect(client, host.Token(), deadline.Token);
        foreach (var raw in new[] { "{\"id\":\"1\"}", "{\"id\":true}", "{\"id\":1.5}",
                     "{\"id\":9223372036854775808}", "{\"id\":null}" })
        {
            using var arguments = JsonDocument.Parse(raw);
            using var response = await client.SendAsync(Request(HttpMethod.Post, Root + "/reporting-db/query",
                body: new { query = "lookup", arguments = arguments.RootElement }));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var failure = await Assert.ThrowsAsync<McpProtocolException>(async () =>
                await mcp.CallToolAsync("run_database_query", new Dictionary<string, object?>
                { ["connectorId"] = "reporting-db", ["queryName"] = "lookup", ["argumentsJson"] = raw },
                    cancellationToken: deadline.Token));
            Assert.DoesNotContain(Password, failure.Message);
        }
        Assert.Equal(0, host.Executor.Queries);
        Assert.Null(host.Executor.LastPassword);
        Assert.Equal(10, host.Services.GetRequiredService<DatabaseConnectorStore>().History("reporting-db")
            .Count(entry => entry.Action == "invalid_arguments"));
    }

    [Fact]
    public async Task Deployment_disable_unapproved_targets_cross_site_and_malformed_json_fail_closed()
    {
        using var host = new Host(enabled: false); using var client = host.CreateClient(); await Create(client);
        using var probe = await client.SendAsync(Request(HttpMethod.Post, Root + "/reporting-db/probe"));
        Assert.Equal(HttpStatusCode.Forbidden, probe.StatusCode); Assert.Equal(0, host.Executor.Probes);
        using var target = await client.SendAsync(Request(HttpMethod.Put, Root + "/reporting-db", body:
            new DatabaseConnectorChange(Definition(false, 1) with { Host = "metadata.example.invalid" }, null, false)));
        Assert.Equal(HttpStatusCode.BadRequest, target.StatusCode);
        using var crossSiteRequest = Request(HttpMethod.Get, Root); crossSiteRequest.Headers.Add("Origin", "https://hostile.example.invalid");
        using var crossSite = await client.SendAsync(crossSiteRequest); Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        using var malformed = Request(HttpMethod.Post, Root); malformed.Content = new StringContent("{\"definition\":null}", Encoding.UTF8, "application/json");
        using var invalid = await client.SendAsync(malformed); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Native_mcp_signed_oidc_filters_contracts_and_invokes_only_exact_database_query()
    {
        using var host = new Host(); using var client = host.CreateClient(); await Create(client, true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var mcp = await Connect(client, host.Token(), timeout.Token);
        var list = await mcp.CallToolAsync("list_database_queries", new Dictionary<string, object?>(), cancellationToken: timeout.Token);
        var text = JsonSerializer.Serialize(list, SkillAiJson.Options);
        Assert.Contains("lookup", text); Assert.DoesNotContain("synthetic_view", text); Assert.DoesNotContain("db.example.invalid", text);
        Assert.DoesNotContain("packory_reader", text); Assert.DoesNotContain(Password, text);
        var result = await mcp.CallToolAsync("run_database_query", new Dictionary<string, object?>
        { ["connectorId"] = "reporting-db", ["queryName"] = "lookup", ["argumentsJson"] = "{\"id\":1}" }, cancellationToken: timeout.Token);
        Assert.NotEqual(true, result.IsError); Assert.Contains("alpha", JsonSerializer.Serialize(result, SkillAiJson.Options));
        Assert.Equal(1, host.Executor.Queries); Assert.Equal(Password, host.Executor.LastPassword);
        await Assert.ThrowsAsync<McpProtocolException>(() => mcp.CallToolAsync("run_database_query", new Dictionary<string, object?>
        { ["connectorId"] = "reporting-db", ["queryName"] = "different_query", ["argumentsJson"] = "{}" }, cancellationToken: timeout.Token).AsTask());
        Assert.Equal(1, host.Executor.Queries);
    }

    [Theory]
    [InlineData("packory.read", "reporting-db:lookup")]
    [InlineData("packory.read packory.database.read", "reporting-db:*")]
    [InlineData("packory.read packory.database.read", "other-db:lookup")]
    [InlineData("packory.read packory.admin", "reporting-db:lookup")]
    public async Task Native_mcp_missing_scope_or_exact_claim_never_contacts_database(string scopes, string grant)
    {
        using var host = new Host(); using var client = host.CreateClient(); await Create(client, true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var mcp = await Connect(client, host.Token(scopes, grant), timeout.Token);
        await Assert.ThrowsAsync<McpProtocolException>(() => mcp.CallToolAsync("run_database_query", new Dictionary<string, object?>
        { ["connectorId"] = "reporting-db", ["queryName"] = "lookup", ["argumentsJson"] = "{\"id\":1}" }, cancellationToken: timeout.Token).AsTask());
        Assert.Equal(0, host.Executor.Queries);
    }

    [Fact]
    public async Task Wrong_audience_and_expired_signed_tokens_fail_transport_authentication()
    {
        using var host = new Host(); using var client = host.CreateClient();
        foreach (var token in new[] { host.Token(audience: "wrong-resource"), host.Token(expired: true) })
        {
            using var response = await client.SendAsync(Request(HttpMethod.Post, "/mcp", token, new { jsonrpc = "2.0", method = "server/discover", id = 1 }));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        Assert.Equal(0, host.Executor.Queries);
    }

    [Fact]
    public async Task Provider_errors_are_opaque_and_audited_without_sql_credentials_or_arguments()
    {
        using var host = new Host(); using var client = host.CreateClient(); await Create(client, true);
        host.Executor.Failure = new InvalidOperationException("provider failure " + Password + " SELECT secret FROM internal_table");
        using var response = await client.SendAsync(Request(HttpMethod.Post, Root + "/reporting-db/query", body: new { query = "lookup", arguments = new { id = 1 } }));
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain(Password, text); Assert.DoesNotContain("internal_table", text);
        var history = host.Services.GetRequiredService<DatabaseConnectorStore>().History("reporting-db");
        Assert.Contains(history, e => e.Action == "failed"); Assert.DoesNotContain(Password, JsonSerializer.Serialize(history));
    }

    [Fact]
    public async Task Successful_disable_waits_for_inflight_query_and_blocks_later_queries()
    {
        using var host = new Host(); using var client = host.CreateClient(); await Create(client, true);
        host.Executor.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Executor.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = client.SendAsync(Request(HttpMethod.Post, Root + "/reporting-db/query", body: new { query = "lookup", arguments = new { id = 1 } }));
        await host.Executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disabling = client.SendAsync(Request(HttpMethod.Put, Root + "/reporting-db", body: new DatabaseConnectorChange(Definition(false, 1), null, false)));
        host.Executor.Release.SetResult();
        using var result = await running; using var changed = await disabling;
        Assert.Equal(HttpStatusCode.OK, result.StatusCode); Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var denied = await client.SendAsync(Request(HttpMethod.Post, Root + "/reporting-db/query", body: new { query = "lookup", arguments = new { id = 1 } }));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode); Assert.Equal(1, host.Executor.Queries);
    }

    [Theory]
    [InlineData("SELECT 1; DELETE FROM records")]
    [InlineData("WITH x AS (DELETE FROM records RETURNING *) SELECT * FROM x")]
    [InlineData("SELECT * INTO another_table FROM records")]
    [InlineData("SELECT * FROM records FOR UPDATE")]
    [InlineData("SELECT /* comment */ 1")]
    public void Query_policy_rejects_batches_writes_and_ambiguous_templates(string sql) =>
        Assert.Throws<InvalidDataException>(() => DatabaseConnectorPolicy.ValidateQuery(new("lookup", "Fixture", sql, [], 10, 5)));

    [Theory]
    [InlineData("SELECT 1 # hidden MySQL comment")]
    [InlineData("SELECT 1 # comment\\nDROP TABLE records")]
    [InlineData("SELECT 1\\n# comment")]
    public void Query_policy_rejects_mysql_hash_comments_outside_literals(string sql)
    {
        foreach (var provider in new[] { "mysql", "mariadb" })
            Assert.Throws<InvalidDataException>(() => DatabaseConnectorPolicy.ValidateQuery(
                new("lookup", "Fixture", sql, [], 10, 5), provider));
    }

    [Fact]
    public void Query_policy_preserves_hash_characters_inside_sql_string_literals()
    {
        DatabaseConnectorPolicy.ValidateQuery(
            new("lookup", "Fixture", "SELECT '#safe' AS label", [], 10, 5), "mysql");
        DatabaseConnectorPolicy.ValidateQuery(
            new("lookup", "Fixture", "SELECT 'it''s #safe' AS label", [], 10, 5), "mariadb");
    }

    [Fact]
    public void Policy_checks_placeholder_dialects_case_ambiguity_and_denies_network_special_ranges()
    {
        var q = Definition().Queries[0];
        Assert.Throws<InvalidDataException>(() => DatabaseConnectorPolicy.ValidateQuery(q with { Sql = "SELECT :id" }, "postgresql"));
        Assert.Throws<InvalidDataException>(() => DatabaseConnectorPolicy.ValidateQuery(q with
        { Sql = "SELECT @id,@ID", Parameters = [new("id", "int64", true, 100), new("ID", "int64", true, 100)] }));
        var policy = new DatabaseConnectorPolicy(["postgresql://db.example.invalid:5432"], ["127.0.0.0/8", "169.254.0.0/16"]);
        Assert.True(policy.AddressAllowed(IPAddress.Loopback));
        Assert.False(policy.AddressAllowed(IPAddress.Parse("169.254.169.254")));
        Assert.False(policy.AddressAllowed(IPAddress.Parse("::ffff:169.254.169.254")));
        Assert.False(policy.AddressAllowed(IPAddress.Parse("224.0.0.1")));
        Assert.False(new DatabaseConnectorPolicy([]).AddressAllowed(IPAddress.Loopback));
    }
}
