using SkiaSharp;
using WayScrawl.Core;
using WayScrawl.Application;
using WayScrawl.Platform;
using WayScrawl.Platform.Hyprland;
using WayScrawl.Wayland;

namespace WayScrawl.Checks;

internal static class InkOverlayChecks
{
    public static async Task RunAsync(Harness h, NestedHyprland nested, Geometry geometry, VirtualPointer pointer, double ratio)
    {
        h.Section = "ink-overlay";
        var clock = new SessionClock();
        await using var overlay = new WaylandOverlay(new(Display: nested.Display));
        await overlay.ShowAsync(geometry, "TEST", clock, CancellationToken.None);
        async Task<T> Next<T>() where T : OverlayEvent
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await overlay.Events.WaitToReadAsync(timeout.Token))
                while (overlay.Events.TryRead(out var item)) if (item is T match) return match;
            throw new CheckFailed("No " + typeof(T).Name);
        }
        await overlay.SetInputModeAsync(OverlayInputMode.Drawing, CancellationToken.None);
        pointer.MoveTo(400, 400); pointer.Press(); pointer.Release();
        var pin = (await Next<PinPlaced>()).Pin;
        await Task.Delay(120);
        using (var shot = SKBitmap.Decode(await nested.ScreenshotAsync()))
        {
            var center = shot.GetPixel((int)(400 * ratio + 8 * ratio), (int)(400 * ratio));
            var ring = shot.GetPixel((int)(400 * ratio + 11 * ratio), (int)(400 * ratio));
            h.Check(center.Red < 90 && ring.Red > 190, "A click draws a dark numbered pin with a white ring.");
        }
        pointer.Drag([(500, 400), (700, 400)]);
        var stroke = (await Next<StrokeCompleted>()).Stroke;
        await overlay.SetInputModeAsync(OverlayInputMode.PassThrough, CancellationToken.None);
        await overlay.UndoAsync(CancellationToken.None);
        h.Check((await Next<StrokesRemoved>()).StrokeIds.SequenceEqual([stroke.Id]), "Undo orders strokes and pins together.");
        await overlay.ClearAsync(CancellationToken.None);
        h.Check((await Next<StrokesRemoved>()).StrokeIds.SequenceEqual([pin.Id]), "Clear removes the remaining pin.");

        await overlay.SetInputModeAsync(OverlayInputMode.Drawing, CancellationToken.None);
        pointer.Drag([(400, 300), (700, 300)]);
        var fading = await Next<StrokeCompleted>();
        await overlay.SetInputModeAsync(OverlayInputMode.PassThrough, CancellationToken.None);
        await Task.Delay(1000);
        using (var shot = SKBitmap.Decode(await nested.ScreenshotAsync()))
            h.Check(shot.GetPixel((int)(550 * ratio), (int)(300 * ratio)).Red > 200, "Ink remains before the fade deadline.");
        var removed = await Next<StrokesRemoved>();
        h.Check(removed.Reason == "fade" && Math.Abs(removed.AtUs - fading.AtUs - 1_500_000) < 180_000,
            "Fade journals semantic removal at pen-up plus 1.5 seconds.");
        await Task.Delay(500);
        using (var shot = SKBitmap.Decode(await nested.ScreenshotAsync()))
            h.Check(shot.GetPixel((int)(550 * ratio), (int)(300 * ratio)).Red < 120, "Fade removes all ink pixels in pass-through.");

        await overlay.HideAsync(CancellationToken.None);
        var instance = new HyprlandInstance(nested.Runtime, nested.Environment["HYPRLAND_INSTANCE_SIGNATURE"]);
        var control = new HyprlandHoldControl("Shift_L", instance);
        string fixture = Path.Combine(nested.Root, "hold-fixture.mkv");
        await ProcessRunner.RunCheckedAsync("ffmpeg", ["-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=size=320x180:rate=30:duration=1",
            "-c:v", "libx264", "-preset", "ultrafast", fixture], CancellationToken.None);
        var backend = new CoordinatorChecks.FakeBackend(fixture) { GeometryOverride = geometry };
        var paths = new WayScrawlPaths(Path.Combine(nested.Root, "coord-run"), Path.Combine(nested.Root, "coord-state"),
            Path.Combine(nested.Root, "coord-data"), Path.Combine(nested.Root, "coord-config"));
        var holdOverlay = new WaylandOverlay(new(Display: nested.Display));
        await using var recording = new SessionCoordinator(paths,
            new(Notifications: false, CopyPathToClipboard: false, Overlay: true, Telemetry: "off", DrawTrigger: "hold"), backend,
            overlayFactory: () => holdOverlay, holdFactory: () => control, processor: _ => Task.FromResult((1, "not processing this input fixture")));
        Task<(bool Ok, string Message)> Command(string name, string? argument = null) =>
            recording.ExecuteAsync(new(Guid.NewGuid().ToString("N"), name, argument), CancellationToken.None);
        await Command(DaemonCommands.Start);
        await PlatformChecks.WaitForAsync(() => recording.Status.Draw is not null);
        await PlatformChecks.WaitForAsync(() => nested.RunAsync("hyprctl", ["-j", "binds"]).Result.Text.Contains("Shift_L", StringComparison.Ordinal));
        using (var binds = System.Text.Json.JsonDocument.Parse((await nested.RunAsync("hyprctl", ["-j", "binds"])).Text))
            h.Check(!binds.RootElement.EnumerateArray().Any(b => b.GetProperty("key").GetString()?.StartsWith("mouse", StringComparison.Ordinal) == true),
                "Hold with telemetry off installs only hold-key binds, without mouse or wheel tracking binds.");
        string raw = Path.Combine(paths.Sessions, recording.Status.SessionId!);
        SessionJournal Journal() => SessionJournal.Read(Path.Combine(raw, SessionRecord.JournalName));
        string typed = Path.Combine(nested.Root, "hold-typed.txt");
        nested.Spawn("alacritty", ["--class", "HoldCheck", "-e", "sh", "-c", "cat > \"" + typed + "\""], new Dictionary<string, string>());
        await PlatformChecks.WaitForAsync(() => File.Exists(typed));
        string driver = Path.Combine(nested.Root, "keyboard");
        await nested.RunAsync("gcc", ["-Wall", "-Wextra", "-Werror", "-o", driver,
            Path.Combine(OverlayChecks.RepositoryRoot(), "tests/WayScrawl.Checks/Fixtures/virtual-keyboard.c"), "-lwayland-client", "-lxkbcommon"]);
        var keys = nested.RunAsync(driver, [nested.Display, "800"]);
        await PlatformChecks.WaitForAsync(() => holdOverlay.Mode == OverlayInputMode.Holding);
        pointer.Drag([(400, 500), (700, 500)]);
        await keys;
        await PlatformChecks.WaitForAsync(() => holdOverlay.Mode == OverlayInputMode.PassThrough);
        await PlatformChecks.WaitForAsync(() => File.ReadAllText(typed).Contains('\n'));
        await PlatformChecks.WaitForAsync(() => Journal().Strokes.Length == 1);
        h.Check((await File.ReadAllTextAsync(typed)) == "A\n" && Journal().Events.Count(e => e.Kind is "draw.on" or "draw.off") == 2,
            "Real Shift input through the production coordinator draws and releases with telemetry off; Shift typing reaches the application.");
        await nested.RunAsync(driver, [nested.Display, "20"]);
        await Task.Delay(100);
        h.Check(Journal().Events.Count(e => e.Kind is "draw.on" or "draw.off") == 2,
            "An actual Shift press that only types a capital letter leaves no key-only journal span.");
        string beforeReloadToken = control.Token;
        await nested.HyprctlAsync("reload");
        await PlatformChecks.WaitForAsync(() => control.Token != beforeReloadToken);
        await PlatformChecks.WaitForAsync(() => nested.RunAsync("hyprctl", ["-j", "binds"]).Result.Text.Contains("Shift_L", StringComparison.Ordinal));
        await HyprlandIpc.EvalAsync($"hl.dispatch(hl.dsp.event(\"wayscrawl-hold-v1:{beforeReloadToken}:999:down\"))", CancellationToken.None, instance);
        await Task.Delay(150);
        h.Check(holdOverlay.Mode == OverlayInputMode.PassThrough,
            "After a real compositor config reload, stale hold tokens cannot reacquire the production overlay.");
        await nested.RunAsync(driver, [nested.Display, "20"]);
        await Task.Delay(100);
        h.Check(holdOverlay.Mode == OverlayInputMode.PassThrough && Journal().Events.Count(e => e.Kind is "draw.on" or "draw.off") == 2,
            "The reinstalled hold controls accept a fresh modifier press and release without journaling typed capitals.");
        await HyprlandIpc.EvalAsync(HyprlandHoldControl.StopScript(control.Token), CancellationToken.None, instance);
        await Task.Delay(8000);
        await Command(DaemonCommands.Draw, "hold");
        var expiredAfter = System.Diagnostics.Stopwatch.StartNew();
        await PlatformChecks.WaitForAsync(() => holdOverlay.Mode == OverlayInputMode.PassThrough, 8000);
        h.Check(expiredAfter.Elapsed < TimeSpan.FromSeconds(9),
            "A failed independent hold renewal releases the production coordinator's grab before its 10 second cap.");

        string mouseFile = Path.Combine(nested.Root, "mouse.txt");
        nested.Spawn("alacritty", ["--class", "MouseProbe", "--option", "window.startup_mode=\"Fullscreen\"", "-e", "sh", "-c",
            "printf '\\033[?1000h\\033[?1006h'; cat > \"" + mouseFile + "\""], new Dictionary<string, string>());
        await Task.Delay(900);
        await holdOverlay.SetInputModeAsync(OverlayInputMode.Holding, CancellationToken.None);
        pointer.MoveTo(500, 600); await Task.Delay(80);
        pointer.Press(); pointer.MoveTo(600, 600); await Task.Delay(80);
        await holdOverlay.SetInputModeAsync(OverlayInputMode.PassThrough, CancellationToken.None);
        await PlatformChecks.WaitForAsync(() => Journal().Strokes.Length == 2);
        pointer.Release(); // Pointer routing resumes after button release.
        pointer.MoveTo(500, 650); await Task.Delay(80); pointer.Press(); pointer.Release();
        await nested.KeysAsync("-k", "Return");
        await Task.Delay(150);
        h.Check(File.Exists(mouseFile) && (await File.ReadAllTextAsync(mouseFile)).Contains("\u001b[<0;"),
            "After key-before-button release, the next click reaches the normal application underneath.");
        int beforeApplicationDrag = (await File.ReadAllTextAsync(mouseFile)).Length;
        pointer.MoveTo(400, 650); pointer.Press();
        await holdOverlay.SetInputModeAsync(OverlayInputMode.Holding, CancellationToken.None);
        pointer.MoveTo(600, 650); pointer.Release(); await Task.Delay(100);
        await nested.KeysAsync("-k", "Return");
        await Task.Delay(120);
        string applicationMouse = (await File.ReadAllTextAsync(mouseFile))[beforeApplicationDrag..];
        h.Check(System.Text.RegularExpressions.Regex.IsMatch(applicationMouse, @"\x1b\[<0;\d+;\d+M") &&
            System.Text.RegularExpressions.Regex.IsMatch(applicationMouse, @"\x1b\[<0;\d+;\d+m"),
            "Activating Holding during an application drag preserves the client's mouse press and release delivery.");
        await holdOverlay.SetInputModeAsync(OverlayInputMode.PassThrough, CancellationToken.None);

        await holdOverlay.SetInputModeAsync(OverlayInputMode.Holding, CancellationToken.None);
        pointer.MoveTo(480, 480); await Task.Delay(80); pointer.Press();
        await holdOverlay.SetInputModeAsync(OverlayInputMode.PassThrough, CancellationToken.None);
        pointer.Release();
        await PlatformChecks.WaitForAsync(() => Journal().Pins.Length == 1);
        h.Check(Journal().Pins.Single().Number == 1, "Releasing the hold key before the mouse button still completes a quick click as a pin.");

        // Pointer motion cannot extend a lost-release grab indefinitely.
        await holdOverlay.SetInputModeAsync(OverlayInputMode.Holding, CancellationToken.None);
        var until = DateTime.UtcNow.AddSeconds(11);
        while (DateTime.UtcNow < until && holdOverlay.Mode == OverlayInputMode.Holding)
        { pointer.MoveTo(400 + (int)(clock.ElapsedUs / 1000 % 100), 500); await Task.Delay(100); }
        h.Check(holdOverlay.Mode == OverlayInputMode.PassThrough, "Holding expires after 10 seconds even while the pointer keeps moving.");
        await Command(DaemonCommands.Cancel);

        // Ctrl+Z must be handled by toggle Drawing even when Ctrl is also the hold modifier.
        var ctrlOverlay = new WaylandOverlay(new(Display: nested.Display));
        var ctrlControl = new HyprlandHoldControl("Control_L", instance);
        await using var ctrlRecording = new SessionCoordinator(paths,
            new(Notifications: false, CopyPathToClipboard: false, Overlay: true, Telemetry: "off", DrawTrigger: "hold", DrawHoldKey: "Control_L"), backend,
            overlayFactory: () => ctrlOverlay, holdFactory: () => ctrlControl, processor: _ => Task.FromResult((1, "input fixture")));
        await ctrlRecording.ExecuteAsync(new("ctrl-start", DaemonCommands.Start), CancellationToken.None);
        await PlatformChecks.WaitForAsync(() => ctrlRecording.Status.Draw is not null);
        await PlatformChecks.WaitForAsync(() => nested.RunAsync("hyprctl", ["-j", "binds"]).Result.Text.Contains("Control_L", StringComparison.Ordinal));
        await ctrlRecording.ExecuteAsync(new("ctrl-draw", DaemonCommands.Draw, "on"), CancellationToken.None);
        pointer.Drag([(300, 400), (600, 400)]);
        await PlatformChecks.WaitForAsync(() => ctrlOverlay.InkCount == 1);
        await nested.RunAsync(driver, [nested.Display, "200", "control"]);
        await PlatformChecks.WaitForAsync(() => ctrlOverlay.InkCount == 0);
        h.Check(ctrlOverlay.Mode == OverlayInputMode.Drawing,
            "With Control_L as hold modifier, physical Ctrl+Z still undoes ink through Drawing's exclusive keyboard without handing it to the application.");
        await ctrlRecording.ExecuteAsync(new("ctrl-cancel", DaemonCommands.Cancel), CancellationToken.None);

    }
}
