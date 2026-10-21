using System.Collections.Concurrent;
using System.Threading.Channels;
using CursorShape;
using FractionalScale;
using global::Wayland;
using global::Wayland.SourceGenerator;
using SkiaSharp;
using Viewporter;
using WayScrawl.Core;
using WayScrawl.Rendering;
using WayScrawl.Wayland.Interop;
using WlrLayerShellUnstable;

namespace WayScrawl.Wayland;

// Counters for checks: buffers must stay bounded however long a session draws.
public sealed class OverlayDiagnostics
{
    private int buffersCreated, buffersLive, framesRendered;
    public int BuffersCreated => Volatile.Read(ref buffersCreated);
    public int BuffersLive => Volatile.Read(ref buffersLive);
    public int FramesRendered => Volatile.Read(ref framesRendered);
    internal void Created(int count) { Interlocked.Add(ref buffersCreated, count); Interlocked.Add(ref buffersLive, count); }
    internal void Destroyed(int count) => Interlocked.Add(ref buffersLive, -count);
    internal void Rendered() => Interlocked.Increment(ref framesRendered);
}

public sealed record OverlayOptions(string Color = "#ff3b30", double Width = 6, string? Display = null, double InkFadeSeconds = 1.5)
{
    public static readonly string[] Palette = ["#ff3b30", "#ffcc00", "#34c759", "#0a84ff", "#bf5af2", "#ffffff"];
}

// A transparent wlr-layer-shell surface on one output. One dedicated thread owns the
// display, every proxy and every callback; other threads only post commands and read events.
// Pass-through: empty input region, no keyboard, nothing drawn but ink.
// Drawing: whole-surface input, exclusive keyboard, a thin border marks the mode.
public sealed class WaylandOverlay(OverlayOptions options) : IOverlayHost
{
    private readonly Channel<OverlayEvent> events = Channel.CreateUnbounded<OverlayEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentQueue<Action<Loop>> commands = new();
    // Completions handed to callers; whatever is still open when the loop ends is faulted.
    private readonly ConcurrentDictionary<TaskCompletionSource, byte> pending = new();
    private readonly TaskCompletionSource shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int wake = -1;
    private int inkCount, activeInk;
    private Thread? thread;
    private volatile OverlayInputMode mode = OverlayInputMode.PassThrough;
    private OverlayOptions Options { get; } = options;

    public OverlayInputMode Mode => mode;
    public int InkCount => Volatile.Read(ref inkCount) + Volatile.Read(ref activeInk);
    public OverlayDiagnostics Diagnostics { get; } = new();
    public ChannelReader<OverlayEvent> Events => events.Reader;
    public Task Stopped => stopped.Task;

    public Task ShowAsync(Geometry geometry, string outputName, ISessionClock clock, CancellationToken cancellationToken)
    {
        if (thread is not null) throw new InvalidOperationException("The overlay was already shown.");
        wake = Native.CreateEventFd();
        thread = new Thread(() => new Loop(this, geometry, outputName, clock).Run())
            { IsBackground = true, Name = "wayscrawl-wayland" };
        thread.Start();
        // A caller that gives up on showing also stops the loop, so no orphan overlay remains.
        cancellationToken.Register(() =>
        {
            if (!shown.Task.IsCompleted) Post(loop => loop.RequestExit("show cancelled"), null, throwIfStopped: false);
        });
        return shown.Task.WaitAsync(cancellationToken);
    }

    public Task SetInputModeAsync(OverlayInputMode next, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(loop => loop.SetMode(next, "command", done), done);
        return done.Task.WaitAsync(cancellationToken);
    }

    public Task ClearAsync(CancellationToken cancellationToken) => RemoveAsync(false, cancellationToken);
    public Task UndoAsync(CancellationToken cancellationToken) => RemoveAsync(true, cancellationToken);

    private Task RemoveAsync(bool undo, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(loop => { loop.RemoveInk(undo); done.TrySetResult(); }, done);
        return done.Task.WaitAsync(cancellationToken);
    }

    // Test and demo hook: draws a stroke as if the pointer had drawn it.
    public Task DrawAsync(IReadOnlyList<PointD> surfacePoints, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(loop => loop.Synthesize(surfacePoints, done), done);
        return done.Task.WaitAsync(cancellationToken);
    }

