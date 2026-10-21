using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using WayScrawl.Core;

namespace WayScrawl.Platform.Hyprland;

// Interaction observations from Hyprland, never claimed as portable Wayland input:
// - socket2 events (window focus, workspaces, window open/close), stamped on arrival;
// - an optional session-scoped Lua bridge that observes mouse buttons and wheel through
//   non-consuming binds. A bind firing is evidence that the compositor saw the button,
//   not that an application received it, so those samples carry fidelity "binding".
// Keyboard keys are never observed.
// geometry is null when the recorded source has no known place in the global layout (a window
// share); click positions are then omitted rather than mapped wrongly.
public sealed class HyprlandTelemetry(Geometry? geometry, bool bridge = true, bool windowTitles = true,
    HyprlandInstance? instance = null) : IInteractionSource
{
    public const string Source = "hyprland";
    private const int MaxLineBytes = 8192;
    private string token = Guid.NewGuid().ToString("N")[..12];
    private long received, dropped, gaps, lastSequence;
    private string detail = "not started";
    private string? lastLayout;
    private long lastLayoutUs = -1_000_000;
    private long? layoutDirtyUs;

    public string Token => token;

    public Capability[] Describe() =>
    [
        new("windowFocus", HyprlandIpc.EventSocket(instance) is null ? Availability.Unsupported : Availability.Available,
            "Hyprland socket2 activewindow/workspace events, timestamped on arrival."),
        new("pointerButtons", bridge ? Availability.Available : Availability.NotImplemented,
            bridge ? "Session-scoped Hyprland Lua bridge with non-consuming binds; observes binds, not application delivery."
                   : "Lua bridge disabled in config."),
        new("keyboard", Availability.Unsupported, "Keystrokes are deliberately not recorded.")
    ];

    public InteractionSourceInfo Statistics => new(Source, "arrival", Interlocked.Read(ref received),
        Interlocked.Read(ref dropped), Interlocked.Read(ref gaps), detail);

    public async IAsyncEnumerable<InteractionSample> ReadAsync(ISessionClock clock,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string socket = HyprlandIpc.EventSocket(instance) ?? throw new InvalidOperationException("Not running under Hyprland.");
        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var lease = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken input = lease.Token;
        Task? renewing = null;
        Task<InteractionSample?>? layoutTask = null;
        if (bridge)
        {
            await EvalBoundedAsync(BridgeScript(token), input);
            lease.CancelAfter(TimeSpan.FromSeconds(14));
            renewing = RenewAsync(renewal.Token, lease);
        }
        detail = bridge ? "socket2 plus Lua bridge " + token : "socket2";
        try
        {
            int attempt = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                using var connection = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await connection.ConnectAsync(new UnixDomainSocketEndPoint(socket), input);
                }
                catch (SocketException) when (attempt < 20)
                {
                    attempt++;
                    await Task.Delay(Math.Min(2000, 100 * attempt), cancellationToken);
                    continue;
                }
                if (attempt > 0)
                    yield return new(clock.ElapsedUs, Source, "arrival", "telemetry.reconnected", "socket2 reconnected", 0);
                attempt = 0;
                await using var stream = new NetworkStream(connection, ownsSocket: false);
                layoutTask ??= LayoutAsync(clock, input, force: true);
                await foreach (string line in LinesAsync(stream, input, cancellationToken))
                {
                    if (line != "__layout_tick__") Interlocked.Increment(ref received);
                    string eventName = line.Split(">>", 2)[0];
                    if (eventName is "activewindow" or "activewindowv2" or "openwindow" or "closewindow" or "movewindowv2" or
                        "changefloatingmode" or "fullscreen" or "workspace" or "workspacev2" or "activespecial" or "focusedmon")
                        layoutDirtyUs = clock.ElapsedUs;
                    if ((bridge) && line.StartsWith("configreloaded>>", StringComparison.Ordinal))
                    {
                        // A reload replaces the Lua state and drops the bridge's binds; install it again.
                        // Yield first: the coordinator releases pointer input before potentially slow IPC.
                        yield return new(clock.ElapsedUs, Source, "arrival", "telemetry.bridge-reset", "Configuration reloaded", 0);
                        token = Guid.NewGuid().ToString("N")[..12];
                        Interlocked.Exchange(ref lastSequence, 0);
                        await EvalBoundedAsync(BridgeScript(token), input);
                        lease.CancelAfter(TimeSpan.FromSeconds(14));
                        yield return new(clock.ElapsedUs, Source, "arrival", "telemetry.bridge-reinstalled",
                            "Hyprland reloaded its configuration", 0);
                        continue;
                    }
                    if (Parse(line, clock.ElapsedUs) is { } sample) yield return sample;
                    // Layout IPC runs in the background so socket2 observations remain responsive.
                    if (layoutTask is { IsCompleted: true })
                    {
                        var layout = await layoutTask;
                        layoutTask = null;
                        if (layout is not null) yield return layout;
                    }
                    layoutTask ??= LayoutAsync(clock, input);
                }
                if (lease.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    yield return new(clock.ElapsedUs, Source, "arrival", "telemetry.lease-expired", "The bridge lease could not be renewed", 0);
                    yield break;
                }
                // EOF: the compositor restarted or closed the socket; cached window state is stale.
                Interlocked.Increment(ref gaps);
                yield return new(clock.ElapsedUs, Source, "arrival", "telemetry.disconnected", "socket2 closed; reconnecting", 0);
                // Discard queries from the lost connection before resetting deduplication. A late
                // completion must not suppress the first identical layout after reconnecting.
                if (layoutTask is not null) await layoutTask;
                layoutTask = null;
                lastLayout = null;
            }
        }
        finally
        {
            await renewal.CancelAsync();
            await lease.CancelAsync();
            if (layoutTask is not null)
                try { await layoutTask; } catch (OperationCanceledException) when (lease.IsCancellationRequested) { }
            if (renewing is not null) await renewing.ContinueWith(_ => { }, TaskScheduler.Default);
            if (bridge)
                try { await HyprlandIpc.EvalAsync(StopScript(token), new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token, instance); }
                catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException
                                                  or System.ComponentModel.Win32Exception)
                {
                    detail += "; bridge removal failed, it expires by itself: " + error.Message;
                }
        }
    }

    // Keeps the bridge alive while the daemon is; a crashed daemon stops renewing and the
    // bridge silences itself within its deadline.
    private async Task RenewAsync(CancellationToken cancellationToken, CancellationTokenSource lease)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            string renewingToken = token;
            try
            {
                await EvalBoundedAsync(RenewScript(renewingToken), cancellationToken);
                if (renewingToken == token) lease.CancelAfter(TimeSpan.FromSeconds(14));
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
            { detail = "Mouse bridge renewal failed; its observations expire with the lease: " + error.Message; }
        }
    }

    private async Task<InteractionSample?> LayoutAsync(ISessionClock clock, CancellationToken cancellationToken, bool force = false)
    {
        if (geometry is null || !geometry.Source.StartsWith("monitor:", StringComparison.Ordinal)) return null;
        long now = clock.ElapsedUs;
        if (!force && now - lastLayoutUs < 2_000_000 &&
            (layoutDirtyUs is null || now - layoutDirtyUs.Value < 200_000)) return null;
        lastLayoutUs = now;
        layoutDirtyUs = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var environment = (instance ?? HyprlandInstance.Current)?.Environment;
            var queries = await Task.WhenAll(new[] { "clients", "monitors", "activewindow" }.Select(name =>
                ProcessRunner.RunCheckedAsync("hyprctl", ["-j", name], timeout.Token, environment)));
            string payload = HyprlandLayouts.Compose(queries[0].Text, queries[1].Text, queries[2].Text, geometry, windowTitles);
            if (payload == lastLayout) return null;
            lastLayout = payload;
            return new(clock.ElapsedUs, Source, "arrival", "window.layout", payload, 0);
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or InvalidDataException or
            InvalidOperationException or ArgumentException or KeyNotFoundException or System.ComponentModel.Win32Exception ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { detail = "Window layout unavailable: " + error.Message; return null; }
    }

    private async Task EvalBoundedAsync(string script, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { await HyprlandIpc.EvalAsync(script, timeout.Token, instance); }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Hyprland bridge IPC timed out.", error); }
    }

    public InteractionSample? Parse(string line, long atUs)
    {
        int split = line.IndexOf(">>", StringComparison.Ordinal);
        if (split <= 0) return null;
        string name = line[..split], data = line[(split + 2)..];
        switch (name)
        {
            case "activewindow":
                int comma = data.IndexOf(',');
                string windowClass = comma < 0 ? data : data[..comma];
                string title = comma < 0 ? "" : data[(comma + 1)..];
                if (windowClass.Length == 0 && title.Length == 0) return null;
                string text = windowTitles && title.Length > 0 ? $"{windowClass}: {Truncate(title, 160)}" : windowClass;
                return new(atUs, Source, "arrival", "window.focus", text, 0);
            case "workspacev2":
                return new(atUs, Source, "arrival", "workspace", data[(data.IndexOf(',') + 1)..], 0);
            case "openwindow":
                string[] parts = data.Split(',', 4);
                return parts.Length >= 3
                    ? new(atUs, Source, "arrival", "window.open", windowTitles && parts.Length == 4 ? $"{parts[2]}: {Truncate(parts[3], 160)}" : parts[2], 0)
                    : null;
            case "custom" when data.StartsWith("wayscrawl-v1:", StringComparison.Ordinal):
                return ParseBridge(data["wayscrawl-v1:".Length..], atUs);
            default:
                return null;
        }
    }

    // Bridge records: TOKEN:SEQ:KIND:...; anything from another session's bridge is ignored.
    private InteractionSample? ParseBridge(string data, long atUs)
    {
        string[] f = data.Split(':');
        if (f.Length < 4 || f[0] != token || !long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out long sequence))
            return null;
        long previous = Interlocked.Read(ref lastSequence);
        if (sequence <= previous) { Interlocked.Increment(ref dropped); return null; }
        Interlocked.Exchange(ref lastSequence, sequence);
        if (previous > 0 && sequence != previous + 1) Interlocked.Add(ref gaps, Math.Max(1, sequence - previous - 1));
        string where = f.Length >= 5 ? Locate(f[^1]) : "";
        return f[2] switch
        {
            "button" when f.Length >= 6 && (f[4] is "down" or "up") =>
                new(atUs, Source, "binding", f[4] == "down" ? "input.press" : "input.release", ButtonName(f[3]) + where, sequence),
            "wheel" when f.Length >= 5 && (f[3] is "up" or "down") =>
                new(atUs, Source, "binding", "input.wheel", f[3] + where, sequence),
            _ => null
        };
    }

    // Global logical cursor position -> source pixels of the recorded output, when inside it.
    private string Locate(string position)
    {
        if (geometry is null) return "";
        string[] xy = position.Split(',');
        if (xy.Length != 2 || !double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
            !double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            return "";
        var source = geometry.ToSource(new(x, y));
        bool inside = source.X >= 0 && source.Y >= 0 && source.X <= geometry.PixelWidth && source.Y <= geometry.PixelHeight;
        return inside ? FormattableString.Invariant($" at {source.X:0},{source.Y:0}") : " outside the recorded output";
    }

    private static string ButtonName(string code) => code switch
    {
        "272" => "left", "273" => "right", "274" => "middle", _ => "button " + code
    };

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private async IAsyncEnumerable<string> LinesAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken, CancellationToken ownerCancellation)
    {
        byte[] buffer = new byte[16384];
        var line = new List<byte>(256);
        bool overflow = false;
        int read;
        while (true)
        {
            var pending = stream.ReadAsync(buffer, cancellationToken).AsTask();
            if (geometry is not null)
                while (!pending.IsCompleted && !cancellationToken.IsCancellationRequested)
                    if (await Task.WhenAny(pending, Task.Delay(200, cancellationToken)) != pending && !cancellationToken.IsCancellationRequested)
                        yield return "__layout_tick__";
            try { read = await pending; }
            catch (IOException) { read = 0; }
            catch (OperationCanceledException) when (!ownerCancellation.IsCancellationRequested) { read = 0; }
            if (read <= 0) break;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    if (line.Count < MaxLineBytes) line.Add(buffer[i]);
                    else overflow = true;
                    continue;
                }
                if (overflow) Interlocked.Increment(ref dropped);
                else yield return Encoding.UTF8.GetString(line.ToArray());
                line.Clear();
                overflow = false;
            }
        }
    }

    // Stop and renew act only for their own session, so a late cleanup cannot silence a newer one.
    public static string StopScript(string token) => $"if WayScrawlBridge and WayScrawlBridge.version == 4 then WayScrawlBridge.stop(\"{token}\") end";
    public static string RenewScript(string token) => $"if not WayScrawlBridge or WayScrawlBridge.version ~= 4 or not WayScrawlBridge.renew(\"{token}\", 15) then error(\"bridge lease not renewed\") end";

    // Installed with `hyprctl eval`; nothing is written to the user's config. The binds are added
    // once per compositor lifetime and never removed: Hyprland 0.56's Keybind:remove() aborts the
    // compositor (Config::Lua::Objects::CLuaKeybind::push). Sessions only switch a token and a
    // deadline, so between sessions, or after a daemon crash, the binds do nothing.
    public static string BridgeScript(string token, int seconds = 15) => """
        if not WayScrawlBridge or WayScrawlBridge.version ~= 4 then
          local state = { token = "", seq = 0, alive_until = 0 }
          local function emit(kind)
            if state.token == "" or os.time() > state.alive_until then return end
            state.seq = state.seq + 1
            local p = hl.get_cursor_pos()
            local where = p and string.format("%.1f,%.1f", p.x, p.y) or ""
            hl.dispatch(hl.dsp.event("wayscrawl-v1:" .. state.token .. ":" .. state.seq .. ":" .. kind .. ":" .. where))
          end
          local opts = { non_consuming = true, ignore_mods = true }
          local up = { non_consuming = true, ignore_mods = true, release = true }
          for _, b in ipairs({ "272", "273", "274" }) do
            hl.bind("mouse:" .. b, function() emit("button:" .. b .. ":down") end, opts)
            hl.bind("mouse:" .. b, function() emit("button:" .. b .. ":up") end, up)
          end
          hl.bind("mouse_up", function() emit("wheel:up") end, opts)
          hl.bind("mouse_down", function() emit("wheel:down") end, opts)
          WayScrawlBridge = {
            version = 4,
            start = function(t, seconds) state.token, state.seq, state.alive_until = t, 0, os.time() + seconds end,
            renew = function(t, seconds) if state.token == t then state.alive_until = os.time() + seconds; return true end return false end,
            stop = function(t) if state.token == t then state.token, state.alive_until = "", 0 end end
          }
        end
        WayScrawlBridge.start("TOKEN", SECONDS)
        """.Replace("TOKEN", token).Replace("SECONDS", seconds.ToString(CultureInfo.InvariantCulture));
}
