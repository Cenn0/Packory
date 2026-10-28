using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Packory;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Packory.Tests;

/// <summary>
/// Synthetic, no-network qualification. This does not certify a real database
/// deployment; PostgreSQL TLS, role and readback need a separate live fixture.
/// </summary>
public sealed class McpManagedConnectorTests : IDisposable
{
    private const string Credential =
        "Host=db.internal;Port=5432;Database=fixture;Username=fixture_reader;Password=synthetic-not-real;SSL Mode=VerifyFull";
    private readonly string folder = Path.Combine(Path.GetTempPath(),
        "packory-mcp-connectors-" + Guid.NewGuid().ToString("N"));
    private readonly Settings settings;
    private readonly McpConnectorDeployment deployment;
    private readonly McpConnectorSecrets secrets;
    private readonly McpConnectorStore store;

    private static McpManagedQuery SafeQuery => new()
    {
        Name = "order_lookup",
        Description = "Read one order fixture",
        Sql = "SELECT id FROM public.safe_orders WHERE id = @id",
        Parameters = new Dictionary<string, string> { ["id"] = "int64" },
        MaxRows = 10
    };

    private static McpManagedConnector Policy => new()
    {
        Id = "orders",
        Description = "Order read view",
        Enabled = true,
        AllowedSubjects = ["subject-1"],
        Queries = [SafeQuery]
    };

