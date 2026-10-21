using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using WayScrawl.Core;
using WayScrawl.Platform;
using WayScrawl.Platform.Hyprland;
using WayScrawl.Wayland;

namespace WayScrawl.Checks;

internal static class TelemetryChecks
{
    public static async Task RunAsync(Harness h)
    {
        h.Section = "telemetry";
        var geometry = new Geometry("g0", 2304, 0, 2304, 1296, 3840, 2160, "monitor:DP-1");
        var telemetry = new HyprlandTelemetry(geometry, instance: new("/nonexistent", "none"));
        h.Check(telemetry.Parse("activewindow>>firefox,Docs, with, commas", 5) is { Kind: "window.focus", Payload: "firefox: Docs, with, commas", AtUs: 5 },
            "Window focus keeps titles containing commas.");
        h.Check(new HyprlandTelemetry(geometry, windowTitles: false, instance: new("/x", "y")).Parse("activewindow>>kitty,secret", 1)?.Payload == "kitty",
            "Window titles can be withheld.");
        h.Check(telemetry.Parse("activewindow>>,", 1) is null && telemetry.Parse("garbage", 1) is null &&
                telemetry.Parse("monitoradded>>DP-2", 1) is null, "Empty, malformed and unrelated lines are ignored.");
        h.Check(telemetry.Parse("workspacev2>>3,code", 1) is { Kind: "workspace", Payload: "code" }, "Workspace changes are named.");
        string t = telemetry.Token;
        h.Check(telemetry.Parse("custom>>wayscrawl-v1:otherbridge:1:button:272:down:2400.0,10.0", 1) is null,
            "Another session's bridge is ignored.");
        var press = telemetry.Parse($"custom>>wayscrawl-v1:{t}:1:button:272:down:2304.0,648.0", 7);
        h.Check(press is { Kind: "input.press", Fidelity: "binding", Payload: "left at 0,1080", Sequence: 1 },
            $"A bridge press maps global logical to source pixels ({press?.Payload}).");
        var outside = telemetry.Parse($"custom>>wayscrawl-v1:{t}:2:button:273:up:100.0,100.0", 8);
        h.Check(outside is { Kind: "input.release", Payload: "right outside the recorded output" }, "Clicks on other outputs are labelled.");
        telemetry.Parse($"custom>>wayscrawl-v1:{t}:5:wheel:down:2500.0,100.0", 9);
        h.Check(telemetry.Statistics.Gaps == 2, $"Sequence gaps are counted ({telemetry.Statistics.Gaps}).");
        var hold = new HyprlandHoldControl("Shift_L");
        h.Check(hold.Parse($"custom>>wayscrawl-hold-v1:stale-token:1:down") is null &&
            hold.Parse($"custom>>wayscrawl-hold-v1:{hold.Token}:1:down") is { Pressed: true } &&
            hold.Parse($"custom>>wayscrawl-hold-v1:{hold.Token}:1:up") is null &&
            hold.Parse($"custom>>wayscrawl-hold-v1:{hold.Token}:0:up") is null &&
            hold.Parse($"custom>>wayscrawl-hold-v1:{hold.Token}:2:up") is { Pressed: false },
            "Hold controls reject stale tokens and duplicate/backward sequences, and accept the next release.");
        h.Check(telemetry.Describe().Any(c => c.Name == "keyboard" && c.Status == Availability.Unsupported), "Keystrokes are declared unrecorded.");
        var planned = KeyframePlanner.Plan(10_000_000, [], [
            new(2_000_000, "input.press", "left at 10,10", "hyprland", "binding"),
            new(5_000_000, "draw.on", ""), new(5_500_000, "input.press", "left at 20,20", "hyprland", "binding"),
            new(6_000_000, "draw.off", "")], [], [], new());
        h.Check(planned.Selected.Count(c => c.Kind == "click") == 1, "Clicks become frames, except presses while drawing.");
        if (File.Exists("/usr/bin/luac"))
        {
            string script = Path.Combine(h.TempDirectory("lua"), "bridge.lua");
            await File.WriteAllTextAsync(script, HyprlandTelemetry.BridgeScript("abc"));
            var luac = await ProcessRunner.RunAsync("luac", ["-p", script], CancellationToken.None);
            h.Check(luac.ExitCode == 0, "The bridge script is valid Lua: " + luac.Error);
        }

        string aquamarine = Environment.GetEnvironmentVariable("WAYSCRAWL_TEST_AQUAMARINE")
            ?? Path.Combine(OverlayChecks.RepositoryRoot(), ".cache", "aquamarine");
        if (NestedHyprland.MissingPrerequisite(aquamarine) is { } missing)
        {
            Console.WriteLine("Skipped isolated telemetry checks: " + missing + ".");
            return;
        }
        await using var nested = await NestedHyprland.StartAsync(aquamarine);
        var instance = new HyprlandInstance(nested.Runtime, nested.Environment["HYPRLAND_INSTANCE_SIGNATURE"]);
        var monitor = (await nested.MonitorsAsync()).Single(m => m.Name == "TEST");
        using var pointer = new VirtualPointer(nested.Display, "TEST", (int)monitor.LogicalWidth, (int)monitor.LogicalHeight);
        var live = new HyprlandTelemetry(monitor.ToGeometry("g0"), instance: instance);
        var samples = new ConcurrentQueue<InteractionSample>();
        using var stop = new CancellationTokenSource();
        var clock = new SessionClock();
        var reading = Task.Run(async () =>
        {
            await foreach (var sample in live.ReadAsync(clock, stop.Token)) samples.Enqueue(sample);
        });
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!samples.Any(s => s.Kind == "input.press") && DateTime.UtcNow < deadline)
        {
            pointer.MoveTo(640, 360);
            pointer.Press();
            pointer.Release();
            await Task.Delay(200);
        }
        string oldToken = live.Token;
        // A config reload drops the Lua state; the bridge must be reinstalled and keep reporting.
        await nested.HyprctlAsync("reload");
        await PlatformChecks.WaitForAsync(() => samples.Any(s => s.Kind == "telemetry.bridge-reinstalled"));
        h.Check(live.Token != oldToken && live.Parse($"custom>>wayscrawl-v1:{oldToken}:999999:button:272:down:1,1", 1) is null,
            "Reload rotates the token, so records from before reload cannot be replayed.");
        h.Check(samples.Any(s => s.Kind == "telemetry.bridge-reset"), "Reload announces input release before reinstallation.");
        int pressesBefore = samples.Count(s => s.Kind == "input.press");
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (samples.Count(s => s.Kind == "input.press") == pressesBefore && DateTime.UtcNow < deadline)
        {
            pointer.Press();
            pointer.Release();
            await Task.Delay(200);
        }
        h.Check(samples.Count(s => s.Kind == "input.press") > pressesBefore, "Clicks keep arriving after a config reload.");
        var livePress = samples.FirstOrDefault(s => s.Kind == "input.press");
        double ratio = monitor.Width / monitor.LogicalWidth;
        h.Check(livePress?.Payload == $"left at {640 * ratio:0},{360 * ratio:0}",
            $"The live bridge reports clicks in source pixels ({livePress?.Payload ?? "none"}).");
        await PlatformChecks.WaitForAsync(() => samples.Any(s => s.Kind == "input.release"));
        h.Check(true, "Releases are observed.");
        await stop.CancelAsync();
        try { await reading.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        h.Check(live.Statistics is { Received: > 0, Gaps: 0 }, $"Statistics are kept across the reload ({live.Statistics.Received} lines, {live.Statistics.Gaps} gaps).");

        var after = await CustomEventsAsync(nested, instance, () => { pointer.Press(); pointer.Release(); });
        h.Check(after.Count == 0, "The bridge is silent once telemetry stops.");
        h.Check((await nested.MonitorsAsync()).Any(m => m.Name == "TEST"), "The compositor survives clicks after the bridge stops.");

        // A second session reuses the installed binds under a new token; the old token stays silent.
        var second = new HyprlandTelemetry(monitor.ToGeometry("g0"), instance: instance);
        var secondSamples = new ConcurrentQueue<InteractionSample>();
        using (var stopSecond = new CancellationTokenSource())
        {
            var secondReading = Task.Run(async () =>
            {
                await foreach (var sample in second.ReadAsync(clock, stopSecond.Token)) secondSamples.Enqueue(sample);
            });
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (!secondSamples.Any(s => s.Kind == "input.press") && DateTime.UtcNow < deadline)
            {
                pointer.Press();
                pointer.Release();
                await Task.Delay(200);
            }
            await stopSecond.CancelAsync();
            try { await secondReading.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        }
        h.Check(secondSamples.Any(s => s.Kind == "input.press" && s.Sequence >= 1), "A later session gets its own sequence of clicks.");

        // Non-consuming: with the bridge active, a client under the pointer still receives the drag.
        var overlay = new WaylandOverlay(new OverlayOptions(Display: nested.Display, InkFadeSeconds: 0));
        await overlay.ShowAsync(monitor.ToGeometry("g0"), "TEST", clock, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        await overlay.SetInputModeAsync(OverlayInputMode.Drawing, CancellationToken.None);
        var third = new HyprlandTelemetry(monitor.ToGeometry("g0"), instance: instance);
        var thirdSamples = new ConcurrentQueue<InteractionSample>();
        using (var stopThird = new CancellationTokenSource())
        {
            var thirdReading = Task.Run(async () =>
            {
                await foreach (var sample in third.ReadAsync(clock, stopThird.Token)) thirdSamples.Enqueue(sample);
            });
            await Task.Delay(500);
            pointer.Drag([(300, 300), (400, 320), (500, 300)]);
            await PlatformChecks.WaitForAsync(() => thirdSamples.Any(x => x.Kind == "input.release"));
            await stopThird.CancelAsync();
            try { await thirdReading.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { }
        }
        bool stroke = false;
        while (overlay.Events.TryRead(out var item)) stroke |= item is StrokeCompleted;
        h.Check(stroke && thirdSamples.Any(x => x.Kind == "input.press"),
            "Bridge binds are non-consuming: the client under the pointer still gets the drag.");
        await overlay.DisposeAsync();

        // A daemon that dies cannot renew: the bridge must fall silent on its own.
        await HyprlandIpc.EvalAsync(HyprlandTelemetry.BridgeScript("expiring", seconds: 1), CancellationToken.None, instance);
        var early = await CustomEventsAsync(nested, instance, () => { pointer.Press(); pointer.Release(); });
        await Task.Delay(2500);
        var expired = await CustomEventsAsync(nested, instance, () => { pointer.Press(); pointer.Release(); });
        h.Check(early.Any(l => l.Contains("expiring")) && !expired.Any(l => l.Contains("expiring")),
            "An unrenewed bridge stops emitting after its deadline.");
        // A late stop for an older session leaves the current one running.
        await HyprlandIpc.EvalAsync(HyprlandTelemetry.BridgeScript("current", seconds: 30), CancellationToken.None, instance);
        await HyprlandIpc.EvalAsync(HyprlandTelemetry.StopScript("stale-session"), CancellationToken.None, instance);
        var stillCurrent = await CustomEventsAsync(nested, instance, () => { pointer.Press(); pointer.Release(); });
        h.Check(stillCurrent.Any(l => l.Contains("current")), "A stale session's stop cannot silence the current bridge.");
        await HyprlandIpc.EvalAsync(HyprlandTelemetry.StopScript("current"), CancellationToken.None, instance);
        h.Check((await nested.MonitorsAsync()).Any(m => m.Name == "TEST"), "The compositor is still healthy at the end.");

        var layouts = new ConcurrentQueue<InteractionSample>();
        var layoutSource = new HyprlandTelemetry(monitor.ToGeometry("g0"), bridge: false, windowTitles: false, instance: instance);
        using var layoutStop = new CancellationTokenSource();
        var layoutPump = Task.Run(async () =>
        {
            await foreach (var item in layoutSource.ReadAsync(clock, layoutStop.Token)) if (item.Kind == "window.layout") layouts.Enqueue(item);
        });
        nested.Spawn("alacritty", ["--class", "LayoutOne", "--title", "first secret", "-e", "sleep", "60"], new Dictionary<string, string>());
        nested.Spawn("alacritty", ["--class", "LayoutTwo", "--title", "second secret", "-e", "sleep", "60"], new Dictionary<string, string>());
        WindowSnapshot[] Latest() => layouts.LastOrDefault() is { } sample
            ? System.Text.Json.JsonSerializer.Deserialize<WindowSnapshot[]>(sample.Payload, WayScrawlJson.Options)! : [];
        await PlatformChecks.WaitForAsync(() => Latest().Length == 2, 10_000);
        await HyprlandIpc.EvalAsync("""
            hl.dispatch(hl.dsp.window.float({window="class:LayoutOne"}))
            hl.dispatch(hl.dsp.window.resize({x=500, y=300, relative=false, window="class:LayoutOne"}))
            hl.dispatch(hl.dsp.window.move({x=100, y=100, relative=false, window="class:LayoutOne"}))
            hl.dispatch(hl.dsp.focus({window="class:LayoutOne"}))
            """, CancellationToken.None, instance);
        await PlatformChecks.WaitForAsync(() => Latest().Any(w => w.Class == "LayoutOne" && w.Focused && Math.Abs(w.Rect.X - 100 * ratio) < 12), 10_000);
        var one = Latest().Single(w => w.Class == "LayoutOne");
        h.Check(Math.Abs(one.Rect.Width - 500 * ratio) < 12 && !layouts.Any(l => l.Payload.Contains("secret") || l.Payload.Contains("\"title\"")),
            "Nested two-client layouts map floating rectangles to source pixels and omit titles when disabled.");
        int layoutCount = layouts.Count;
        await HyprlandIpc.EvalAsync("hl.dispatch(hl.dsp.window.move({x=200,y=100,relative=false,window=\"class:LayoutOne\"}))", CancellationToken.None, instance);
        await PlatformChecks.WaitForAsync(() => Latest().Any(w => w.Class == "LayoutOne" && Math.Abs(w.Rect.X - 200 * ratio) < 12), 10_000);
        h.Check(layouts.Count > layoutCount, "Bounded refresh observes ordinary window motion without a workspace-move event.");
        await HyprlandIpc.EvalAsync("hl.dispatch(hl.dsp.focus({workspace=2}))", CancellationToken.None, instance);
        await PlatformChecks.WaitForAsync(() => layouts.LastOrDefault()?.Payload == "[]", 10_000);
        h.Check(true, "Workspace changes remove windows from the recorded active workspace layout.");
        await layoutStop.CancelAsync();
        try { await layoutPump; } catch (OperationCanceledException) { }
        // Replace only this private compositor's event-socket name. Layout queries continue to
        // reach its real command socket, while the listener deterministically cuts transport.
        string eventSocket = HyprlandIpc.EventSocket(instance)!;
        string savedSocket = eventSocket + ".saved";
        File.Move(eventSocket, savedSocket);
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(eventSocket)); listener.Listen(2);
            var reconnect = new HyprlandTelemetry(monitor.ToGeometry("g0"), bridge: false, instance: instance);
            var observed = new ConcurrentQueue<InteractionSample>();
            using var reconnectStop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var reconnectPump = Task.Run(async () =>
            {
                await foreach (var sample in reconnect.ReadAsync(clock, reconnectStop.Token)) observed.Enqueue(sample);
            });
            using var first = await listener.AcceptAsync(reconnectStop.Token);
            await PlatformChecks.WaitForAsync(() => observed.Any(s => s.Kind == "window.layout"));
            first.Dispose();
            using var next = await listener.AcceptAsync(reconnectStop.Token);
            await PlatformChecks.WaitForAsync(() => observed.Count(s => s.Kind == "window.layout") >= 2);
            var all = observed.ToArray();
            h.Check(all.Count(s => s.Kind == "window.layout") >= 2 &&
                all.First(s => s.Kind == "window.layout").Payload == all.Last(s => s.Kind == "window.layout").Payload &&
                all.Any(s => s.Kind == "telemetry.disconnected"),
                "An unchanged layout is emitted again after transport loss invalidates its previous snapshot.");
            await HyprlandIpc.EvalAsync("hl.dispatch(hl.dsp.focus({workspace=1}))", CancellationToken.None, instance);
            using var eventWriter = new NetworkStream(next, ownsSocket: false);
            byte[] chatter = Encoding.UTF8.GetBytes("activewindow>>LayoutOne,\n");
            var storm = System.Diagnostics.Stopwatch.StartNew();
            bool refreshed = false;
            while (storm.Elapsed < TimeSpan.FromSeconds(3.5))
            {
                await eventWriter.WriteAsync(chatter, reconnectStop.Token);
                await Task.Delay(80, reconnectStop.Token);
                if (observed.Any(s => s.Kind == "window.layout" && s.Payload != "[]")) { refreshed = true; break; }
            }
            h.Check(refreshed, "Continuous focus events cannot starve the bounded two-second window refresh.");
            await reconnectStop.CancelAsync();
            try { await reconnectPump; } catch (OperationCanceledException) { }
        }
        finally { File.Delete(eventSocket); File.Move(savedSocket, eventSocket); }
    }

    // Collects wayscrawl custom events from socket2 while an action runs.
    private static async Task<List<string>> CustomEventsAsync(NestedHyprland nested, HyprlandInstance instance, Action action)
    {
        var lines = new List<string>();
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(HyprlandIpc.EventSocket(instance)!));
        await using var stream = new NetworkStream(socket);
        action();
        var buffer = new byte[8192];
        using var window = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        var text = new StringBuilder();
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, window.Token)) > 0) text.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (OperationCanceledException) { }
        lines.AddRange(text.ToString().Split('\n').Where(l => l.StartsWith("custom>>wayscrawl-v1:", StringComparison.Ordinal)));
        return lines;
    }
}
