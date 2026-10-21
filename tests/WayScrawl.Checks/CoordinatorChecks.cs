using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using WayScrawl.Application;
using WayScrawl.Core;
using WayScrawl.Platform;

namespace WayScrawl.Checks;

internal static class CoordinatorChecks
{
    private sealed class ManualTime : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => 1_000_000;
        public override long GetTimestamp() => Now;
    }

    internal sealed class FakeBackend(string fixture) : ICaptureBackend
    {
        public Geometry? GeometryOverride { get; set; }
        public TaskCompletionSource? PrepareGate { get; set; }
        public bool FailBeforeReady { get; set; }
        public TaskCompletionSource? ReadyGate { get; set; }
        public int Starts;
        public FakeRecording? Last;
        public string Name => "fake";

        public Task<Capability[]> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(Array.Empty<Capability>());

        public async Task<IPreparedCapture> PrepareAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            if (PrepareGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            return new Prepared(this, request);
        }

        private sealed class Prepared(FakeBackend owner, CaptureRequest request) : IPreparedCapture
        {
            public CaptureSource Source { get; } = new("fake", owner.GeometryOverride ?? new("g0", 0, 0, 640, 360, 640, 360, "monitor:TEST"),
                CompositionMode.Unknown, "fake output");

            public Task<IRecording> StartAsync(ISessionClock clock, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.Starts);
                string video = Path.Combine(request.WorkDirectory, "recording.mkv");
                File.Copy(owner.Fixture, video);
                owner.Last = new FakeRecording(video, owner.FailBeforeReady, owner.ReadyGate);
                return Task.FromResult<IRecording>(owner.Last);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public string Fixture => fixture;
    }

    internal sealed class FakeRecording(string video, bool fail, TaskCompletionSource? gate) : IRecording
    {
        private readonly TaskCompletionSource<RecordingResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Stops;
        public Task Ready { get; } = gate?.Task ?? (fail ? Task.FromException(new IOException("no frames")) : Task.CompletedTask);
        public Task<RecordingResult> Completion => completion.Task;

        public void Exit() => completion.TrySetResult(Result());

        // Like a real recorder, finalization takes a moment, so concurrent stops overlap.
        public async Task<RecordingResult> StopAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Stops);
            await Task.Delay(300, cancellationToken);
            completion.TrySetResult(Result());
            return await completion.Task;
        }

        private RecordingResult Result()
        {
            var clock = new ClockMap(0, ClockQuality.Measured, "CLOCK_MONOTONIC", "fake", 33_333);
            return new(video, true, clock, null, []);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Stands in for the Wayland overlay: the check pushes the events a user's drawing would produce.
    private sealed class FakeOverlay : IOverlayHost
    {
        private readonly System.Threading.Channels.Channel<OverlayEvent> channel =
            System.Threading.Channels.Channel.CreateUnbounded<OverlayEvent>();
        public ISessionClock? Clock { get; private set; }
        public bool Shown, Hidden;
        public TaskCompletionSource? ShowGate;
        private readonly List<string> ink = [];
        public int InkCount => ink.Count;
        public OverlayInputMode Mode { get; private set; }
        public System.Threading.Channels.ChannelReader<OverlayEvent> Events => channel.Reader;
        public Task ShowAsync(Geometry geometry, string outputName, ISessionClock clock, CancellationToken cancellationToken)
        {
            Clock = clock;
            Shown = outputName == "TEST";
            return ShowGate?.Task ?? Task.CompletedTask;
        }
        public Task SetInputModeAsync(OverlayInputMode mode, CancellationToken cancellationToken)
        {
            Mode = mode;
            channel.Writer.TryWrite(new ModeChanged(Clock!.ElapsedUs, mode, "command"));
            return Task.CompletedTask;
        }
        public int Clears, Undos;
        public Task ClearAsync(CancellationToken cancellationToken)
        {
            Clears++;
            if (ink.Count > 0) Emit(new StrokesRemoved(Clock!.ElapsedUs, ink.ToArray(), "clear"));
            return Task.CompletedTask;
        }
        public Task UndoAsync(CancellationToken cancellationToken)
        {
            Undos++;
            if (ink.LastOrDefault() is { } id) Emit(new StrokesRemoved(Clock!.ElapsedUs, [id], "undo"));
            return Task.CompletedTask;
        }
        public void Emit(OverlayEvent item)
        {
            if (item is StrokeCompleted stroke) ink.Add(stroke.Stroke.Id);
            if (item is PinPlaced pin) ink.Add(pin.Pin.Id);
            if (item is StrokesRemoved removed) ink.RemoveAll(id => removed.StrokeIds.Contains(id));
            channel.Writer.TryWrite(item);
        }
        public Task HideAsync(CancellationToken cancellationToken)
        {
            Hidden = true;
            channel.Writer.TryWrite(new OverlayClosed(Clock!.ElapsedUs, "hidden"));
            channel.Writer.TryComplete();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HoldEvents : IHoldControl
    {
        private readonly System.Threading.Channels.Channel<HoldControlEvent> events = System.Threading.Channels.Channel.CreateUnbounded<HoldControlEvent>();
        public IAsyncEnumerable<HoldControlEvent> ReadAsync(ISessionClock clock, CancellationToken cancellationToken) => events.Reader.ReadAllAsync(cancellationToken);
        public void Press(bool down) => events.Writer.TryWrite(new(down));
        public void Fail() => events.Writer.TryComplete(new IOException("hold setup lost"));
    }

    private sealed class Observations : IInteractionSource
    {
        private readonly System.Threading.Channels.Channel<InteractionSample> events = System.Threading.Channels.Channel.CreateUnbounded<InteractionSample>();
        public Capability[] Describe() => [];
        public InteractionSourceInfo Statistics => new("observation-check", "arrival", 0, 0, 0, "fixture");
        public IAsyncEnumerable<InteractionSample> ReadAsync(ISessionClock clock, CancellationToken cancellationToken) => events.Reader.ReadAllAsync(cancellationToken);
        public void Focus(long atUs) => events.Writer.TryWrite(new(atUs, "observation-check", "arrival", "window.focus", "still tracked", 0));
    }

    public static async Task RunAsync(Harness h)
    {
        h.Section = "coordinator";
        string root = h.TempDirectory("coord");
        string fixture = Path.Combine(root, "fixture.mkv");
        await ProcessRunner.RunCheckedAsync("ffmpeg", ["-nostdin", "-v", "error", "-f", "lavfi",
            "-i", "testsrc2=size=320x180:rate=30:duration=6", "-c:v", "libx264", "-preset", "ultrafast", fixture], CancellationToken.None);
        var paths = new WayScrawlPaths(Path.Combine(root, "run"), Path.Combine(root, "state"), Path.Combine(root, "data"),
            Path.Combine(root, "config"));
        var config = new WayScrawlConfig(Notifications: false, CopyPathToClipboard: false, Overlay: false, Telemetry: "off");
        var backend = new FakeBackend(fixture);
        var time = new ManualTime();
        Task<(int, string)> Process(string directory) =>
            new EvidenceBuilder(paths, config, null).BuildAsync(directory, CancellationToken.None)
                .ContinueWith(t => t.IsCompletedSuccessfully ? (0, t.Result) : (1, t.Exception?.GetBaseException().Message ?? "failed"));
        var holdEvents = new HoldEvents();
        var holdOverlay = new FakeOverlay();
        await using (var held = new SessionCoordinator(paths, config with { Overlay = true, DrawTrigger = "hold" }, backend,
            overlayFactory: () => holdOverlay, holdFactory: () => holdEvents, processor: Process))
        {
            Task<(bool Ok, string Message)> Control(string command, string? argument = null) =>
                held.ExecuteAsync(new(Guid.NewGuid().ToString("N"), command, argument), CancellationToken.None);
            await Control(DaemonCommands.Start);
            await PlatformChecks.WaitForAsync(() => holdOverlay.Shown);
            holdEvents.Press(true);
            await PlatformChecks.WaitForAsync(() => holdOverlay.Mode == OverlayInputMode.Holding);
            await Control(DaemonCommands.Draw, "toggle");
            h.Check(holdOverlay.Mode == OverlayInputMode.Drawing, "The draw hotkey enables explicit Drawing even when its modifier already started Holding.");
            holdEvents.Press(false);
            await Task.Delay(80);
            h.Check(holdOverlay.Mode == OverlayInputMode.Drawing, "Releasing the hold key preserves explicit Drawing and its exclusive keyboard.");
            holdEvents.Press(true);
            holdEvents.Press(false);
            await Task.Delay(80);
            h.Check(holdOverlay.Mode == OverlayInputMode.Drawing, "Pressing and releasing a configured hold modifier never hands Drawing's keyboard back to an application.");
            await Control(DaemonCommands.Draw, "off");
            string heldRaw = Path.Combine(paths.Sessions, held.Status.SessionId!);
            holdEvents.Press(true);
            await PlatformChecks.WaitForAsync(() => holdOverlay.Mode == OverlayInputMode.Holding);
            holdEvents.Press(false);
            await PlatformChecks.WaitForAsync(() => holdOverlay.Mode == OverlayInputMode.PassThrough);
            await Task.Delay(80);
            var journal = SessionJournal.Read(Path.Combine(heldRaw, SessionRecord.JournalName));
            h.Check(journal.Events.Count(e => e.Kind is "draw.on" or "draw.off") == 2 &&
                journal.Events.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, journal.Events.Length).Select(i => (long)i)),
                "Typing a capital letter with hold enabled adds no key-only span, timestamp or sequence-count gap to the journal.");
            holdEvents.Press(true);
            await PlatformChecks.WaitForAsync(() => holdOverlay.Mode == OverlayInputMode.Holding);
            long at = holdOverlay.Clock!.ElapsedUs;
            holdOverlay.Emit(new PinPlaced(at, new("p-held", "g0", 1, 200, 100, at)));
            holdEvents.Press(false);
            await PlatformChecks.WaitForAsync(() => holdOverlay.Mode == OverlayInputMode.PassThrough);
            await Task.Delay(80);
            journal = SessionJournal.Read(Path.Combine(heldRaw, SessionRecord.JournalName));
            h.Check(journal.Pins.Length == 1 && journal.Events.Count(e => e.Kind is "draw.on" or "draw.off") == 4,
                "A Holding span that produced actual ink is journaled around the ink.");
            h.Check((await Control(DaemonCommands.Clear)).Message == "Cleared." && holdOverlay.InkCount == 0 &&
                (await Control(DaemonCommands.Clear)).Message == "Nothing to clear.",
                "Clear distinguishes real ink from an already empty overlay through the production coordinator.");
            await Control(DaemonCommands.Cancel);
        }
        var observations = new Observations();
        var brokenHold = new HoldEvents();
        var trackedOverlay = new FakeOverlay();
        await using (var tracked = new SessionCoordinator(paths, config with { Overlay = true, DrawTrigger = "hold", Telemetry = "socket" }, backend,
            overlayFactory: () => trackedOverlay, telemetryFactory: _ => observations, holdFactory: () => brokenHold, processor: Process))
        {
            await tracked.ExecuteAsync(new("track-start", DaemonCommands.Start), CancellationToken.None);
            await PlatformChecks.WaitForAsync(() => trackedOverlay.Shown);
            brokenHold.Press(true);
            await PlatformChecks.WaitForAsync(() => trackedOverlay.Mode == OverlayInputMode.Holding);
            brokenHold.Fail();
            await PlatformChecks.WaitForAsync(() => trackedOverlay.Mode == OverlayInputMode.PassThrough);
            observations.Focus(123);
            await Task.Delay(100);
            var journal = SessionJournal.Read(Path.Combine(paths.Sessions, tracked.Status.SessionId!, SessionRecord.JournalName));
            h.Check(journal.Events.Any(e => e.Kind == "window.focus" && e.Detail == "still tracked"),
                "A failed hold control releases its grab while the independent window telemetry stream keeps recording.");
            await tracked.ExecuteAsync(new("track-cancel", DaemonCommands.Cancel), CancellationToken.None);
        }
        int unexpectedHold = 0;
        backend.PrepareGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var noOverlay = new SessionCoordinator(paths, config with { DrawTrigger = "hold" }, backend,
            holdFactory: () => { unexpectedHold++; return new HoldEvents(); }, processor: Process))
        {
            var toggles = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => noOverlay.ExecuteAsync(new("idle-toggle-" + i, DaemonCommands.Toggle), CancellationToken.None)));
            h.Check(toggles.All(t => t.Ok) && toggles.Count(t => t.Message.StartsWith("Preparing")) == 1,
                "Concurrent idle toggles admit one session and acknowledge the double tap.");
            backend.PrepareGate.SetResult();
            await PlatformChecks.WaitForAsync(() => noOverlay.Status.State == "Recording");
            h.Check(unexpectedHold == 0, "Hold binds are never installed when no overlay exists.");
            await noOverlay.ExecuteAsync(new("no-overlay-cancel", DaemonCommands.Cancel), CancellationToken.None);
        }
        backend.PrepareGate = null;
        await using var coordinator = new SessionCoordinator(paths, config, backend, processor: Process, timeProvider: time);
        Task<(bool Ok, string Message)> Send(string command, string? argument = null) =>
            coordinator.ExecuteAsync(new(Guid.NewGuid().ToString("N"), command, argument), CancellationToken.None);

        var started = await Send(DaemonCommands.Start);
        h.Check(started.Ok, "Start accepted: " + started.Message);
        await PlatformChecks.WaitForAsync(() => coordinator.Status.State == "Recording");
        h.Check(!(await Send(DaemonCommands.Start)).Ok, "A second start is refused while recording.");
        h.Check((await Send(DaemonCommands.Marker, "here")).Ok, "Marker accepted while recording.");
        h.Check(!(await Send(DaemonCommands.Draw, "on")).Ok, "Draw is refused when there is no overlay.");
        await Task.Delay(1200);
        var stops = await Task.WhenAll(Send(DaemonCommands.Stop), Send(DaemonCommands.Stop));
        h.Check(stops.All(s => s.Ok) && stops[0].Message == stops[1].Message, "Concurrent stops join one finalization.");
        await PlatformChecks.WaitForAsync(() => coordinator.Status is { State: "Idle", Processing: 0, LastBundle: not null }, 60_000);
        var bundle = await BundleStore.ReadAsync(coordinator.Status.LastBundle!);
        h.Check(bundle.Keyframes.Any(k => k.Reason == "Marker: here") && bundle.Media.Length == 1, "Recording processed into a bundle.");
        h.Check(!(await Send(DaemonCommands.Clear)).Ok && !(await Send(DaemonCommands.Undo)).Ok, "Ink commands while idle fail.");
        h.Check(!(await Send(DaemonCommands.Stop)).Ok, "Stop while idle reports not recording.");

        backend.PrepareGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int startsBefore = backend.Starts;
        await Send(DaemonCommands.Start);
        h.Check(coordinator.Status.State == "Preparing", "Preparation is visible in status.");
        string preparing = Path.Combine(paths.Sessions, coordinator.Status.SessionId!);
        time.Now += 100_000;
        h.Check((await Send(DaemonCommands.Toggle)).Ok && coordinator.Status.State == "Preparing",
            "Second toggle at 100 ms is ignored during preparation.");
        var cancelled = await Send(DaemonCommands.Stop);
        h.Check(cancelled.Ok && coordinator.Status.State == "Idle" && !Directory.Exists(preparing),
            "Stop during preparation cancels without leaving a session.");
        backend.PrepareGate.SetResult();
        await Task.Delay(200);
        h.Check(backend.Starts == startsBefore && coordinator.Status.State == "Idle", "A late consent never starts a recording.");
        backend.PrepareGate = null;
        await Send(DaemonCommands.Toggle);
        await PlatformChecks.WaitForAsync(() => coordinator.Status.State == "Recording");
        time.Now += 100_000;
        h.Check((await Send(DaemonCommands.Toggle)).Ok && coordinator.Status.State == "Recording", "Second toggle at 100 ms keeps recording.");
        time.Now += 500_000;
        h.Check((await Send(DaemonCommands.Toggle)).Ok && coordinator.Status.State == "Idle", "Toggle at 600 ms stops.");
        await PlatformChecks.WaitForAsync(() => coordinator.Status.Processing == 0, 60_000);

        backend.FailBeforeReady = true;
        await Send(DaemonCommands.Start);
        await PlatformChecks.WaitForAsync(() => coordinator.Status.State == "Idle");
        h.Check(coordinator.Status.LastError?.Contains("no frames") == true, "A recorder that never produces a frame fails the session.");
        backend.FailBeforeReady = false;

        string previous = coordinator.Status.LastBundle!;
        await Send(DaemonCommands.Start);
        await PlatformChecks.WaitForAsync(() => coordinator.Status.State == "Recording");
        await Task.Delay(800);
        backend.Last!.Exit();
        await PlatformChecks.WaitForAsync(() => coordinator.Status is { State: "Idle", Processing: 0 } && coordinator.Status.LastBundle != previous, 60_000);
        var crashed = await BundleStore.ReadAsync(coordinator.Status.LastBundle!);
        h.Check(crashed.Warnings.Any(w => w.Contains("exited before stop")), "An unrequested recorder exit is kept and disclosed.");

        // A readiness failure and a user stop race: exactly one teardown runs.
        backend.ReadyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await Send(DaemonCommands.Start);
        await PlatformChecks.WaitForAsync(() => coordinator.Status.State == "Recording");
        var racing = Send(DaemonCommands.Stop);
        backend.ReadyGate.SetException(new IOException("late failure"));
        await racing;
        await PlatformChecks.WaitForAsync(() => coordinator.Status is { State: "Idle", Processing: 0 }, 60_000);
        h.Check(backend.Last!.Stops == 1, $"Failure and stop share one terminal operation ({backend.Last.Stops} stops).");
        backend.ReadyGate = null;

        // Overlay events flow through the journal into the published bundle.
        h.Section = "overlay-integration";
        FakeOverlay? fake = null;
        var drawConfig = config with { Overlay = true };
        await using (var drawing = new SessionCoordinator(paths, drawConfig, backend,
            overlayFactory: () => fake = new FakeOverlay(), processor: Process))
        {
            Task<(bool Ok, string Message)> SendDraw(string command, string? argument = null) =>
                drawing.ExecuteAsync(new(Guid.NewGuid().ToString("N"), command, argument), CancellationToken.None);
            await SendDraw(DaemonCommands.Start);
            await PlatformChecks.WaitForAsync(() => drawing.Status.State == "Recording");
            var mock = fake ?? throw new CheckFailed("[overlay-integration] No overlay was created.");
            h.Check(mock.Shown && drawing.Status.Draw == OverlayInputMode.PassThrough,
                "The overlay is shown on the recorded output in pass-through.");
            h.Check((await SendDraw(DaemonCommands.Draw, "toggle")).Ok && mock.Mode == OverlayInputMode.Drawing, "Draw toggles on.");
            await SendDraw(DaemonCommands.Draw, "hold");
            h.Check(mock.Mode == OverlayInputMode.Holding, "Hold command starts pointer-only drawing.");
            await SendDraw(DaemonCommands.Draw, "toggle");
            h.Check(mock.Mode == OverlayInputMode.Drawing, "Draw toggle promotes Holding to explicit keyboard-grabbing Drawing.");
            h.Check((await SendDraw(DaemonCommands.Clear)).Ok && (await SendDraw(DaemonCommands.Undo)).Ok && mock.Clears == 1 && mock.Undos == 1,
                "Clear and undo reach the overlay in pass-through.");
            await SendDraw(DaemonCommands.Draw, "on");
            await Task.Delay(300);
            long at = mock.Clock!.ElapsedUs;
            mock.Emit(new StrokeCompleted(at, new Stroke("s1", "g0", "#ff3b30", 6, [new(at - 200_000, 50, 60), new(at - 100_000, 150, 60), new(at, 250, 70)])));
            await Task.Delay(400);
            long undo = mock.Clock.ElapsedUs;
            mock.Emit(new StrokesRemoved(undo, ["s1"], "undo"));
            mock.Emit(new MarkerRequested(undo + 1000, "Marked while drawing"));
            await SendDraw(DaemonCommands.Draw, "off");
            await Task.Delay(600);
            await SendDraw(DaemonCommands.Stop);
            await PlatformChecks.WaitForAsync(() => drawing.Status is { State: "Idle", Processing: 0, LastBundle: not null }, 60_000);
            var drawn = await BundleStore.ReadAsync(drawing.Status.LastBundle!);
            h.Check(mock.Hidden && !drawn.Warnings.Any(w => w.Contains("overlay closed")),
                "The overlay is hidden when the recording stops, without a false warning.");
            h.Check(drawn.Strokes.Single() is { Id: "s1", RemovedUs: { } removedAt } && removedAt == undo,
                "The stroke and its undo time are in the bundle.");
            h.Check(drawn.Events.Any(e => e.Kind == "draw.on") && drawn.Events.Any(e => e.Kind == "draw.off") &&
                    drawn.Events.Any(e => e.Kind == "ink.undo" && e.Source == "overlay"), "Mode changes and undo are on the timeline.");
            h.Check(drawn.Keyframes.Any(k => k.Reason.Contains("Annotation s1")) &&
                    drawn.Keyframes.Any(k => k.Reason.Contains("Marked while drawing")),
                "Annotation and overlay marker frames are selected.");
        }

        var showGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowOverlay = new FakeOverlay { ShowGate = showGate };
        await using (var slow = new SessionCoordinator(paths, drawConfig, backend, overlayFactory: () => slowOverlay,
            processor: Process, timeProvider: time))
        {
            await slow.ExecuteAsync(new("slow-start", DaemonCommands.Start), CancellationToken.None);
            await PlatformChecks.WaitForAsync(() => slowOverlay.Shown);
            time.Now += 100_000;
            var secondTap = slow.ExecuteAsync(new("slow-toggle", DaemonCommands.Toggle), CancellationToken.None);
            time.Now += 600_000;
            showGate.SetResult();
            h.Check((await secondTap).Message.StartsWith("Ignored:") && slow.Status.State == "Recording",
                "Double-tap guard uses request arrival even when overlay startup holds the lane.");
            await slow.ExecuteAsync(new("slow-stop", DaemonCommands.Stop), CancellationToken.None);
        }

        h.Section = "daemon";
        var server = new DaemonServer(paths, coordinator);
        using var stop = new CancellationTokenSource();
        using var lockFile = DaemonServer.AcquireLock(paths);
        h.Reject(() => DaemonServer.AcquireLock(paths).Dispose(), "A second daemon cannot take the lock.");
        var serving = server.RunAsync(stop.Token);
        await PlatformChecks.WaitForAsync(() => File.Exists(paths.Socket));
        h.Check((File.GetUnixFileMode(paths.Runtime) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.OtherExecute)) == 0,
            "Runtime directory is private.");
        var first = await RawAsync(paths, """{"id":"same-id","command":"toggle","argument":null}""");
        var retry = await RawAsync(paths, """{"id":"same-id","command":"toggle","argument":null}""");
        h.Check(first.Ok && retry.Message == first.Message, "A retried request id returns the original response.");
        await PlatformChecks.WaitForAsync(() => coordinator.Status.State == "Recording");
        h.Check(backend.Starts > 0 && coordinator.Status.State == "Recording", "The retried toggle did not stop the recording.");
        var bad = await RawAsync(paths, """{"id":"x","command":"format-disk"}""");
        h.Check(!bad.Ok && bad.Message.Contains("Unknown command"), "Unknown commands are rejected over the socket.");
        var status = await DaemonClient.SendAsync(paths, DaemonCommands.Status, null, false, CancellationToken.None);
        h.Check(status.Status.State == "Recording", "Client status round trip.");
        var stopped = await DaemonClient.SendAsync(paths, DaemonCommands.Stop, null, false, CancellationToken.None);
        h.Check(stopped.Ok, "Client stop: " + stopped.Message);
        await PlatformChecks.WaitForAsync(() => coordinator.Status is { State: "Idle", Processing: 0 }, 60_000);
        var shutdown = await DaemonClient.SendAsync(paths, DaemonCommands.Shutdown, null, false, CancellationToken.None);
        h.Check(shutdown.Ok && shutdown.Message == "Daemon stopped.", "Shutdown delivers its own reply.");
        await serving.WaitAsync(TimeSpan.FromSeconds(15));
        h.Check(!File.Exists(paths.Socket), "The socket is removed on shutdown.");
        await h.RejectAsync<DaemonUnavailableException>(() => DaemonClient.SendAsync(paths, DaemonCommands.Status, null, false,
            CancellationToken.None), "Clients report a missing daemon.");
    }

    private static async Task<DaemonResponse> RawAsync(WayScrawlPaths paths, string json)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(paths.Socket));
        await using var stream = new NetworkStream(socket);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(json + "\n"));
        string line = await DaemonServer.ReadLineAsync(stream, 1 << 20, CancellationToken.None) ?? "";
        return JsonSerializer.Deserialize<DaemonResponse>(line, WayScrawlJson.Line)!;
    }
}
