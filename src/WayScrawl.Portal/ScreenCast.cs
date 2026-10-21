using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace WayScrawl.Portal;

// SourceType: 1 monitor, 2 window, 4 virtual. Position and size are logical, when the portal sends them.
public sealed record PortalStream(uint NodeId, uint SourceType, int? X, int? Y, int? Width, int? Height, string? Id,
    ulong? Serial = null);

public sealed record ScreenCastInfo(uint Version, uint SourceTypes, uint CursorModes);

// One ScreenCast portal session, owned for the whole recording: closing it ends the stream.
public sealed class ScreenCastSession : IAsyncDisposable
{
    private const string ScreenCast = "org.freedesktop.portal.ScreenCast";
    private const string SessionInterface = "org.freedesktop.portal.Session";
    private readonly DBusConnection connection;
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? closedWatch, ownerWatch;
    private bool disposed;

    private ScreenCastSession(DBusConnection connection, string handle)
    {
        this.connection = connection;
        Handle = handle;
    }

    public string Handle { get; }
    public PortalStream[] Streams { get; private set; } = [];
    public string? RestoreToken { get; private set; }
    // Completes when the portal or the user ends the session (for example by revoking the share).
    public Task Closed => closed.Task;

    public static async Task<ScreenCastInfo> ProbeAsync(string? busAddress, CancellationToken cancellationToken)
    {
        using var connection = new DBusConnection(busAddress ?? DBusAddress.Session ?? throw new PortalException("No session bus."));
        await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken);
        MessageBuffer Get(string name)
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(PortalRequest.PortalService, PortalRequest.PortalPath, "org.freedesktop.DBus.Properties", "Get", "ss");
            writer.WriteString(ScreenCast);
            writer.WriteString(name);
            return writer.CreateMessage();
        }
        Task<uint> Property(string name) => connection.CallMethodAsync(Get(name),
            (message, _) => message.GetBodyReader().ReadVariantValue().GetUInt32()).WaitAsync(cancellationToken);
        return new(await Property("version"), await Property("AvailableSourceTypes"), await Property("AvailableCursorModes"));
    }

    // Shows the portal's chooser unless a valid restore token lets the portal skip it.
    public static async Task<ScreenCastSession> StartAsync(string? busAddress, uint sourceTypes, string? restoreToken,
        CancellationToken cancellationToken)
    {
        var info = await ProbeAsync(busAddress, cancellationToken);
        var connection = new DBusConnection(busAddress ?? DBusAddress.Session!);
        ScreenCastSession? session = null;
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken);
            var created = await RequestAsync(connection, token =>
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteMethodCallHeader(PortalRequest.PortalService, PortalRequest.PortalPath, ScreenCast, "CreateSession", "a{sv}");
                writer.WriteDictionary(new Dictionary<string, VariantValue>
                {
                    ["handle_token"] = token, ["session_handle_token"] = PortalRequest.NewToken()
                });
                return writer.CreateMessage();
            }, TimeSpan.FromSeconds(10), cancellationToken);
            session = new ScreenCastSession(connection, created.Results["session_handle"].GetString());
            session.closedWatch = await connection.WatchSignalAsync(null, session.Handle, SessionInterface, "Closed",
                _ => session.closed.TrySetResult(), ObserverFlags.None, emitOnCapturedContext: false);
            await session.WatchPortalAsync(cancellationToken);

            var options = new Dictionary<string, VariantValue>
            {
                ["types"] = sourceTypes & info.SourceTypes,
                ["multiple"] = false,
                // Embedded cursor (2) when offered; persist until revoked (2) where version 4+ allows it.
                ["cursor_mode"] = (info.CursorModes & 2) != 0 ? 2u : 1u
            };
            if (info.Version >= 4)
            {
                options["persist_mode"] = 2u;
                if (restoreToken is { Length: > 0 }) options["restore_token"] = restoreToken;
            }
            await RequestAsync(connection, token =>
            {
                options["handle_token"] = token;
                using var writer = connection.GetMessageWriter();
                writer.WriteMethodCallHeader(PortalRequest.PortalService, PortalRequest.PortalPath, ScreenCast, "SelectSources", "oa{sv}");
                writer.WriteObjectPath(session.Handle);
                writer.WriteDictionary(options);
                return writer.CreateMessage();
            }, TimeSpan.FromSeconds(10), cancellationToken);

            var started = await RequestAsync(connection, token =>
            {
                using var writer = connection.GetMessageWriter();
                writer.WriteMethodCallHeader(PortalRequest.PortalService, PortalRequest.PortalPath, ScreenCast, "Start", "osa{sv}");
                writer.WriteObjectPath(session.Handle);
                writer.WriteString("");
                writer.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = token });
                return writer.CreateMessage();
            }, TimeSpan.FromSeconds(15), cancellationToken, waitForUser: true);
            session.Streams = ParseStreams(started.Results);
            session.RestoreToken = started.Results.TryGetValue("restore_token", out var restore) ? restore.GetString() : null;
            if (session.Streams.Length == 0) throw new PortalException("The portal started no stream.");
            return session;
        }
        catch
        {
            if (session is not null) await session.DisposeAsync();
            else connection.Dispose();
            throw;
        }
    }

    // The session also ends when the portal process or the bus connection goes away.
    private async Task WatchPortalAsync(CancellationToken cancellationToken)
    {
        string owner = await connection.CallMethodAsync(NameOwnerCall(), (message, _) => message.GetBodyReader().ReadString())
            .WaitAsync(cancellationToken);
        ownerWatch = await connection.WatchSignalAsync<(string Name, string Old, string New)>("org.freedesktop.DBus",
            "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameOwnerChanged", static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (reader.ReadString(), reader.ReadString(), reader.ReadString());
            }, change =>
            {
                if (!change.HasValue || change.Value.Name == PortalRequest.PortalService && change.Value.Old == owner)
                    closed.TrySetResult();
            }, ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnConnectionFailed, emitOnCapturedContext: false)
            .AsTask().WaitAsync(cancellationToken);
    }

    private MessageBuffer NameOwnerCall()
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "GetNameOwner", "s");
        writer.WriteString(PortalRequest.PortalService);
        return writer.CreateMessage();
    }

    public static PortalStream[] ParseStreams(Dictionary<string, VariantValue> results)
    {
        if (!results.TryGetValue("streams", out var streams)) return [];
        var parsed = new List<PortalStream>();
        for (int i = 0; i < streams.Count; i++)
        {
            var item = streams.GetItem(i);
            var properties = item.GetItem(1).GetDictionary<string, VariantValue>();
            (int, int)? Pair(string key) => properties.TryGetValue(key, out var value) && value.Count == 2
                ? (value.GetItem(0).GetInt32(), value.GetItem(1).GetInt32()) : null;
            var position = Pair("position");
            var size = Pair("size");
            ulong? serial = properties.TryGetValue("pipewire-serial", out var s1) ? s1.GetUInt64()
                : properties.TryGetValue("pipewire_serial", out var s2) ? s2.GetUInt64() : null;
            parsed.Add(new(item.GetItem(0).GetUInt32(),
                properties.TryGetValue("source_type", out var type) ? type.GetUInt32() : 0,
                position?.Item1, position?.Item2, size?.Item1, size?.Item2,
                properties.TryGetValue("id", out var id) ? id.GetString() : null, serial));
        }
        return parsed.ToArray();
    }

    // The restricted PipeWire remote for this session's streams. The caller owns the descriptor.
    public async Task<SafeHandle> OpenPipeWireRemoteAsync(CancellationToken cancellationToken)
    {
        MessageBuffer Call()
        {
            using var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(PortalRequest.PortalService, PortalRequest.PortalPath, ScreenCast, "OpenPipeWireRemote", "oa{sv}");
            writer.WriteObjectPath(Handle);
            writer.WriteDictionary(new Dictionary<string, VariantValue>());
            return writer.CreateMessage();
        }
        return await connection.CallMethodAsync(Call(),
            (message, _) => message.GetBodyReader().ReadHandle<SafeFileHandle>()
                ?? throw new PortalException("The portal returned no PipeWire descriptor.")).WaitAsync(cancellationToken);
    }

    private static async Task<PortalResponse> RequestAsync(DBusConnection connection, Func<string, MessageBuffer> call,
        TimeSpan deadline, CancellationToken cancellationToken, bool waitForUser = false)
    {
        using var request = new PortalRequest(connection);
        var response = await request.RunAsync(call, deadline, cancellationToken, waitForUser);
        return response.Code switch
        {
            0 => response,
            1 => throw new OperationCanceledException("Screen sharing was cancelled at the portal."),
            _ => throw new PortalException("The portal refused the screen-sharing request.")
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            await connection.CallMethodAsync(PortalRequest.Message(connection, PortalRequest.PortalService, Handle, SessionInterface, "Close"))
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception error) when (error is DBusExceptionBase or TimeoutException or ObjectDisposedException) { }
        closedWatch?.Dispose();
        ownerWatch?.Dispose();
        connection.Dispose();
        closed.TrySetResult();
    }
}
