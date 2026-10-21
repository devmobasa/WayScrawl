using System.Text.Json;
using WayScrawl.Core;

namespace WayScrawl.Platform.Hyprland;

public sealed record HyprMonitor(string Name, string Description, int X, int Y, int Width, int Height,
    double Scale, int Transform, bool Focused, bool Disabled)
{
    // Hyprland lays outputs out in logical coordinates: pixel size divided by scale.
    public double LogicalWidth => (Transform % 2 == 0 ? Width : Height) / Scale;
    public double LogicalHeight => (Transform % 2 == 0 ? Height : Width) / Scale;

    public Geometry ToGeometry(string id, long fromUs = 0) =>
        new(id, X, Y, LogicalWidth, LogicalHeight, Width, Height, "monitor:" + Name, Transform, fromUs);
}

// Identifies one compositor instance; the default is the session's own (environment variables).
public sealed record HyprlandInstance(string RuntimeDirectory, string Signature)
{
    public static HyprlandInstance? Current =>
        System.Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime &&
        System.Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE") is { Length: > 0 } signature
            ? new(runtime, signature) : null;

    public IReadOnlyDictionary<string, string> Environment =>
        new Dictionary<string, string> { ["XDG_RUNTIME_DIR"] = RuntimeDirectory, ["HYPRLAND_INSTANCE_SIGNATURE"] = Signature };
}

public static class HyprlandIpc
{
    public static string? InstanceDirectory(HyprlandInstance? instance = null)
    {
        instance ??= HyprlandInstance.Current;
        string? signature = instance?.Signature;
        string? runtime = instance?.RuntimeDirectory;
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(runtime) ||
            signature.Contains('/') || signature.Contains(".."))
            return null;
        return Path.Combine(runtime, "hypr", signature);
    }

    public static string? EventSocket(HyprlandInstance? instance = null) => InstanceDirectory(instance) is { } directory
        ? Path.Combine(directory, ".socket2.sock") : null;

    public static async Task<HyprMonitor[]> MonitorsAsync(CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunCheckedAsync("hyprctl", ["-j", "monitors"], cancellationToken);
        return ParseMonitors(result.Text);
    }

    public static HyprMonitor[] ParseMonitors(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(m => new HyprMonitor(
            m.GetProperty("name").GetString() ?? "",
            m.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
            m.GetProperty("x").GetInt32(), m.GetProperty("y").GetInt32(),
            m.GetProperty("width").GetInt32(), m.GetProperty("height").GetInt32(),
            m.GetProperty("scale").GetDouble(), m.GetProperty("transform").GetInt32(),
            m.TryGetProperty("focused", out var f) && f.GetBoolean(),
            m.TryGetProperty("disabled", out var x) && x.GetBoolean())).ToArray();
    }

    // "focused" (or empty) selects the focused output; otherwise an exact connector name.
    public static HyprMonitor Select(HyprMonitor[] monitors, string output)
    {
        var enabled = monitors.Where(m => !m.Disabled).ToArray();
        var match = output is "" or "focused"
            ? enabled.FirstOrDefault(m => m.Focused) ?? enabled.FirstOrDefault()
            : enabled.FirstOrDefault(m => m.Name == output);
        return match ?? throw new InvalidOperationException(
            $"Output '{output}' was not found. Available: {string.Join(", ", enabled.Select(m => m.Name))}.");
    }

    // Runs Lua inside the compositor. Used only for the session-scoped telemetry bridge.
    public static async Task EvalAsync(string lua, CancellationToken cancellationToken, HyprlandInstance? instance = null)
    {
        var result = await ProcessRunner.RunCheckedAsync("hyprctl", ["eval", lua], cancellationToken,
            (instance ?? HyprlandInstance.Current)?.Environment);
        string reply = result.Text.Trim();
        if (reply != "ok" && reply.Length > 0)
            throw new InvalidOperationException("Hyprland rejected the Lua bridge: " + ProcessRunner.LastLine(reply));
    }
}