    public async Task HideAsync(CancellationToken cancellationToken)
    {
        if (thread is null) return;
        Post(loop => loop.RequestExit("hidden"), null, throwIfStopped: false);
        await stopped.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (thread is not null && !stopped.Task.IsCompleted)
            try { await HideAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token); }
            catch (OperationCanceledException) { }
        thread?.Join(TimeSpan.FromSeconds(2));
        if (wake >= 0 && stopped.Task.IsCompleted) { Native.Close(wake); wake = -1; }
    }

    private void Post(Action<Loop> command, TaskCompletionSource? done, bool throwIfStopped = true)
    {
        if (stopped.Task.IsCompleted)
        {
            if (throwIfStopped) throw new InvalidOperationException("The overlay is not running.");
            return;
        }
        if (done is not null)
        {
            pending.TryAdd(done, 0);
            _ = done.Task.ContinueWith((completed, state) => pending.TryRemove((TaskCompletionSource)state!, out _), done,
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        commands.Enqueue(command);
        Native.Signal(wake);
        // The loop may have ended between the check and the enqueue.
        if (stopped.Task.IsCompleted) FaultPending();
    }

    private void FaultPending()
    {
        foreach (var done in pending.Keys) done.TrySetException(new InvalidOperationException("The overlay stopped."));
    }

    private sealed class Loop : WlRegistry.IEvents, ZwlrLayerSurfaceV1.IEvents, WlPointer.IEvents, WlKeyboard.IEvents,
        WlSeat.IEvents, WpFractionalScaleV1.IEvents, WlSurface.IEvents
    {
        private const uint BtnLeft = 0x110, BtnRight = 0x111;
        // Physical evdev fallbacks, used only when no XKB keymap could be loaded.
        private const int KeyEsc = 1, KeyBackspace = 14, KeyZ = 44, KeyM = 50, KeyDelete = 111, Key1 = 2, Key6 = 7;

        private readonly WaylandOverlay owner;
        private readonly Geometry geometry;
        private readonly string outputName;
        private readonly ISessionClock clock;
        private readonly Dictionary<uint, (WlOutput Output, OutputInfo Info)> outputs = [];
        private readonly List<Stroke> strokes = [];
        private readonly List<Pin> pins = [];
        private readonly List<string> history = [];
        private readonly Dictionary<string, long> fading = [];
        private long? fadeDeadline, holdDeadline;
        private int pinCounter;
        private readonly HashSet<string> removed = [];
        private Display? display;
        private WlRegistry? registry;
        private WlCompositor? compositor;
        private WlShm? shm;
        private ZwlrLayerShellV1? layerShell;
        private WpFractionalScaleManagerV1? fractionalManager;
        private WpViewporter? viewporter;
        private WpCursorShapeManagerV1? cursorShapes;
        private WlSeat? seat;
        private WlPointer? pointer;
        private WlKeyboard? keyboard;
        private WpCursorShapeDeviceV1? cursor;
        private WlSurface? surface;
        private ZwlrLayerSurfaceV1? layer;
        private WpFractionalScaleV1? fractional;
        private WpViewport? viewport;
        private WlCallback? frameCallback;
        private ShmBufferSet? buffers;
        private readonly List<ShmBufferSet> retiring = [];
        private readonly Xkb xkb = new();
        // wl_callback proxies are only weakly held by the generated registry; keep them alive.
        private readonly HashSet<WlCallback> callbacks = [];
        private bool waitingForBuffer;
        private uint targetName;
        private uint? pendingConfigure;
        private int logicalWidth, logicalHeight;
        private uint scale120;
        private bool configured, mapped, dirty = true, exit, ctrl;
        private string? exitReason;
        private List<InkPoint>? active;
        private uint pointerSerial;
        private double pointerX, pointerY;
        private int strokeCounter;
        private string color;

        internal Loop(WaylandOverlay owner, Geometry geometry, string outputName, ISessionClock clock)
        {
            this.owner = owner;
            this.geometry = geometry;
            this.outputName = outputName;
            this.clock = clock;
            color = owner.Options.Color;
            // Until the compositor reports a preferred scale, assume the output's own pixel ratio.
            scale120 = (uint)Math.Max(120, Math.Round(geometry.PixelWidth / geometry.LogicalWidth * 120));
        }

        private long Now => clock.ElapsedUs;
        private static readonly bool Tracing = Environment.GetEnvironmentVariable("WAYSCRAWL_OVERLAY_TRACE") == "1";
        private static void Trace(string message) { if (Tracing) Console.Error.WriteLine("overlay: " + message); }

        internal void Run()
        {
            try
            {
                display = Display.Connect(owner.Options.Display);
                registry = display.GetRegistry();
                registry.Events = this;
                display.Roundtrip();
                display.Roundtrip();
                if (compositor is null || shm is null || layerShell is null)
                    throw new WaylandException("The compositor lacks wl_compositor, wl_shm or zwlr_layer_shell_v1.");
                var target = outputs.FirstOrDefault(o => o.Value.Info.Name == outputName);
                if (target.Value.Output is null)
                {
                    if (outputs.Count != 1) throw new WaylandException($"Output {outputName} was not found.");
                    target = outputs.First();
                }
                targetName = target.Key;
                CreateSurface(target.Value.Output);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!mapped && !exit)
                {
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("The overlay was never configured.");
                    Step(100);
                }
                owner.shown.TrySetResult();
                while (!exit) Step(-1);
            }
            catch (Exception error)
            {
                exitReason ??= error.Message;
                owner.shown.TrySetException(error);
            }
            finally
            {
                // Ink drawn while the button is still held belongs to the recording too.
                FinishStroke();
                Teardown();
                owner.events.Writer.TryWrite(new OverlayClosed(Now, exitReason ?? "closed"));
                owner.events.Writer.TryComplete();
                owner.stopped.TrySetResult();
                owner.FaultPending();
            }
        }

        private void CreateSurface(WlOutput output)
        {
            surface = Proxies.Child(compositor!, 0, WlSurface.BindFactory, [WlArgument.NewId]);
            surface.Events = this;
            if (fractionalManager is not null && viewporter is not null)
            {
                fractional = fractionalManager.GetFractionalScale(surface);
                fractional.Events = this;
                viewport = viewporter.GetViewport(surface);
            }
            using (var name = new Utf8Buffer("wayscrawl"))
                layer = Proxies.Child(layerShell!, 0, ZwlrLayerSurfaceV1.BindFactory,
                    [WlArgument.NewId, surface, output, (uint)ZwlrLayerShellV1.LayerEnum.Overlay, name]);
            layer.Events = this;
            layer.SetAnchor(ZwlrLayerSurfaceV1.AnchorEnum.Top | ZwlrLayerSurfaceV1.AnchorEnum.Bottom |
                            ZwlrLayerSurfaceV1.AnchorEnum.Left | ZwlrLayerSurfaceV1.AnchorEnum.Right);
            layer.SetExclusiveZone(-1);
            layer.SetSize(0, 0);
            layer.SetKeyboardInteractivity(ZwlrLayerSurfaceV1.KeyboardInteractivityEnum.None);
            ApplyInputRegion(false);
            surface.Commit(); // Bufferless: the role is assigned, then the compositor configures.
        }

        // One poll iteration. Every successful prepare_read ends in read_events or cancel_read.
        private void Step(int timeoutMs)
        {
            TickInk();
            var d = display!;
            d.DispatchPending();
            bool prepared = false;
            try
            {
                while (d.PrepareRead() != 0) d.DispatchPending();
                prepared = true;
                bool flushed = d.Flush();
                int timerMs = fadeDeadline is { } deadline ? (int)Math.Clamp((deadline - Now + 999) / 1000, 0, int.MaxValue) : -1;
                if (holdDeadline is { } hold)
                {
                    int holdMs = (int)Math.Clamp((hold - Now + 999) / 1000, 0, int.MaxValue);
                    timerMs = timerMs < 0 ? holdMs : Math.Min(timerMs, holdMs);
                }
                if (fading.Count > 0) timerMs = timerMs < 0 ? 16 : Math.Min(timerMs, 16);
                int bounded = timeoutMs < 0 ? timerMs : timerMs < 0 ? timeoutMs : Math.Min(timeoutMs, timerMs);
                int timeout = dirty && CanRender ? 0 : bounded;
                var ready = Native.Poll(d.FileDescriptor, owner.wake, !flushed, timeout);
                if (ready.DisplayFailed && !ready.DisplayReadable) throw new WaylandException("The Wayland connection closed.");
                if (ready.DisplayReadable)
                {
                    prepared = false;
                    if (d.ReadEvents() < 0) throw new WaylandException("Reading Wayland events failed.");
                }
                else
                {
                    prepared = false;
                    d.CancelRead();
                }
                if (ready.DisplayWritable) d.Flush();
                d.DispatchPending();
                if (ready.WakeReadable) Native.Drain(owner.wake);
                while (owner.commands.TryDequeue(out var command)) command(this);
                TickInk();
                if (dirty && CanRender) Render();
                d.Flush();
            }
            finally
            {
                if (prepared) d.CancelRead();
            }
        }

        private bool CanRender => configured && frameCallback is null && !waitingForBuffer && !exit && surface is not null;

        private void Render()
        {
            int bw = (int)Math.Round(logicalWidth * scale120 / 120.0);
            int bh = (int)Math.Round(logicalHeight * scale120 / 120.0);
            if (viewport is null)
            {
                int integer = (int)Math.Ceiling(scale120 / 120.0);
                bw = logicalWidth * integer;
                bh = logicalHeight * integer;
            }
            if (buffers is null || buffers.Width != bw || buffers.Height != bh)
            {
                if (buffers is not null) { buffers.Retire(); retiring.Add(buffers); }
                buffers = new ShmBufferSet(shm!, bw, bh, 3, () => { waitingForBuffer = false; dirty = true; }, owner.Diagnostics);
            }
            retiring.RemoveAll(set => set.AllReleased);
            var target = buffers.Acquire();
            if (target is null)
            {
                // Every buffer is with the compositor: wait for a release instead of spinning.
                waitingForBuffer = true;
                return;
            }

            using (var canvas = new SKCanvas(target.Bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                canvas.Save();
                // Strokes are stored in source pixels; map them onto this buffer.
                canvas.Scale((float)(bw / (double)geometry.PixelWidth), (float)(bh / (double)geometry.PixelHeight));
                foreach (var stroke in strokes)
                    if (!removed.Contains(stroke.Id) || fading.ContainsKey(stroke.Id))
                        InkRenderer.Draw(canvas, stroke.Points, InkRenderer.ParseColor(stroke.Color).WithAlpha(Alpha(stroke.Id)), (float)stroke.Width);
                foreach (var pin in pins)
                    if (!removed.Contains(pin.Id) || fading.ContainsKey(pin.Id))
                        InkRenderer.DrawPin(canvas, pin, SourcePerLogical, Alpha(pin.Id));
                if (active is { Count: > 0 })
                    InkRenderer.Draw(canvas, active, InkRenderer.ParseColor(color), (float)owner.Options.Width * SourcePerLogical);
                canvas.Restore();
                if (owner.mode == OverlayInputMode.Drawing)
                {
                    using var border = new SKPaint
                    {
                        Color = InkRenderer.ParseColor(color).WithAlpha(150), Style = SKPaintStyle.Stroke,
                        StrokeWidth = 4 * scale120 / 120f
                    };
                    canvas.DrawRect(2 * scale120 / 120f, 2 * scale120 / 120f, bw - 4 * scale120 / 120f, bh - 4 * scale120 / 120f, border);
                }
            }
            if (pendingConfigure is { } serial)
            {
                layer!.AckConfigure(serial);
                pendingConfigure = null;
            }
            if (viewport is not null) viewport.SetDestination(logicalWidth, logicalHeight);
            else surface!.SetBufferScale(bw / Math.Max(1, logicalWidth));
            surface!.Attach(target.Buffer, 0, 0);
            surface.DamageBuffer(0, 0, bw, bh);
            frameCallback = surface.Frame();
            frameCallback.Events = new FrameDone(this, frameCallback);
            callbacks.Add(frameCallback);
            surface.Commit();
            target.Busy = true;
            mapped = true;
            dirty = false;
            owner.Diagnostics.Rendered();
        }

        private float SourcePerLogical => (float)(geometry.PixelWidth / geometry.LogicalWidth);

        internal void SetMode(OverlayInputMode next, string reason, TaskCompletionSource? done)
        {
            if (owner.mode != next)
            {
                owner.mode = next;
                holdDeadline = next == OverlayInputMode.Holding ? Now + 10_000_000 : null;
                if (next == OverlayInputMode.PassThrough) FinishStroke(allowPin: true);
                ApplyInputRegion(next != OverlayInputMode.PassThrough);
                layer!.SetKeyboardInteractivity(next == OverlayInputMode.Drawing
                    ? ZwlrLayerSurfaceV1.KeyboardInteractivityEnum.Exclusive
                    : ZwlrLayerSurfaceV1.KeyboardInteractivityEnum.None);
                surface!.Commit();
                dirty = true;
                if (next != OverlayInputMode.PassThrough) UpdateCursor();
                owner.events.Writer.TryWrite(new ModeChanged(Now, next, reason));
            }
            if (done is null) return;
            // The sync reply proves the compositor applied the region and keyboard policy.
            var sync = display!.Sync();
            sync.Events = new SyncDone(this, sync, done);
            callbacks.Add(sync);
        }

        internal void Synthesize(IReadOnlyList<PointD> points, TaskCompletionSource done)
        {
            foreach (var point in points) AddPoint(point.X, point.Y, begin: active is null);
            FinishStroke();
            done.TrySetResult();
        }

        internal void RequestExit(string reason)
        {
            exitReason ??= reason;
            exit = true;
        }

        private void ApplyInputRegion(bool everything)
        {
            if (everything)
            {
                surface!.SetInputRegion(null);
                return;
            }
            var region = Proxies.Child(compositor!, 1, WlRegion.BindFactory, [WlArgument.NewId]);
            surface!.SetInputRegion(region); // An empty region passes every event through.
            region.Destroy();
        }

        private void AddPoint(double surfaceX, double surfaceY, bool begin)
        {
            double sx = logicalWidth > 0 ? geometry.LogicalWidth / logicalWidth : 1;
            double sy = logicalHeight > 0 ? geometry.LogicalHeight / logicalHeight : 1;
            var source = geometry.ToSource(new(geometry.LogicalX + surfaceX * sx, geometry.LogicalY + surfaceY * sy));
            if (begin) { active = []; Volatile.Write(ref owner.activeInk, 1); fadeDeadline = null; }
            active!.Add(new InkPoint(Now, source.X, source.Y));
            dirty = true;
        }

        private void FinishStroke(bool allowPin = false)
        {
            if (active is not { Count: > 0 } points) { active = null; return; }
            active = null;
            Volatile.Write(ref owner.activeInk, 0);
            long now = Now;
            if (allowPin && now - points[0].AtUs <= 1_000_000 && points.All(p =>
                Math.Pow((p.X - points[0].X) / SourcePerLogical, 2) + Math.Pow((p.Y - points[0].Y) / SourcePerLogical, 2) < 144))
            {
                var pin = new Pin("p" + ++pinCounter, geometry.Id, pinCounter, points[0].X, points[0].Y, now);
                pins.Add(pin);
                history.Add(pin.Id);
                Interlocked.Increment(ref owner.inkCount);
                owner.events.Writer.TryWrite(new PinPlaced(now, pin));
                ArmFade(now);
                dirty = true;
                return;
            }
            var stroke = new Stroke("s" + ++strokeCounter, geometry.Id, color, owner.Options.Width * SourcePerLogical, points.ToArray());
            strokes.Add(stroke);
            history.Add(stroke.Id);
            ArmFade(now);
            Interlocked.Increment(ref owner.inkCount);
            owner.events.Writer.TryWrite(new StrokeCompleted(Now, stroke));
            dirty = true;
        }

        private void ArmFade(long now)
        {
            if (owner.Options.InkFadeSeconds > 0)
                fadeDeadline = now + (long)(owner.Options.InkFadeSeconds * 1_000_000);
        }

        private byte Alpha(string id)
        {
            if (!fading.TryGetValue(id, out long start)) return 255;
            double remaining = Math.Clamp(1 - (Now - start) / 300_000d, 0, 1);
            return (byte)(255 * remaining * remaining);
        }

        private void TickInk()
        {
            if (holdDeadline is { } hold && Now >= hold) SetMode(OverlayInputMode.PassThrough, "hold-timeout", null);
            if (fadeDeadline is { } deadline && Now >= deadline)
            {
                fadeDeadline = null;
                Remove(history.ToArray(), "fade");
            }
            foreach (string id in fading.Where(p => Now - p.Value >= 300_000).Select(p => p.Key).ToArray())
            {
                fading.Remove(id);
                strokes.RemoveAll(s => s.Id == id);
                pins.RemoveAll(p => p.Id == id);
                removed.Remove(id);
                dirty = true;
            }
            if (fading.Count > 0) dirty = true;
        }

        internal void RemoveInk(bool undo)
        {
            FinishStroke();
            Remove(undo ? history.TakeLast(1).ToArray() : history.ToArray(), undo ? "undo" : "clear");
        }

        private void Remove(IEnumerable<string> victims, string reason)
        {
            string[] ids = victims.Where(removed.Add).ToArray();
            if (ids.Length == 0) return;
            long now = Now;
            Interlocked.Add(ref owner.inkCount, -ids.Length);
            foreach (string id in ids)
            {
                history.Remove(id);
                if (reason == "fade") fading[id] = now;
                else
                {
                    strokes.RemoveAll(s => s.Id == id);
                    pins.RemoveAll(p => p.Id == id);
                    removed.Remove(id);
                }
            }
            owner.events.Writer.TryWrite(new StrokesRemoved(now, ids, reason));
            dirty = true;
        }

        private void UpdateCursor()
        {
            if (cursor is not null && pointerSerial != 0 && owner.mode != OverlayInputMode.PassThrough)
                cursor.SetShape(pointerSerial, WpCursorShapeDeviceV1.ShapeEnum.Crosshair);
        }

        private void Teardown()
        {
            try
            {
                if (display is not null && surface is not null && mapped)
                {
                    // Unmap, then let the compositor release every buffer before freeing memory.
                    ApplyInputRegion(false);
                    layer?.SetKeyboardInteractivity(ZwlrLayerSurfaceV1.KeyboardInteractivityEnum.None);
                    surface.Attach(null, 0, 0);
                    surface.Commit();
                    display.Roundtrip();
                }
                cursor?.Destroy();
                if (pointer is { Version: >= 3 }) pointer.Release();
                if (keyboard is { Version: >= 3 }) keyboard.Release();
                viewport?.Destroy();
                fractional?.Destroy();
                layer?.Destroy();
                surface?.Destroy();
                display?.Roundtrip();
                // Globals and outstanding callbacks too: disconnecting does not free client proxies.
                foreach (var callback in callbacks.ToList()) callback.DestroyProxy();
                callbacks.Clear();
                foreach (var (output, _) in outputs.Values)
                    if (output.Version >= 3) output.Release(); else output.DestroyProxy();
                if (seat is not null) { if (seat.Version >= 5) seat.Release(); else seat.DestroyProxy(); }
                cursorShapes?.Destroy();
                viewporter?.Destroy();
                fractionalManager?.Destroy();
                if (layerShell is not null) { if (layerShell.Version >= 3) layerShell.Destroy(); else layerShell.DestroyProxy(); }
                if (shm is not null) { if (shm.Version >= 2) shm.Release(); else shm.DestroyProxy(); }
                compositor?.DestroyProxy();
                registry?.DestroyProxy();
                display?.Flush();
            }
            catch (Exception) { /* The connection may already be gone; memory is freed below. */ }
            buffers?.Dispose();
            foreach (var set in retiring) set.Dispose();
            xkb.Dispose();
            display?.Dispose();
        }

        public void OnGlobal(uint name, string @interface, uint version)
        {
            switch (@interface)
            {
                case "wl_compositor": compositor = registry!.Bind(name, version, WlCompositor.BindFactory); break;
                case "wl_shm": shm = registry!.Bind(name, version, WlShm.BindFactory); break;
                case "zwlr_layer_shell_v1": layerShell = registry!.Bind(name, version, ZwlrLayerShellV1.BindFactory); break;
                case "wp_fractional_scale_manager_v1": fractionalManager = registry!.Bind(name, version, WpFractionalScaleManagerV1.BindFactory); break;
                case "wp_viewporter": viewporter = registry!.Bind(name, version, WpViewporter.BindFactory); break;
                case "wp_cursor_shape_manager_v1": cursorShapes = registry!.Bind(name, version, WpCursorShapeManagerV1.BindFactory); break;
                case "wl_seat" when seat is null:
                    seat = registry!.Bind(name, version, WlSeat.BindFactory);
                    seat.Events = this;
                    break;
                case "wl_output":
                    var output = registry!.Bind(name, version, WlOutput.BindFactory);
                    var info = new OutputInfo();
                    output.Events = info;
                    outputs[name] = (output, info);
                    break;
            }
        }

        public void OnGlobalRemove(uint name)
        {
            if (name == targetName && mapped) RequestExit("The recorded output was removed.");
        }

        // Objects are created when a capability first appears and then kept. Hyprland 0.56 announces
        // spurious capability removals whenever a virtual keyboard (wtype, dictation tools) comes and
        // goes while the real devices stay; releasing on those would leave the overlay deaf.
        public void OnCapabilities(WlSeat.CapabilityEnum capabilities)
        {
            Trace($"seat capabilities {capabilities}");
            if (capabilities.HasFlag(WlSeat.CapabilityEnum.Pointer) && pointer is null)
            {
                pointer = Proxies.Child(seat!, 0, WlPointer.BindFactory, [WlArgument.NewId]);
                pointer.Events = this;
                if (cursorShapes is not null) cursor = cursorShapes.GetPointer(pointer);
            }
            if (capabilities.HasFlag(WlSeat.CapabilityEnum.Keyboard) && keyboard is null)
            {
                keyboard = Proxies.Child(seat!, 1, WlKeyboard.BindFactory, [WlArgument.NewId]);
                keyboard.Events = this;
            }
        }

        public void OnName(string name) { }

        public void OnConfigure(uint serial, uint width, uint height)
        {
            Trace($"configure {width}x{height} serial {serial}");
            logicalWidth = width > 0 ? (int)width : (int)Math.Round(geometry.LogicalWidth);
            logicalHeight = height > 0 ? (int)height : (int)Math.Round(geometry.LogicalHeight);
            pendingConfigure = serial;
            configured = true;
            dirty = true;
        }

        public void OnClosed() => RequestExit("The compositor closed the overlay.");

        public void OnPreferredScale(uint scale)
        {
            if (scale == scale120 || scale == 0) return;
            scale120 = scale;
            dirty = true;
        }

        public void OnEnter(WlOutput output) { }
        public void OnLeave(WlOutput output) { }
        public void OnPreferredBufferScale(int factor)
        {
            if (viewport is null && factor > 0) { scale120 = (uint)factor * 120; dirty = true; }
        }
        public void OnPreferredBufferTransform(WlOutput.TransformEnum transform) { }

        public void OnEnter(uint serial, WlSurface entered, WlFixed x, WlFixed y)
        {
            Trace($"pointer enter {(double)x:0.0},{(double)y:0.0}");
            pointerSerial = serial;
            pointerX = (double)x;
            pointerY = (double)y;
            UpdateCursor();
        }

        public void OnLeave(uint serial, WlSurface left) => FinishStroke();

        public void OnMotion(uint time, WlFixed x, WlFixed y)
        {
            Trace($"motion {(double)x:0.0},{(double)y:0.0}");
            pointerX = (double)x;
            pointerY = (double)y;
            if (active is not null && owner.mode != OverlayInputMode.PassThrough) AddPoint(pointerX, pointerY, begin: false);
        }

        public void OnButton(uint serial, uint time, uint button, WlPointer.ButtonStateEnum state)
        {
            Trace($"button {button} {state} in {owner.mode}");
            if (owner.mode == OverlayInputMode.PassThrough) return;
            bool pressed = state == WlPointer.ButtonStateEnum.Pressed;
            if (button == BtnLeft && pressed) AddPoint(pointerX, pointerY, begin: true);
            else if (button == BtnLeft) FinishStroke(allowPin: true);
            else if (button == BtnRight && pressed) SetMode(OverlayInputMode.PassThrough, "right-click", null);
        }

        public void OnAxis(uint time, WlPointer.AxisEnum axis, WlFixed value) { }
        public void OnFrame() { }
        public void OnAxisSource(WlPointer.AxisSourceEnum axisSource) { }
        public void OnAxisStop(uint time, WlPointer.AxisEnum axis) { }
        public void OnAxisDiscrete(WlPointer.AxisEnum axis, int discrete) { }
        public void OnAxisValue120(WlPointer.AxisEnum axis, int value120) { }
        public void OnAxisRelativeDirection(WlPointer.AxisEnum axis, WlPointer.AxisRelativeDirectionEnum direction) { }

        public void OnKeymap(WlKeyboard.KeymapFormatEnum format, int fd, uint size)
        {
            if (!xkb.Load((int)format, fd, size)) Trace("keymap unusable; using physical key codes");
        }
        public void OnEnter(uint serial, WlSurface entered, ReadOnlySpan<int> keys) => Trace("keyboard enter");
        void WlKeyboard.IEvents.OnLeave(uint serial, WlSurface left)
        {
            Trace("keyboard leave");
            ctrl = false;
        }
        public void OnModifiers(uint serial, uint depressed, uint latched, uint locked, uint group)
        {
            xkb.UpdateModifiers(depressed, latched, locked, group);
            ctrl = xkb.Ready ? xkb.Control : (depressed & 4) != 0;
        }
        public void OnRepeatInfo(int rate, int delay) { }

        public void OnKey(uint serial, uint time, uint key, WlKeyboard.KeyStateEnum state)
        {
            Trace($"key {key} {state} ctrl={ctrl}");
            if (state != WlKeyboard.KeyStateEnum.Pressed || owner.mode != OverlayInputMode.Drawing) return;
            var action = Classify(key);
            switch (action)
            {
                case Shortcut.Exit:
                    SetMode(OverlayInputMode.PassThrough, "escape", null);
                    break;
                case Shortcut.Undo when ctrl:
                    RemoveInk(true);
                    break;
                case Shortcut.Clear:
                    RemoveInk(false);
                    break;
                case Shortcut.Marker:
                    owner.events.Writer.TryWrite(new MarkerRequested(Now, "Marked while drawing"));
                    break;
                case >= Shortcut.Color1 and <= Shortcut.Color6:
                    color = OverlayOptions.Palette[action - Shortcut.Color1];
                    dirty = true;
                    break;
            }
        }

        private enum Shortcut { None, Exit, Undo, Clear, Marker, Color1, Color2, Color3, Color4, Color5, Color6 }

        private Shortcut Classify(uint key)
        {
            if (xkb.Ready)
                return xkb.Keysym(key) switch
                {
                    Xkb.Escape => Shortcut.Exit,
                    'z' => Shortcut.Undo,
                    Xkb.Delete or Xkb.BackSpace => Shortcut.Clear,
                    'm' => Shortcut.Marker,
                    var sym and >= '1' and <= '6' => Shortcut.Color1 + (int)(sym - '1'),
                    _ => Shortcut.None
                };
            return (int)key switch
            {
                KeyEsc => Shortcut.Exit,
                KeyZ => Shortcut.Undo,
                KeyDelete or KeyBackspace => Shortcut.Clear,
                KeyM => Shortcut.Marker,
                >= Key1 and <= Key6 => Shortcut.Color1 + ((int)key - Key1),
                _ => Shortcut.None
            };
        }

        // The compositor destroys a callback after done; the client frees its proxy.
        private void Retire(WlCallback callback)
        {
            callbacks.Remove(callback);
            callback.DestroyProxy();
        }

        private sealed class FrameDone(Loop loop, WlCallback self) : WlCallback.IEvents
        {
            public void OnDone(uint callbackData)
            {
                loop.frameCallback = null;
                loop.Retire(self);
            }
        }

        private sealed class SyncDone(Loop loop, WlCallback self, TaskCompletionSource done) : WlCallback.IEvents
        {
            public void OnDone(uint callbackData)
            {
                loop.Retire(self);
                done.TrySetResult();
            }
        }
    }

    private sealed class OutputInfo : WlOutput.IEvents
    {
        public string? Name { get; private set; }
        public void OnGeometry(int x, int y, int physicalWidth, int physicalHeight, WlOutput.SubpixelEnum subpixel,
            string make, string model, WlOutput.TransformEnum transform) { }
        public void OnMode(WlOutput.ModeEnum flags, int width, int height, int refresh) { }
        public void OnDone() { }
        public void OnScale(int factor) { }
        public void OnName(string name) => Name = name;
        public void OnDescription(string description) { }
    }
}
