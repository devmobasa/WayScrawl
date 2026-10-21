using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WayScrawl.Core;
using WayScrawl.Platform;

namespace WayScrawl.Application;

// Listens on $XDG_RUNTIME_DIR/wayscrawl/control.sock. The directory is 0700, so only this
// user can connect. Requests are bounded JSON lines; responses are cached by request id
// so a client that retries after a dropped connection gets the original answer.
public sealed class DaemonServer(WayScrawlPaths paths, SessionCoordinator coordinator, Action<string>? log = null)
{
    private readonly Dictionary<string, Task<DaemonResponse>> requests = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<Task> clients = [];

    public static FileStream AcquireLock(WayScrawlPaths paths)
    {
        WayScrawlPaths.EnsurePrivateDirectory(paths.Runtime);
        try
        {
            // FileShare.None takes an exclusive advisory lock on Unix.
            return new FileStream(paths.DaemonLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new InvalidOperationException("Another WayScrawl daemon is already running.");
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        if (File.Exists(paths.Socket)) File.Delete(paths.Socket);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(paths.Socket));
        File.SetUnixFileMode(paths.Socket, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(16);
        log?.Invoke("Listening on " + paths.Socket);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                Socket client = await listener.AcceptAsync(linked.Token);
                lock (clients)
                {
                    clients.RemoveAll(t => t.IsCompleted);
                    clients.Add(Task.Run(() => ServeAsync(client, linked.Token), CancellationToken.None));
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            File.Delete(paths.Socket);
            // Deliver in-flight replies, including the reply to "shutdown" itself.
            Task[] pending;
            lock (clients) pending = clients.ToArray();
            try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { log?.Invoke("Abandoned unanswered clients at shutdown."); }
        }
    }

    private async Task ServeAsync(Socket client, CancellationToken cancellationToken)
    {
        using var _ = client;
        await using var stream = new NetworkStream(client, ownsSocket: false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            string? line = await ReadLineAsync(stream, DaemonCommands.MaxLineBytes, timeout.Token);
            if (line is null) return; // A readiness probe connects and closes without a request.
            DaemonResponse response;
            DaemonRequest? request = null;
            try { request = JsonSerializer.Deserialize<DaemonRequest>(line, WayScrawlJson.Line); }
            catch (JsonException) { }
            if (DaemonCommands.Validate(request) is { } problem)
                response = new(request?.Id ?? "", false, problem, coordinator.Status);
            else
                response = await DispatchAsync(request!);
            byte[] reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, WayScrawlJson.Line) + "\n");
            // Not the shutdown token: the reply to "shutdown" itself must still be delivered.
            using var write = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await stream.WriteAsync(reply, write.Token);
        }
        catch (Exception error) when (error is IOException or SocketException or OperationCanceledException or InvalidDataException)
        {
            log?.Invoke("Client error: " + error.Message);
        }
    }

    private Task<DaemonResponse> DispatchAsync(DaemonRequest request)
    {
        lock (requests)
        {
            if (requests.TryGetValue(request.Id, out var existing)) return existing;
            var task = ExecuteAsync(request);
            requests[request.Id] = task;
            order.Enqueue(request.Id);
            while (order.Count > 256) requests.Remove(order.Dequeue());
            return task;
        }
    }

    private async Task<DaemonResponse> ExecuteAsync(DaemonRequest request)
    {
        await Task.Yield();
        log?.Invoke($"{request.Command} {request.Argument} ({request.Id})");
        if (request.Command == DaemonCommands.Shutdown)
        {
            await coordinator.DisposeAsync();
            await shutdown.CancelAsync();
            return new(request.Id, true, "Daemon stopped.", coordinator.Status);
        }
        try
        {
            var (ok, message) = await coordinator.ExecuteAsync(request, shutdown.Token);
            return new(request.Id, ok, message, coordinator.Status);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log?.Invoke(error.ToString());
            return new(request.Id, false, error.Message, coordinator.Status);
        }
    }

    public static async Task<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        byte[] one = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0) return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
            int newline = Array.IndexOf(one, (byte)'\n', 0, read);
            buffer.Write(one, 0, newline >= 0 ? newline : read);
            if (buffer.Length > maxBytes) throw new InvalidDataException("Line too long.");
            if (newline >= 0) return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }
}

public sealed class DaemonUnavailableException(string message) : Exception(message);

public static class DaemonClient
{
    // daemon names the wayscrawl command that hosts the daemon. It defaults to this process,
    // which is right only for the CLI; the review app passes the CLI it found.
    public static async Task<DaemonResponse> SendAsync(WayScrawlPaths paths, string command, string? argument,
        bool autoStart, CancellationToken cancellationToken, (string File, string[] Prefix)? daemon = null)
    {
        var request = new DaemonRequest(Guid.NewGuid().ToString("N"), command, argument);
        try
        {
            return await SendOnceAsync(paths, request, cancellationToken);
        }
        catch (DaemonUnavailableException) when (autoStart)
        {
            await StartDaemonAsync(paths, cancellationToken, daemon);
            return await SendOnceAsync(paths, request, cancellationToken);
        }
        catch (IOException)
        {
            // The same id is idempotent on the daemon side.
            return await SendOnceAsync(paths, request, cancellationToken);
        }
    }

    private static async Task<DaemonResponse> SendOnceAsync(WayScrawlPaths paths, DaemonRequest request,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(paths.Socket), cancellationToken);
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.ConnectionRefused
                                                or SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported)
        {
            throw new DaemonUnavailableException("The WayScrawl daemon is not running.");
        }
        catch (SocketException) when (!File.Exists(paths.Socket))
        {
            throw new DaemonUnavailableException("The WayScrawl daemon is not running.");
        }
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, WayScrawlJson.Line) + "\n"),
            cancellationToken);
        // Stopping waits for the recorder to finalize; allow for its bounded escalation.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        string line = await DaemonServer.ReadLineAsync(stream, 1024 * 1024, timeout.Token)
            ?? throw new IOException("The daemon closed the connection.");
        return JsonSerializer.Deserialize<DaemonResponse>(line, WayScrawlJson.Line)
            ?? throw new InvalidDataException("Empty daemon response.");
    }

    public static async Task StartDaemonAsync(WayScrawlPaths paths, CancellationToken cancellationToken,
        (string File, string[] Prefix)? daemon = null)
    {
        var (file, prefix) = daemon ?? SelfCommand.Resolve();
        // setsid -f detaches from the caller's session, so closing a terminal or the
        // compositor's exec helper does not hang up the daemon.
        var info = new System.Diagnostics.ProcessStartInfo("setsid") { UseShellExecute = false };
        foreach (string argument in new[] { "-f", file }.Concat(prefix).Append("daemon").Append("--detached"))
            info.ArgumentList.Add(argument);
        using (var launcher = Spawn.Start(info)) await launcher.WaitForExitAsync(cancellationToken);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(paths.Socket))
            {
                try
                {
                    using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await probe.ConnectAsync(new UnixDomainSocketEndPoint(paths.Socket), cancellationToken);
                    return;
                }
                catch (SocketException) { }
            }
            await Task.Delay(100, cancellationToken);
        }
        throw new DaemonUnavailableException($"The daemon did not start; see {paths.DaemonLog}.");
    }
}
