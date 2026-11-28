using System.Data.Common;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Packory;

public sealed class DatabaseConnectorExecutionException : Exception
{
    public DatabaseConnectorExecutionException() : base("Database connector execution failed.") { }
}

/// <summary>One admission and current policy check for REST probes and MCP query calls.</summary>
public sealed class DatabaseConnectorService(Settings settings, DatabaseConnectorPolicy policy,
    DatabaseConnectorStore store, IDatabaseConnectorExecutor executor, ConnectorOperationGate gates,
    PostgresSingleWriterLease writerLease) : IDisposable
{
    private readonly SemaphoreSlim concurrency = new(4, 4);

    public void RequireReady()
    {
        if (!policy.Enabled || settings.InstanceRole == "reader" || settings.RecoveryMaintenance || !writerLease.IsHeld())
            throw new UnauthorizedAccessException("Database connector execution requires an enabled active writer.");
    }

    public async Task ProbeAsync(string id, string actor, CancellationToken cancellationToken)
    {
        RequireReady();
        using var gate = await gates.EnterAsync("database:" + id, cancellationToken);
        RequireReady();
        var connector = store.Get(id) ?? throw new UnauthorizedAccessException("Connector unavailable.");
        await ExecuteAsync(connector, null, actor, async ct =>
        {
            await executor.ProbeAsync(connector.Definition, store.Password(connector), ct);
            return true;
        }, cancellationToken);
    }

    public async Task<DatabaseQueryResult> QueryAsync(string id, string name, JsonElement arguments,
        string actor, CancellationToken cancellationToken)
    {
        RequireReady();
        using var gate = await gates.EnterAsync("database:" + id, cancellationToken);
        RequireReady();
        var connector = store.Get(id);
        var query = connector?.Definition.Queries.SingleOrDefault(q => q.Name == name);
        if (connector is null || !connector.Definition.Enabled || query is null)
        {
            store.Record(id, name, "denied", actor);
            throw new UnauthorizedAccessException("Connector or query unavailable.");
        }
        // Apply the same typed contract at admission and in the native executor.
        // Invalid caller values must be a client error, before credential access,
        // rather than being normalized as an opaque provider failure.
        try
        {
            if (arguments.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(arguments.GetRawText()) > 32768)
                throw new InvalidDataException("Query arguments require a bounded object.");
            _ = DatabaseConnectorExecutor.BindArguments(query.Parameters, arguments);
        }
        catch (InvalidDataException)
        {
            store.Record(id, name, "invalid_arguments", actor);
            throw;
        }
        return await ExecuteAsync(connector, name, actor,
            ct => executor.QueryAsync(connector.Definition, query, arguments, store.Password(connector), ct),
            cancellationToken);
    }

    private async Task<T> ExecuteAsync<T>(DatabaseConnectorRecord connector, string? query,
        string actor, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        if (!await concurrency.WaitAsync(0, cancellationToken)) throw new ConnectorBusyException();
        try
        {
            RequireReady();
            policy.Validate(connector.Definition);
            store.Record(connector.Definition.Id, query, query is null ? "probe_started" : "query_started", actor);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            try
            {
                var result = await operation(deadline.Token);
                // Fail closed on loss of writer authority, including audit and returned data.
                RequireReady();
                store.Record(connector.Definition.Id, query, query is null ? "probe_succeeded" : "query_succeeded", actor);
                return result;
            }
            catch (OperationCanceledException)
            {
                if (writerLease.IsHeld()) store.Record(connector.Definition.Id, query,
                    cancellationToken.IsCancellationRequested ? "cancelled" : "timeout", actor);
                throw;
            }
            catch (Exception e) when (e is DbException or IOException or AuthenticationException or
                InvalidDataException or ArgumentException or InvalidOperationException or FormatException or
                CryptographicException or McpConnectorKeyringException or TimeoutException)
            {
                if (writerLease.IsHeld()) store.Record(connector.Definition.Id, query, "failed", actor);
                // Provider diagnostics may contain endpoints, SQL or passwords. Never propagate them.
                throw new DatabaseConnectorExecutionException();
            }
        }
        finally { concurrency.Release(); }
    }

    public void Dispose() => concurrency.Dispose();
}
