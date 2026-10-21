namespace WayScrawl.Core;

// One JSON object per line on the daemon's Unix socket. A client reuses the same Id when it
// retries, so a reconnect can never execute a toggle twice.
public sealed record DaemonRequest(string Id, string Command, string? Argument = null);

public sealed record DaemonStatus(
    string State, string? SessionId, long ElapsedUs, OverlayInputMode? Draw,
    string? LastBundle, string? LastError, int Processing, string Backend, int ConfigProblems = 0);

public sealed record DaemonResponse(string Id, bool Ok, string Message, DaemonStatus Status);

public static class DaemonCommands
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Toggle = "toggle";
    public const string Cancel = "cancel";
    public const string Draw = "draw";
    public const string Clear = "clear";
    public const string Undo = "undo";
    public const string Marker = "marker";
    public const string Status = "status";
    public const string Shutdown = "shutdown";

    public static readonly string[] All = [Start, Stop, Toggle, Cancel, Draw, Clear, Undo, Marker, Status, Shutdown];
    public const int MaxLineBytes = 64 * 1024;

    public static string? Validate(DaemonRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 128)
            return "A request needs an id.";
        if (!All.Contains(request.Command)) return "Unknown command: " + request.Command;
        if (request.Command == Draw && request.Argument is not (null or "on" or "off" or "toggle" or "hold" or "release"))
            return "draw accepts on, off, toggle, hold or release.";
        if (request.Argument is { Length: > 1024 }) return "Argument too long.";
        return null;
    }
}
