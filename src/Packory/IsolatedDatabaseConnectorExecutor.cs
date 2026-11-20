using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Packory;

/// <summary>
/// The only executor registered in the Packory web process. It cannot create a
/// DbConnection or load a native provider; all driver calls happen in the
/// separately sandboxed Linux worker container over a private Unix socket.
/// Missing IPC configuration or a crashed worker always fails closed.
/// </summary>
public sealed class IsolatedDatabaseConnectorExecutor : IDatabaseConnectorExecutor
{
    public async Task ProbeAsync(DatabaseConnectorDefinition definition, string password,
        CancellationToken cancellationToken)
    {
        using var empty = JsonDocument.Parse("{}");
        var request = NewRequest("probe", definition, null, empty.RootElement, password);
        var reply = await InvokeAsync(request, cancellationToken);
        if (!reply.Succeeded || reply.Result is not null)
            throw new DatabaseConnectorExecutionException();
    }

    public async Task<DatabaseQueryResult> QueryAsync(DatabaseConnectorDefinition definition,
        DatabaseQueryDefinition query, JsonElement arguments, string password,
        CancellationToken cancellationToken)
    {
        // The native implementation performs the same check independently.
        // Admission must never send an invalid caller argument to the worker.
        _ = DatabaseConnectorExecutor.BindArguments(query.Parameters, arguments);
        var request = NewRequest("query", definition, query, arguments, password);
        var reply = await InvokeAsync(request, cancellationToken);
        if (!reply.Succeeded || reply.Result is null)
            throw new DatabaseConnectorExecutionException();
        try
        {
            DatabaseWorkerProtocol.ValidateResult(reply.Result, query, password);
            return reply.Result;
        }
        catch (InvalidDataException)
        {
            // Treat compromised-worker protocol violations as opaque failures.
            throw new DatabaseConnectorExecutionException();
        }
    }

    private static DatabaseWorkerRequest NewRequest(string operation,
        DatabaseConnectorDefinition definition, DatabaseQueryDefinition? query,
        JsonElement arguments, string password) =>
        new(DatabaseWorkerProtocol.Version, Guid.NewGuid().ToString("N"), operation,
            definition, query, arguments, password);

    private static async Task<DatabaseWorkerResponse> InvokeAsync(DatabaseWorkerRequest request,
        CancellationToken cancellationToken)
    {
        byte[]? key = null;
        try
        {
            var path = DatabaseWorkerProtocol.SocketPath();
            key = DatabaseWorkerProtocol.LoadKey();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(35));
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), deadline.Token);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            await DatabaseWorkerProtocol.WriteAsync(stream, request, key,
                DatabaseWorkerProtocol.MaximumRequestBytes, deadline.Token);
            var reply = await DatabaseWorkerProtocol.ReadAsync<DatabaseWorkerResponse>(
                stream, key, DatabaseWorkerProtocol.MaximumResponseBytes, deadline.Token);
            if (reply.Version != DatabaseWorkerProtocol.Version ||
                !string.Equals(reply.RequestId, request.RequestId, StringComparison.Ordinal))
                throw new InvalidDataException("Database worker response correlation failed.");
            return reply;
        }
        catch (OperationCanceledException)
        {
            // The REST/MCP boundary owns the generic timeout response.
            throw;
        }
        catch (Exception e) when (e is IOException or SocketException or InvalidDataException or
            UnauthorizedAccessException or CryptographicException or JsonException or
            InvalidOperationException or ArgumentException)
        {
            // No socket path, SQL, host, driver error or credential reaches an API caller.
            throw new DatabaseConnectorExecutionException();
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }
}