    public McpManagedConnectorTests()
    {
        Directory.CreateDirectory(folder);
        var ring = Path.Combine(folder, "keys.json");
        File.WriteAllText(ring, JsonSerializer.Serialize(new
        {
            active = "key1",
            keys = new Dictionary<string, string>
            {
                ["key1"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }
        }));
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(ring, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        deployment = new McpConnectorDeployment
        {
            KeyRingFile = ring,
            AllowedHosts = ["db.internal"]
        };
        deployment.Validate();
        secrets = new McpConnectorSecrets(deployment);
        secrets.VerifyReady();
        settings = new Settings
        {
            MetadataProvider = "sqlite",
            DataDirectory = folder,
            McpEnabled = true,
            McpConnectorsEnabled = true,
            OidcAuthority = "https://issuer.example.test/realms/fixture",
            OidcAudience = "packory"
        };
        store = new McpConnectorStore(settings, secrets, deployment);
        store.Initialize();
    }

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    [Fact]
    public void AllConnectorCredentialsAreEncryptedAndBoundToTheExactId()
    {
        var saved = store.Save("orders", new(Policy, Credential, false, "Synthetic test change"),
            "synthetic-operator");
        Assert.Equal(1, saved.Definition.Revision);
        Assert.True(saved.HasCredential);
        var stored = store.Get("orders")!;
        Assert.NotNull(stored.Ciphertext);
        Assert.DoesNotContain("synthetic-not-real", stored.Ciphertext);
        Assert.DoesNotContain("db.internal", stored.Ciphertext);
        var plain = secrets.Decrypt(stored.Ciphertext!, "orders");
        Assert.Contains("Host=db.internal", plain);
        Assert.ThrowsAny<CryptographicException>(() =>
            secrets.Decrypt(stored.Ciphertext!, "other"));
        Assert.DoesNotContain("synthetic-not-real", JsonSerializer.Serialize(store.List()));
        Assert.DoesNotContain("synthetic-not-real", JsonSerializer.Serialize(store.History()));
    }

    [Fact]
    public void AdminRevisionAndRevocationAreEnforcedAndAudited()
    {
        var first = store.Save("orders", new(Policy, Credential, false, "Initial policy"),
            "synthetic-operator");
        Assert.Throws<InvalidOperationException>(() =>
            store.Save("orders", new(Policy, null, false, "Stale policy"), "synthetic-operator"));
        var revised = store.Save("orders", new(first.Definition with { Description = "Revised" },
            null, false, "Update policy"), "synthetic-operator");
        Assert.Equal(2, revised.Definition.Revision);
        Assert.True(store.Revoke("orders", "synthetic-operator"));
        var revoked = store.Get("orders")!;
        Assert.False(revoked.Definition.Enabled);
        Assert.Null(revoked.Ciphertext);
        Assert.False(store.Revoke("missing", "synthetic-operator"));
        Assert.Contains(store.History(), item => item.Decision == "revoked");
    }

    [Theory]
    [InlineData("WITH deleted AS (DELETE FROM users RETURNING *) SELECT * FROM deleted")]
    [InlineData("DELETE FROM users")]
    [InlineData("SELECT * FROM orders; DROP TABLE orders")]
    [InlineData("SELECT * FROM orders -- hidden query")]
    [InlineData("SELECT * FROM orders /* hidden query */")]
    public void RejectsUnapprovedSqlShapes(string sql)
    {
        Assert.Throws<InvalidDataException>(() =>
            McpConnectorPolicy.Validate(Policy with
            {
                Queries = [SafeQuery with { Sql = sql, Parameters = new() }]
            }));
    }

    [Fact]
    public void QueryParameterContractRejectsMismatchesAndUnsupportedTypes()
    {
        Assert.Throws<InvalidDataException>(() =>
            McpConnectorPolicy.Validate(Policy with
            {
                Queries = [SafeQuery with { Parameters = new() }]
            }));
        Assert.Throws<InvalidDataException>(() =>
            McpConnectorPolicy.Validate(Policy with
            {
                Queries = [SafeQuery with
                {
                    Parameters = new() { ["id"] = "shell" }
                }]
            }));
        Assert.Throws<InvalidDataException>(() =>
            McpPostgresConnectorExecutor.Parse("int64", "1; DROP TABLE users"));
        Assert.Throws<InvalidDataException>(() =>
            McpPostgresConnectorExecutor.Parse("uuid", "malformed"));
        Assert.Equal(42L, McpPostgresConnectorExecutor.Parse("int64", "42").Value);
    }

    [Fact]
    public void OnlyPinnedTlsEndpointsAndSanitizedNpgsqlOptionsAreAllowed()
    {
        var normalized = McpConnectorPolicy.NormalizeConnection(Credential +
            ";Options=-c search_path=public;Pooling=true;Timeout=100", deployment);
        Assert.Contains("db.internal", normalized);
        Assert.DoesNotContain("search_path", normalized);
        Assert.DoesNotContain("Timeout=100", normalized);
        Assert.Throws<InvalidDataException>(() =>
            McpConnectorPolicy.NormalizeConnection(Credential.Replace("db.internal", "169.254.169.254"), deployment));
        Assert.Throws<InvalidDataException>(() =>
            McpConnectorPolicy.NormalizeConnection(Credential.Replace("VerifyFull", "Disable"), deployment));
    }

    [Fact]
    public async Task GatewayRequiresVerifiedScopeAndAnExactSubjectGrant()
    {
        store.Save("orders", new(Policy, Credential, false, "Synthetic test change"),
            "synthetic-operator");
        var executor = new FakeExecutor();
        var gateway = new McpConnectorGateway(settings, store, deployment, secrets,
            new McpConnectorQuota(), executor);
        var args = new Dictionary<string, string> { ["id"] = "42" };

        var onlyRead = Principal("subject-1", "packory.read");
        Assert.Equal("connector_not_authorized", Error(await gateway.QueryAsync(onlyRead,
            "orders", "order_lookup", args)));
        Assert.Empty(executor.Calls);

        var otherSubject = Principal("subject-2", "packory.read packory.connectors.read");
        Assert.Empty(JsonSerializer.SerializeToElement(gateway.List(otherSubject))
            .GetProperty("items").EnumerateArray());
        Assert.Equal("connector_unavailable", Error(await gateway.QueryAsync(otherSubject,
            "orders", "order_lookup", args)));
        Assert.Empty(executor.Calls);

        var allowed = Principal("subject-1", "packory.read packory.connectors.read");
        Assert.Single(JsonSerializer.SerializeToElement(gateway.List(allowed))
            .GetProperty("items").EnumerateArray());
        var success = await gateway.QueryAsync(allowed, "orders", "order_lookup", args);
        Assert.Equal("synthetic", JsonSerializer.SerializeToElement(success)
            .GetProperty("proof").GetString());
        Assert.Single(executor.Calls);
        Assert.DoesNotContain("synthetic-not-real", JsonSerializer.Serialize(success));
        Assert.Contains(store.History(), item => item.Decision == "allowed" &&
            !item.PrincipalDigest.Contains("subject-1", StringComparison.Ordinal));

        store.Revoke("orders", "synthetic-operator");
        Assert.Equal("connector_unavailable", Error(await gateway.QueryAsync(allowed,
            "orders", "order_lookup", args)));
        Assert.Single(executor.Calls);
    }

    [Fact]
    public async Task BusyPolicyGateFailsClosedBeforeAnyDatabaseCall()
    {
        store.Save("orders", new(Policy, Credential, false, "Synthetic test change"),
            "synthetic-operator");
        var executor = new FakeExecutor();
        var gateway = new McpConnectorGateway(settings, store, deployment, secrets,
            new McpConnectorQuota(), executor);
        await store.PolicyGate.WaitAsync();
        try
        {
            var result = await gateway.QueryAsync(
                Principal("subject-1", "packory.read packory.connectors.read"),
                "orders", "order_lookup", new() { ["id"] = "1" });
            Assert.Equal("connector_busy", Error(result));
            Assert.Empty(executor.Calls);
        }
        finally { store.PolicyGate.Release(); }
    }

    private static string Error(object result) =>
        JsonSerializer.SerializeToElement(result).GetProperty("error").GetString()!;

    private static HttpContext Principal(string subject, string scope)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Authorization = "Bearer synthetic-validated-principal";
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", subject),
            new Claim("scope", scope)
        ], "synthetic-verified-by-jwt-middleware");
        Access.RecordValidatedJwt(ctx, new ClaimsPrincipal(identity));
        return ctx;
    }

    private sealed class FakeExecutor : IMcpConnectorExecutor
    {
        public List<string> Calls { get; } = [];
        public Task<object> QueryAsync(string connectionString, McpManagedQuery query,
            IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken)
        {
            Calls.Add(query.Name);
            if (!connectionString.Contains("db.internal", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected connector destination.");
            return Task.FromResult<object>(new { proof = "synthetic" });
        }
    }
}
