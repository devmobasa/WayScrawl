using System.Security.Cryptography;
using Tmds.DBus.Protocol;

namespace WayScrawl.Portal;

public sealed record PortalResponse(string Path, uint Code, Dictionary<string, VariantValue> Results);

public sealed class PortalException(string message) : Exception(message);

// One portal request: subscribe to Response for the predicted request path before calling,
// accept a response that races the reply, fail if the portal's owner changes, close on cancel.
// Adapted from SharpMarker's PortalRequest (same author), without its logging and clock seams.
internal sealed class PortalRequest(DBusConnection connection) : IDisposable
{
    internal const string PortalService = "org.freedesktop.portal.Desktop";
    internal const string PortalPath = "/org/freedesktop/portal/desktop";
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private const string BusService = "org.freedesktop.DBus";
    private const int MaximumEarlyResponses = 4;

    private readonly TaskCompletionSource<PortalResponse> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock gate = new();
    private readonly Dictionary<string, PortalResponse> early = new(StringComparer.Ordinal);
    private IDisposable? responseWatch, ownerWatch;
    private string? owner, predicted, handle;

    internal static string NewToken() => "wayscrawl_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    internal static string PredictPath(string uniqueName, string token) =>
        $"{PortalPath}/request/{uniqueName.TrimStart(':').Replace('.', '_')}/{token}";

    // createCall receives the handle_token to put in the options. waitForUser lifts the deadline
    // once the request is open, because a chooser waits for a person.
    internal async Task<PortalResponse> RunAsync(Func<string, MessageBuffer> createCall, TimeSpan deadline,
        CancellationToken cancellationToken, bool waitForUser = false)
    {
        string? opened = null;
        try
        {
            using var timer = new CancellationTokenSource(deadline);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
            opened = await StartAsync(createCall, startup.Token);
            return await ended.Task.WaitAsync(waitForUser ? cancellationToken : startup.Token);
        }
        catch (OperationCanceledException)
        {
            if (opened is not null) await CloseAsync(opened);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"The desktop portal did not answer within {deadline.TotalSeconds:0} s.");
        }
    }

    private async Task<string> StartAsync(Func<string, MessageBuffer> createCall, CancellationToken cancellationToken)
    {
        await connection.ConnectAsync().AsTask().WaitAsync(cancellationToken);
        // Subscribe to owner changes before resolving the owner, so a restart in between is seen.
        ownerWatch = await connection.WatchSignalAsync<(string Name, string Old, string New)>(BusService, "/org/freedesktop/DBus",
            BusService, "NameOwnerChanged", static (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (reader.ReadString(), reader.ReadString(), reader.ReadString());
            }, OnOwnerChanged, ObserverFlags.None, emitOnCapturedContext: false).AsTask().WaitAsync(cancellationToken);
        owner = await ResolveOwnerAsync(cancellationToken);
        string token = NewToken();
        lock (gate) predicted = PredictPath(connection.UniqueName ?? throw new PortalException("The bus assigned no unique name."), token);
        responseWatch = await connection.WatchSignalAsync<PortalResponse>(owner, path: null, RequestInterface, "Response",
            static (message, _) =>
            {
                var reader = message.GetBodyReader();
                uint code = reader.ReadUInt32();
                return new PortalResponse(message.PathAsString ?? "", code, reader.ReadDictionaryOfStringToVariantValue());
            }, OnResponse, ObserverFlags.EmitOnConnectionClosed | ObserverFlags.EmitOnConnectionFailed | ObserverFlags.EmitOnReaderFailed,
            emitOnCapturedContext: false).AsTask().WaitAsync(cancellationToken);
        string path = await connection.CallMethodAsync(createCall(token),
            (message, _) => message.GetBodyReader().ReadObjectPathAsString()).WaitAsync(cancellationToken);
        // Responses are filtered by owner; a portal that restarted mid-call would never match.
        if (await ResolveOwnerAsync(cancellationToken) != owner)
            throw new PortalException("The desktop portal restarted during the request.");
        PortalResponse? waiting;
        lock (gate)
        {
            // Older portals return a path of their own; a response already held for it ends the request.
            handle = path;
            early.Remove(path, out waiting);
            early.Clear();
        }
        if (waiting is not null) ended.TrySetResult(waiting);
        return path;
    }

    private void OnResponse(Notification<PortalResponse> notification)
    {
        if (!notification.HasValue)
        {
            ended.TrySetException(new PortalException($"The portal request ended without an answer ({notification.Type})."));
            return;
        }
        var response = notification.Value;
        lock (gate)
        {
            bool ours = handle is { } h ? response.Path == h : response.Path == predicted;
            if (!ours)
            {
                if (handle is null && early.Count < MaximumEarlyResponses) early.TryAdd(response.Path, response);
                return;
            }
        }
        ended.TrySetResult(response);
    }

    private Task<string> ResolveOwnerAsync(CancellationToken cancellationToken) =>
        connection.CallMethodAsync(BusCall("GetNameOwner", PortalService),
            (message, _) => message.GetBodyReader().ReadString()).WaitAsync(cancellationToken);

    private void OnOwnerChanged(Notification<(string Name, string Old, string New)> notification)
    {
        if (notification.HasValue && notification.Value.Name == PortalService && owner is not null &&
            notification.Value.Old == owner && notification.Value.New != owner)
            ended.TrySetException(new PortalException("The desktop portal stopped while the request was open."));
    }

    private async Task CloseAsync(string path)
    {
        try
        {
            await connection.CallMethodAsync(Message(connection, PortalService, path, RequestInterface, "Close")).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception error) when (error is DBusExceptionBase or TimeoutException) { }
    }

    // MessageWriter is a ref struct: messages are built synchronously, then awaited.
    internal static MessageBuffer Message(DBusConnection connection, string service, string path, string @interface, string member)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(service, path, @interface, member);
        return writer.CreateMessage();
    }

    private MessageBuffer BusCall(string member, string name)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(BusService, "/org/freedesktop/DBus", BusService, member, "s");
        writer.WriteString(name);
        return writer.CreateMessage();
    }

    public void Dispose()
    {
        responseWatch?.Dispose();
        ownerWatch?.Dispose();
    }
}
