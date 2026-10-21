using SkiaSharp;
using WayScrawl.Core;
using WayScrawl.Wayland;

namespace WayScrawl.Checks;

// Drives the real overlay inside an isolated nested Hyprland: injected pointer and keyboard
// input never reach the owner's desktop.
internal static class OverlayChecks
{
    public static async Task RunAsync(Harness h)
    {
        h.Section = "overlay";
        string aquamarine = Environment.GetEnvironmentVariable("WAYSCRAWL_TEST_AQUAMARINE")
            ?? Path.Combine(RepositoryRoot(), ".cache", "aquamarine");
        if (NestedHyprland.MissingPrerequisite(aquamarine) is { } missing)
        {
            Console.WriteLine("Skipped isolated overlay checks: " + missing + ".");
            return;
        }
        await using var nested = await NestedHyprland.StartAsync(aquamarine);
        var monitor = (await nested.MonitorsAsync()).Single(m => m.Name == "TEST");
        var geometry = monitor.ToGeometry("g0");
        int lw = (int)Math.Round(monitor.LogicalWidth), lhgt = (int)Math.Round(monitor.LogicalHeight);
        var clock = new SessionClock();
        var overlay = new WaylandOverlay(new OverlayOptions("#ff3b30", 6, nested.Display, InkFadeSeconds: 0));
        var events = new Queue<OverlayEvent>();
        async Task<T> Next<T>(int timeoutMs = 5000) where T : OverlayEvent
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                while (overlay.Events.TryRead(out var item))
                {
                    events.Enqueue(item);
                    if (item is T match) return match;
                }
                await Task.Delay(10);
            }
            throw new CheckFailed($"[overlay] No {typeof(T).Name} within {timeoutMs} ms.");
        }
        bool Quiet<T>() where T : OverlayEvent
        {
            while (overlay.Events.TryRead(out var item)) { events.Enqueue(item); if (item is T) return false; }
            return true;
        }

        string bundlePath = Path.Combine(nested.Root, "clipboard.wayscrawl");
        await WayScrawl.Platform.Demo.CreateAsync(bundlePath);
        string dotnet = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet"));
        string cli = Path.Combine(RepositoryRoot(), "src/WayScrawl.Cli/bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
            "net11.0/wayscrawl.dll");
        await nested.RunAsync(dotnet, [cli, "paste", bundlePath]);
        var clipboard = await nested.RunAsync("wl-paste", ["--no-newline"]);
        h.Check(clipboard.Text == PasteText.Compose(await WayScrawl.Platform.BundleStore.ReadAsync(bundlePath), bundlePath),
            "Paste copies the complete evidence text only into the nested compositor clipboard.");

        // Input devices must exist before the overlay binds the seat: a nested compositor
        // starts without any, and does not re-announce capabilities to existing clients.
        using var pointer = new VirtualPointer(nested.Display, "TEST", lw, lhgt);
        nested.HoldKeyboard();
        await Task.Delay(300);
        var started = System.Diagnostics.Stopwatch.StartNew();
        await overlay.ShowAsync(geometry, "TEST", clock, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        h.Check(true, $"Overlay mapped on the isolated output in {started.ElapsedMilliseconds} ms.");
        using (var layers = await nested.LayersAsync())
            h.Check(layers.RootElement.ToString().Contains("\"wayscrawl\""), "A wayscrawl layer surface exists.");

        pointer.Drag([(100, 100), (200, 150), (300, 100)]);
        await Task.Delay(300);
        h.Check(Quiet<StrokeCompleted>(), "Pass-through: pointer input does not reach the overlay.");

        await overlay.SetInputModeAsync(OverlayInputMode.Drawing, CancellationToken.None);
        h.Check((await Next<ModeChanged>()).Mode == OverlayInputMode.Drawing, "Drawing mode reported.");
        var path = Enumerable.Range(0, 21).Select(i => (400.0 + i * 20, 300.0)).ToList();
        pointer.Drag(path);
        var first = (await Next<StrokeCompleted>()).Stroke;
        double ratio = monitor.Width / monitor.LogicalWidth;
        h.Check(Math.Abs(first.Points[0].X - 400 * ratio) <= 1.5 && Math.Abs(first.Points[0].Y - 300 * ratio) <= 1.5 &&
                Math.Abs(first.Points[^1].X - 800 * ratio) <= 1.5, $"Strokes land on source pixels ({first.Points[0].X:0.0},{first.Points[0].Y:0.0}).");
        h.Check(first.Points.Zip(first.Points.Skip(1)).All(p => p.First.AtUs <= p.Second.AtUs), "Stroke points are time ordered.");
        await Task.Delay(150);
        using (var shot = SKBitmap.Decode(await nested.ScreenshotAsync()))
        {
            var ink = shot.GetPixel((int)(600 * ratio), (int)(300 * ratio));
            var edge = shot.GetPixel(2, (int)(300 * ratio));
            h.Check(ink.Red > 200 && ink.Green < 110, $"Ink is composited at the stroke ({ink}).");
            h.Check(edge.Red > 90, $"The drawing-mode border is visible ({edge}).");
        }

        await nested.KeysAsync("-M", "ctrl", "z", "-m", "ctrl");
        var undo = await Next<StrokesRemoved>();
        h.Check(undo.Reason == "undo" && undo.StrokeIds.SequenceEqual([first.Id]), "Ctrl+Z undoes the last stroke.");
        pointer.Drag([(200, 500), (300, 520)]);
        pointer.Drag([(400, 500), (500, 520)]);
        await Next<StrokeCompleted>();
        await Next<StrokeCompleted>();
        await nested.KeysAsync("-k", "Delete");
        var clear = await Next<StrokesRemoved>();
        h.Check(clear.Reason == "clear" && clear.StrokeIds.Length == 2, "Delete clears every visible stroke.");
        await nested.KeysAsync("-k", "m");
        await Next<MarkerRequested>();
        h.Check(true, "M while drawing requests a marker.");
        await nested.KeysAsync("-k", "Escape");
        var escaped = await Next<ModeChanged>();
        h.Check(escaped is { Mode: OverlayInputMode.PassThrough, Reason: "escape" } && overlay.Mode == OverlayInputMode.PassThrough,
            "Escape leaves drawing mode.");
        await Task.Delay(150);
        using (var shot = SKBitmap.Decode(await nested.ScreenshotAsync()))
            h.Check(shot.GetPixel(2, (int)(300 * ratio)).Red < 90 && shot.GetPixel((int)(250 * ratio), (int)(510 * ratio)).Red < 120,
                "After Escape and clear, no border or ink remains.");
        pointer.Drag([(100, 100), (300, 100)]);
        await Task.Delay(300);
        h.Check(Quiet<StrokeCompleted>(), "Input returns to the desktop after Escape.");

        await overlay.SetInputModeAsync(OverlayInputMode.Drawing, CancellationToken.None);
        pointer.MoveTo(640, 360);
        pointer.Press(right: true);
        pointer.Release(right: true);
        var right = await Next<ModeChanged>();
        while (right.Reason != "right-click") right = await Next<ModeChanged>();
        h.Check(overlay.Mode == OverlayInputMode.PassThrough, "Right-click leaves drawing mode.");

        await overlay.SetInputModeAsync(OverlayInputMode.Drawing, CancellationToken.None);
        pointer.Drag([(200, 400), (400, 400)]);
        await Next<StrokeCompleted>();
        pointer.Drag([(500, 400), (700, 400)]);
        await Next<StrokeCompleted>();
        await overlay.SetInputModeAsync(OverlayInputMode.PassThrough, CancellationToken.None);
        await overlay.UndoAsync(CancellationToken.None);
        h.Check((await Next<StrokesRemoved>()).Reason == "undo", "Command undo journals removal in pass-through.");
        await Task.Delay(150);
        using (var shot = SKBitmap.Decode(await nested.ScreenshotAsync()))
            h.Check(shot.GetPixel((int)(300 * ratio), (int)(400 * ratio)).Red > 200 &&
                shot.GetPixel((int)(600 * ratio), (int)(400 * ratio)).Red < 120, "Undo removes only the last stroke's pixels.");
        await overlay.ClearAsync(CancellationToken.None);
        h.Check((await Next<StrokesRemoved>()).Reason == "clear", "Command clear journals removal in pass-through.");
        await Task.Delay(150);
        using (var shot = SKBitmap.Decode(await nested.ScreenshotAsync()))
            h.Check(shot.GetPixel((int)(300 * ratio), (int)(400 * ratio)).Red < 120, "Clear removes the remaining ink pixels.");

        await InkOverlayChecks.RunAsync(h, nested, geometry, pointer, ratio);
        h.Section = "overlay";

        int before = overlay.Diagnostics.BuffersCreated;
        for (int i = 0; i < 20; i++)
        {
            await overlay.SetInputModeAsync(i % 2 == 0 ? OverlayInputMode.Drawing : OverlayInputMode.PassThrough, CancellationToken.None);
            if (i % 2 == 0) pointer.Drag([(100 + i * 10, 200), (150 + i * 10, 260)]);
        }
        await overlay.SetInputModeAsync(OverlayInputMode.PassThrough, CancellationToken.None);
        await Task.Delay(200);
        h.Check(overlay.Diagnostics.BuffersCreated == before && overlay.Diagnostics.BuffersLive <= 3,
            $"Buffers are reused, not reallocated ({overlay.Diagnostics.BuffersLive} live, {overlay.Diagnostics.FramesRendered} frames).");

        // Leave a drag held while the output disappears: its ink must still be reported.
        await overlay.SetInputModeAsync(OverlayInputMode.Drawing, CancellationToken.None);
        pointer.MoveTo(500, 500);
        pointer.Press();
        pointer.MoveTo(560, 520);
        await nested.HyprctlAsync("output", "remove", "TEST");
        var held = await Next<StrokeCompleted>(8000);
        h.Check(held.Stroke.Points.Length >= 1, "A drag still held when the overlay closes is kept as a stroke.");
        var closed = await Next<OverlayClosed>(8000);
        await overlay.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
        h.Check(closed.Reason.Contains("removed", StringComparison.OrdinalIgnoreCase) ||
                closed.Reason.Contains("closed", StringComparison.OrdinalIgnoreCase), "Output removal closes the overlay: " + closed.Reason);
        h.Check(overlay.Diagnostics.BuffersLive == 0, "Every buffer is freed after the overlay stops.");
        await h.RejectAsync<InvalidOperationException>(() => overlay.SetInputModeAsync(OverlayInputMode.Drawing, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5)), "Commands after the overlay stopped fail instead of hanging.");
        await overlay.DisposeAsync();
    }

    public static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "WayScrawl.slnx"))) return directory.FullName;
        return Environment.CurrentDirectory;
    }
}
