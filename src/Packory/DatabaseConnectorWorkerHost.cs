using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Packory;

/// <summary>
/// Dedicated Linux worker entry point. It never starts the Packory HTTP server,
/// metadata providers, keyring, token verifier, dashboard, or MCP transport.
/// A separate container's UID, mount/PID/network namespaces and cgroup limits
/// are the containment boundary, not this class or a cancellation token.
/// </summary>
public static class DatabaseConnectorWorkerHost
{
    private const int MaximumActiveConnections = 4;

    public static async Task RunAsync()
    {
        var key = DatabaseWorkerProtocol.LoadKey();
        var path = DatabaseWorkerProtocol.SocketPath();
        var policy = DatabaseConnectorPolicy.FromEnvironment();
        if (!policy.Enabled || policy.AllowedTargets.Count == 0)
            throw new InvalidDataException("The isolated database worker requires a nonempty operator policy.");
        var folder = Path.GetDirectoryName(path) ??
            throw new InvalidDataException("The database worker socket directory is missing.");
        if (!Directory.Exists(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The database worker requires a pre-provisioned socket directory.");
        var executor = new DatabaseConnectorExecutor(policy);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var permits = new SemaphoreSlim(MaximumActiveConnections, MaximumActiveConnections);
        try
        {
            // A socket left behind by an uncleanly killed worker is not a
            // listener. Never follow a symlink or create this outside the
            // operator-specified private volume.
            File.Delete(path);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(MaximumActiveConnections);
            while (true)
            {
                var client = await listener.AcceptAsync();
                if (!permits.Wait(0))
                {
                    client.Dispose();
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try { await HandleAsync(client, executor, policy, key); }
                    catch (Exception) { /* A client cannot crash or log secrets from the server. */ }
                    finally { permits.Release(); }
                });
            }
        }
        finally
        {
            listener.Dispose();
            File.Delete(path);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static async Task HandleAsync(Socket socket, DatabaseConnectorExecutor executor,
        DatabaseConnectorPolicy policy, byte[] key)
    {
        // Command.Cancel cannot always interrupt malicious native code. A
        // dedicated watchdog kills this *worker process* if a driver refuses
        // to return; Docker restarts just this worker, never the web process.
        using var done = new ManualResetEventSlim(false);
        var watchdog = new Thread(() =>
        {
            if (!done.Wait(TimeSpan.FromSeconds(45)))
                Environment.Exit(124);
        })
        { IsBackground = true, Name = "Packory DB worker hard deadline" };
        watchdog.Start();
        try
        {
            using (socket)
            using (var stream = new NetworkStream(socket, ownsSocket: false))
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35)))
            {
                DatabaseWorkerRequest request;
                try
                {
                    request = await DatabaseWorkerProtocol.ReadAsync<DatabaseWorkerRequest>(
                        stream, key, DatabaseWorkerProtocol.MaximumRequestBytes, deadline.Token);
                }
                catch (Exception)
                {
                    // In particular, do not answer unauthenticated frames.
                    return;
                }

                DatabaseWorkerResponse response;
                try
                {
                    if (request.Version != DatabaseWorkerProtocol.Version ||
                        request.RequestId is not { Length: 32 } ||
                        !Guid.TryParseExact(request.RequestId, "N", out _) ||
                        request.Definition is null || request.Password is null)
                        throw new InvalidDataException("Invalid worker request.");
                    policy.Validate(request.Definition);
                    if (request.Operation == "probe" && request.Query is null)
                    {
                        await executor.ProbeAsync(request.Definition, request.Password, deadline.Token);
                        response = new(DatabaseWorkerProtocol.Version, request.RequestId, true, null);
                    }
                    else if (request.Operation == "query" && request.Query is not null &&
                             request.Definition.Enabled)
                    {
                        // Revalidate the exact SELECT contract on this side of
                        // the process boundary; no caller-supplied arbitrary SQL.
                        var approved = request.Definition.Queries.SingleOrDefault(
                            q => q.Name == request.Query.Name);
                        if (approved is null ||
                            !JsonSerializer.SerializeToUtf8Bytes(approved, DatabaseWorkerProtocol.JsonOptions)
                                .AsSpan().SequenceEqual(
                                    JsonSerializer.SerializeToUtf8Bytes(request.Query, DatabaseWorkerProtocol.JsonOptions)))
                            throw new InvalidDataException("The worker query contract was not approved.");
                        _ = DatabaseConnectorExecutor.BindArguments(approved.Parameters, request.Arguments);
                        var result = await executor.QueryAsync(request.Definition, approved,
                            request.Arguments, request.Password, deadline.Token);
                        response = new(DatabaseWorkerProtocol.Version, request.RequestId, true, result);
                    }
                    else throw new InvalidDataException("Unsupported database worker operation.");
                }
                catch (Exception)
                {
                    // Never serialize native driver exceptions, SQL, socket
                    // paths, decrypted passwords or stack traces to the client.
                    response = new(DatabaseWorkerProtocol.Version, request.RequestId, false, null);
                }

                try
                {
                    await DatabaseWorkerProtocol.WriteAsync(stream, response, key,
                        DatabaseWorkerProtocol.MaximumResponseBytes, deadline.Token);
                }
                catch (Exception) { /* Client disconnect or reply bound. */ }
            }
        }
        finally
        {
            done.Set();
            watchdog.Join();
        }
    }
}
