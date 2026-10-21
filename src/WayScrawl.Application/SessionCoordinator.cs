using System.Collections.Concurrent;
using WayScrawl.Core;
using WayScrawl.Platform;

namespace WayScrawl.Application;

// Owns at most one live session. State changes happen on a short serialized lane; slow work
// (consent, recorder finalization, processing) runs outside it and re-enters to commit, so
// status, repeated stop and cancel never wait behind a recorder or a model.
public sealed class SessionCoordinator(
    WayScrawlPaths paths, WayScrawlConfig config, ICaptureBackend backend,
    Func<IOverlayHost>? overlayFactory = null, Func<CaptureSource, IInteractionSource>? telemetryFactory = null,
    Action<string>? log = null, Func<string, Task<(int Code, string Output)>>? processor = null,
    TimeProvider? timeProvider = null, int configProblems = 0, Func<IHoldControl>? holdFactory = null) : IAsyncDisposable
{
    // Processing runs in a worker process by default; checks substitute an in-process runner.
    private readonly Func<string, Task<(int Code, string Output)>> process = processor ?? RunWorkerAsync;

    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim lane = new(1, 1);
    private Session? current;
    private int processing;
    private string? lastBundle, lastError;
    private readonly List<Task> workers = [];

    public event Action<string, string>? Notified;

    public DaemonStatus Status
    {
        get
        {
            var session = current;
            return new(session?.Machine.State.ToString() ?? "Idle", session?.Id,
                session?.Clock?.ElapsedUs ?? 0, session?.Overlay?.Mode, lastBundle, lastError,
                Volatile.Read(ref processing), backend.Name, configProblems);
        }
    }

    public async Task<(bool Ok, string Message)> ExecuteAsync(DaemonRequest request, CancellationToken cancellationToken)
    {
        long receivedAt = time.GetTimestamp();
        switch (request.Command)
        {
            case DaemonCommands.Start: return await StartAsync(cancellationToken);
            case DaemonCommands.Stop: return await StopAsync(discard: false, "Stopped by request.");
            case DaemonCommands.Cancel: return await StopAsync(discard: true, "Cancelled by request.");
            case DaemonCommands.Toggle:
                return await ToggleAsync(receivedAt, cancellationToken);
            case DaemonCommands.Draw: return await DrawAsync(request.Argument ?? "toggle", cancellationToken);
            case DaemonCommands.Clear: return await RemoveInkAsync(false, cancellationToken);
            case DaemonCommands.Undo: return await RemoveInkAsync(true, cancellationToken);
            case DaemonCommands.Marker: return await MarkerAsync(request.Argument ?? "", cancellationToken);
            case DaemonCommands.Status: return (true, Status.State);
            default: return (false, "Unsupported command.");
        }
    }

    private async Task<(bool, string)> ToggleAsync(long receivedAt, CancellationToken cancellationToken)
    {
        Session? session;
        await lane.WaitAsync(cancellationToken);
        try
        {
            session = current;
            if (session is null) return BeginSession(receivedAt);
            if (time.GetElapsedTime(session.AcceptedAt, receivedAt) < TimeSpan.FromMilliseconds(400))
                return (true, "Ignored: pressed again within 0.4 s of starting.");
        }
        finally { lane.Release(); }
        return await EndAsync(session, discard: false, "Stopped by toggle.");
    }

    private async Task<(bool, string)> RemoveInkAsync(bool undo, CancellationToken cancellationToken)
    {
        await lane.WaitAsync(cancellationToken);
        try
        {
            if (current is not { Machine.State: SessionState.Recording } session) return (false, "Not recording.");
            if (session.Overlay is not { } overlay) return (false, "The overlay is not available in this session.");
            bool empty = overlay.InkCount == 0;
            if (undo) await overlay.UndoAsync(cancellationToken);
            else await overlay.ClearAsync(cancellationToken);
            return (true, empty ? (undo ? "Nothing to undo." : "Nothing to clear.") : (undo ? "Undone." : "Cleared."));
        }
        finally { lane.Release(); }
    }

    private async Task<(bool, string)> StartAsync(CancellationToken cancellationToken)
    {
        await lane.WaitAsync(cancellationToken);
        try
        {
            return BeginSession(time.GetTimestamp());
        }
        finally { lane.Release(); }
    }

    // Called under the lane so concurrent idle toggles share one admission decision.
    private (bool, string) BeginSession(long acceptedAt)
    {
        if (current is { } busy) return (false, $"Session {busy.Id} is {busy.Machine.State}.");
        string id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        string directory = Path.Combine(paths.Sessions, id);
        WayScrawlPaths.EnsurePrivateDirectory(paths.Sessions);
        WayScrawlPaths.EnsurePrivateDirectory(directory);
        var session = new Session(id, directory, new JournalWriter(Path.Combine(directory, SessionRecord.JournalName)), acceptedAt);
        session.Machine.MoveTo(SessionState.Preparing);
        current = session;
        session.Preparing = Task.Run(() => PrepareAsync(session), CancellationToken.None);
        Track(session.Preparing);
        return (true, "Preparing " + id);
    }

    private async Task PrepareAsync(Session session)
    {
        IPreparedCapture? prepared = null;
        IRecording? recording = null;
        try
        {
            prepared = await backend.PrepareAsync(new CaptureRequest(config.Output, config.Microphone,
                session.Directory, config.FramesPerSecond), session.Cancel.Token);
            var clock = new SessionClock();
            // Starting can wait on the portal (OpenPipeWireRemote): it runs outside the lane, bounded
            // and cancellable, so stop, cancel and status stay responsive.
            using (var start = CancellationTokenSource.CreateLinkedTokenSource(session.Cancel.Token))
            {
                start.CancelAfter(TimeSpan.FromSeconds(20));
                recording = await prepared.StartAsync(clock, start.Token);
            }
            await lane.WaitAsync();
            try
            {
                if (current != session || session.Machine.State != SessionState.Preparing) return;
                // The session now owns both (a portal session must outlive Start).
                session.Prepared = prepared;
                session.Recording = recording;
                prepared = null;
                recording = null;
                session.Source = session.Prepared.Source;
                session.Clock = clock;
                session.Machine.MoveTo(SessionState.Recording);
                session.Journal.Event(new(0, "recording.start", session.Source.Detail, "coordinator", "exact",
                    session.Journal.NextSequence()));
                WriteRecord(session, "recording");
                await AttachOverlayAsync(session);
                AttachTelemetry(session);
                AttachHold(session);
                Track(MonitorAsync(session));
            }
            finally { lane.Release(); }
            log?.Invoke($"Recording {session.Id} with {backend.Name}: {session.Source?.Detail}");
            Notify("Recording", (config.Overlay ? "Draw toggle draws on screen. " : "") + "Toggle again to stop.");
        }
        catch (OperationCanceledException) when (session.Cancel.IsCancellationRequested) { }
        catch (Exception error)
        {
            await FailAsync(session, "Could not start recording: " + error.Message);
        }
        finally
        {
            // Anything not handed to the session (cancelled, failed, superseded) is released here.
            if (recording is not null)
            {
                try { await recording.StopAsync(CancellationToken.None); } catch (Exception error) { log?.Invoke(error.Message); }
                await recording.DisposeAsync();
            }
            if (prepared is not null) await prepared.DisposeAsync();
        }
    }

    private async Task AttachOverlayAsync(Session session)
    {
        if (!config.Overlay || overlayFactory is null || session.Source is null || session.Clock is null) return;
        if (!session.Source.Geometry.Source.StartsWith("monitor:", StringComparison.Ordinal))
        {
            // Ink is drawn on an output; a window share has no known place in the output layout.
            session.Capabilities.Enqueue(new("overlay", Availability.Unsupported,
                "Drawing is available when a whole monitor is recorded, not a single window."));
            return;
        }
        IOverlayHost? overlay = null;
        try
        {
            overlay = overlayFactory();
            string output = session.Source.Geometry.Source["monitor:".Length..];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await overlay.ShowAsync(session.Source.Geometry, output, session.Clock, timeout.Token);
            session.Overlay = overlay;
            session.OverlayPump = Task.Run(() => PumpOverlayAsync(session, overlay));
        }
        catch (Exception error)
        {
            if (overlay is not null)
                try { await overlay.DisposeAsync(); } catch (Exception cleanup) { log?.Invoke("Overlay cleanup: " + cleanup.Message); }
            session.Warnings.Enqueue("The drawing overlay is unavailable: " + error.Message);
            session.Capabilities.Enqueue(new("overlay", Availability.Unknown, error.Message));
            log?.Invoke("Overlay failed: " + error);
        }
    }

    private void AttachTelemetry(Session session)
    {
        if (config.Telemetry == "off" || telemetryFactory is null || session.Clock is null || session.Source is null) return;
        try
        {
            var source = telemetryFactory(session.Source);
            session.Telemetry = source;
            foreach (var capability in source.Describe()) session.Capabilities.Enqueue(capability);
            session.TelemetryPump = Task.Run(() => PumpTelemetryAsync(session, source));
        }
        catch (Exception error)
        {
            session.Warnings.Enqueue("Interaction telemetry is unavailable: " + error.Message);
        }
    }

    private void AttachHold(Session session)
    {
        if (config.DrawTrigger != "hold" || holdFactory is null || session.Overlay is null) return;
        try
        {
            var control = holdFactory();
            session.HoldPump = Task.Run(() => PumpHoldAsync(session, control));
        }
        catch (Exception error) { session.Warnings.Enqueue("Hold drawing is unavailable: " + error.Message); }
    }

    private async Task PumpHoldAsync(Session session, IHoldControl control)
    {
        try
        {
            await foreach (var item in control.ReadAsync(session.Clock!, session.HoldStop.Token))
            {
                await lane.WaitAsync(session.HoldStop.Token);
                try
                {
                    if (current != session || session.Stopping is not null || session.Overlay is not { } overlay) continue;
                    if (item.Problem is { } problem) session.Warnings.Enqueue(problem);
                    // The explicit draw hotkey owns Drawing, including its exclusive keyboard.
                    // A modifier used by that hotkey must never downgrade it to pointer-only input.
                    if (overlay.Mode == OverlayInputMode.Drawing) continue;
                    await overlay.SetInputModeAsync(item.Pressed ? OverlayInputMode.Holding : OverlayInputMode.PassThrough,
                        session.HoldStop.Token);
                }
                finally { lane.Release(); }
            }
        }
        catch (OperationCanceledException) when (session.HoldStop.IsCancellationRequested) { }
        catch (Exception error) { session.Warnings.Enqueue("Hold drawing stopped: " + error.Message); }
        finally
        {
            await lane.WaitAsync();
            try
            {
                if (session.Overlay is { Mode: OverlayInputMode.Holding } overlay)
                    try { await overlay.SetInputModeAsync(OverlayInputMode.PassThrough, CancellationToken.None); }
                    catch (InvalidOperationException) { }
            }
            finally { lane.Release(); }
        }
    }

    private async Task PumpOverlayAsync(Session session, IOverlayHost overlay)
    {
        bool drawing = false;
        long? holdingAt = null;
        void InkStarted()
        {
            if (holdingAt is not { } at || drawing) return;
            session.Journal.Event(new(at, "draw.on", "Held drawing", "overlay", "exact", session.Journal.NextSequence()));
            drawing = true;
        }
        try
        {
            await foreach (var item in overlay.Events.ReadAllAsync())
            {
                switch (item)
                {
                    case StrokeCompleted done:
                        InkStarted();
                        var stroke = InkMath.Simplify(done.Stroke);
                        session.Journal.Stroke(stroke);
                        session.Journal.Event(new(stroke.Points[0].AtUs, "stroke", stroke.Id, "overlay", "exact", session.Journal.NextSequence()));
                        break;
                    case PinPlaced placed:
                        InkStarted();
                        session.Journal.Pin(placed.Pin);
                        session.Journal.Event(new(placed.AtUs, "pin", placed.Pin.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            "overlay", "exact", session.Journal.NextSequence()));
                        break;
                    case StrokesRemoved removed:
                        session.Journal.Removed(removed.AtUs, removed.StrokeIds, removed.Reason);
                        session.Journal.Event(new(removed.AtUs, "ink." + removed.Reason,
                            string.Join(", ", removed.StrokeIds), "overlay", "exact", session.Journal.NextSequence()));
                        break;
                    case ModeChanged mode:
                        if (mode.Mode == OverlayInputMode.Holding)
                        {
                            if (drawing) session.Journal.Event(new(mode.AtUs, "draw.off", mode.Reason, "overlay", "exact", session.Journal.NextSequence()));
                            holdingAt = mode.AtUs;
                            drawing = false;
                        }
                        else
                        {
                            bool next = mode.Mode == OverlayInputMode.Drawing;
                            if (drawing != next)
                                session.Journal.Event(new(mode.AtUs, next ? "draw.on" : "draw.off", mode.Reason, "overlay", "exact", session.Journal.NextSequence()));
                            drawing = next;
                            holdingAt = null;
                        }
                        break;
                    case MarkerRequested marker:
                        session.Journal.Event(new(marker.AtUs, "marker", marker.Detail, "overlay", "exact", session.Journal.NextSequence()));
                        break;
                    case OverlayClosed when session.Stopping is not null:
                        break; // The coordinator hid it while finalizing.
                    case OverlayClosed closed:
                        session.Warnings.Enqueue("The overlay closed during recording: " + closed.Reason);
                        session.Journal.Event(new(closed.AtUs, "overlay.closed", closed.Reason, "overlay", "exact", session.Journal.NextSequence()));
                        break;
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            session.Warnings.Enqueue("Overlay events stopped: " + error.Message);
        }
    }

    private async Task PumpTelemetryAsync(Session session, IInteractionSource source)
    {
        try
        {
            await foreach (var sample in source.ReadAsync(session.Clock!, session.TelemetryStop.Token))
            {
                session.Journal.Event(new(sample.AtUs, sample.Kind, sample.Payload, sample.Source, sample.Fidelity, sample.Sequence));
            }
        }
        catch (OperationCanceledException) when (session.TelemetryStop.IsCancellationRequested) { }
        catch (Exception error)
        {
            session.Warnings.Enqueue("Interaction telemetry stopped: " + error.Message);
        }
        finally
        {
            session.Journal.Source(source.Statistics);
        }
    }

    // Fails the session if no frame arrives; stops it if the recorder exits unasked.
    private async Task MonitorAsync(Session session)
    {
        try
        {
            await session.Recording!.Ready;
            log?.Invoke($"{session.Id}: first frame captured.");
        }
        catch (Exception error)
        {
            await FailAsync(session, "The recorder failed: " + error.Message);
            return;
        }
        await session.Recording.Completion;
        if (session.Stopping is null && current == session)
        {
            session.Warnings.Enqueue("The recorder exited before stop was requested.");
            await EndAsync(session, discard: false, "Recorder exited.");
        }
    }

    private Task<(bool, string)> StopAsync(bool discard, string reason) => EndAsync(null, discard, reason);

    private Task FailAsync(Session session, string message) => EndAsync(session, discard: false, message, failure: true);

    // Stop, cancel and failure all end a session through exactly one terminal operation,
    // claimed under the lane; later callers join it instead of tearing down twice.
    private async Task<(bool, string)> EndAsync(Session? expected, bool discard, string reason, bool failure = false)
    {
        Task<(bool, string)> operation;
        await lane.WaitAsync();
        try
        {
            var session = current;
            if (session is null || expected is not null && session != expected)
                return (false, "Not recording.");
            if (session.Stopping is null)
            {
                if (session.Machine.State == SessionState.Preparing)
                {
                    await session.Cancel.CancelAsync();
                    session.Machine.MoveTo(failure ? SessionState.Failed : SessionState.Cancelled);
                    current = null;
                    Cleanup(session, deleteDirectory: !failure);
                    if (failure)
                    {
                        lastError = reason;
                        Notify("Recording failed", reason);
                    }
                    return (!failure, failure ? reason : "Cancelled before recording started.");
                }
                if (session.Machine.State != SessionState.Recording)
                    return (false, $"Session is {session.Machine.State}.");
                session.Machine.MoveTo(SessionState.Finalizing);
                session.Stopping = Task.Run(() => FinalizeAsync(session, discard, reason, failure));
            }
            operation = session.Stopping;
        }
        finally { lane.Release(); }
        return await operation;
    }

    // Persistence problems never skip stopping the recorder or reaching a terminal state.
    private async Task<(bool, string)> FinalizeAsync(Session session, bool discard, string reason, bool failure)
    {
        long stopUs = session.Clock?.ElapsedUs ?? 0;
        if (failure)
        {
            lastError = reason;
            log?.Invoke($"{session.Id}: {reason}");
            session.Warnings.Enqueue(reason);
        }
        try
        {
            session.Journal.Event(new(stopUs, discard ? "recording.cancel" : failure ? "recording.fail" : "recording.stop",
                reason, "coordinator", "exact", session.Journal.NextSequence()));
        }
        catch (Exception error) { session.Warnings.Enqueue("The journal could not record the stop: " + error.Message); }
        await DetachAsync(session);
        RecordingResult? result = null;
        try
        {
            if (session.Recording is { } recording)
            {
                result = await recording.StopAsync(CancellationToken.None);
                await recording.DisposeAsync();
            }
        }
        catch (Exception error)
        {
            session.Warnings.Enqueue("Stopping the recorder failed: " + error.Message);
            log?.Invoke(error.ToString());
        }
        try { if (session.Prepared is { } prepared) await prepared.DisposeAsync(); }
        catch (Exception error) { log?.Invoke("Capture release: " + error.Message); }

        bool process = false;
        await lane.WaitAsync();
        try
        {
            session.DurationUs = stopUs;
            session.Result = result;
            if (discard)
            {
                session.Machine.MoveTo(SessionState.Cancelled);
                Cleanup(session, deleteDirectory: true);
            }
            else
            {
                bool usable = !failure && result is not null;
                try
                {
                    WriteRecord(session, usable ? "recorded" : "failed");
                }
                catch (Exception error)
                {
                    usable = false;
                    lastError = "The session record could not be written: " + error.Message;
                }
                session.Machine.MoveTo(usable ? SessionState.Ready : SessionState.Failed);
                Cleanup(session, deleteDirectory: false);
                process = usable;
            }
            current = null;
        }
        finally { lane.Release(); }

        if (discard)
        {
            Notify("Recording discarded", reason);
            return (true, "Discarded.");
        }
        if (!process)
        {
            string message = failure ? reason : lastError ?? "The recording could not be finalized.";
            Notify("Recording failed", message + $" Raw files are kept in {session.Directory}.");
            return (false, message);
        }
        if (result is { Finalized: false }) Notify("Recording incomplete", "The recorder did not finalize normally; processing anyway.");
        StartProcessing(session.Directory);
        return (true, $"Stopped {session.Id} after {stopUs / 1_000_000d:0.0} s; processing.");
    }

    private async Task DetachAsync(Session session)
    {
        await session.HoldStop.CancelAsync();
        if (session.HoldPump is { } hold)
            try { await hold.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { session.Warnings.Enqueue("Hold control teardown timed out."); }
        if (session.Overlay is { } overlay)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await overlay.HideAsync(timeout.Token);
                await overlay.DisposeAsync();
                if (session.OverlayPump is { } pump) await pump.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception error) { log?.Invoke("Overlay teardown: " + error.Message); }
            session.Overlay = null;
        }
        if (session.Telemetry is not null)
        {
            await session.TelemetryStop.CancelAsync();
            if (session.TelemetryPump is { } pump)
                try { await pump.WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
        }
    }

    private async Task<(bool, string)> DrawAsync(string argument, CancellationToken cancellationToken)
    {
        await lane.WaitAsync(cancellationToken);
        try
        {
            if (current is not { Machine.State: SessionState.Recording } session) return (false, "Not recording.");
            if (session.Overlay is not { } overlay) return (false, "The overlay is not available in this session.");
            var mode = argument switch
            {
                "on" => OverlayInputMode.Drawing,
                "off" or "release" => OverlayInputMode.PassThrough,
                "hold" => OverlayInputMode.Holding,
                _ => overlay.Mode == OverlayInputMode.Drawing ? OverlayInputMode.PassThrough : OverlayInputMode.Drawing
            };
            await overlay.SetInputModeAsync(mode, cancellationToken);
            return (true, mode switch { OverlayInputMode.Drawing => "Drawing: drag to draw, Esc to stop.",
                OverlayInputMode.Holding => "Holding: move the pointer before the first click; release the key to stop.", _ => "Pass-through." });
        }
        finally { lane.Release(); }
    }

    private async Task<(bool, string)> MarkerAsync(string detail, CancellationToken cancellationToken)
    {
        await lane.WaitAsync(cancellationToken);
        try
        {
            if (current is not { Machine.State: SessionState.Recording, Clock: { } clock } session) return (false, "Not recording.");
            session.Journal.Event(new(clock.ElapsedUs, "marker", detail, "coordinator", "exact", session.Journal.NextSequence()));
            return (true, "Marker added.");
        }
        finally { lane.Release(); }
    }

    private void StartProcessing(string directory)
    {
        Interlocked.Increment(ref processing);
        Track(Task.Run(async () =>
        {
            try
            {
                var (code, output) = await process(directory);
                var record = EvidenceBuilder.ReadRecord(directory);
                // The worker prints the bundle path last; it stands even if the record update failed.
                string? printed = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .LastOrDefault(line => line.EndsWith(".wayscrawl", StringComparison.Ordinal) && Directory.Exists(line));
                if (code == 0 && (record.Bundle ?? printed) is { } bundle)
                {
                    lastBundle = bundle;
                    log?.Invoke("Published " + bundle);
                    if (config.ClipboardMode != "off")
                        try { await new ClipboardSink(config.ClipboardMode).SendAsync(bundle, "clipboard", CancellationToken.None); }
                        catch (Exception error) { log?.Invoke("Clipboard: " + error.Message); }
                    Notify("Evidence ready", (config.ClipboardMode != "off" ? "Evidence copied to clipboard. " : "") + bundle);
                }
                else
                {
                    lastError = "Processing failed: " + ProcessRunner.LastLine(output);
                    log?.Invoke(lastError + "\n" + output);
                    Notify("Processing failed", $"{lastError} Retry: wayscrawl process {directory}");
                }
            }
            catch (Exception error)
            {
                lastError = "Processing failed: " + error.Message;
                log?.Invoke(error.ToString());
            }
            finally { Interlocked.Decrement(ref processing); }
        }));
    }

    private static async Task<(int, string)> RunWorkerAsync(string directory)
    {
        var (file, prefix) = SelfCommand.Resolve();
        await using var worker = SupervisedProcess.Start(file, [.. prefix, "process", directory]);
        int code = await worker.Exited;
        return (code, string.Join('\n', worker.RecentOutput()));
    }

    private void WriteRecord(Session session, string state)
    {
        if (session.Source is null) return;
        EvidenceBuilder.WriteRecord(session.Directory, new SessionRecord(session.Id, session.Created, state, backend.Name,
            session.Source, session.Clock?.OriginMonotonicUs ?? 0, "recording.mkv", config.Microphone, session.DurationUs,
            session.Result, session.Capabilities.ToArray(), session.Warnings.ToArray())
            { WindowTitles = config.WindowTitles, InkFadeSeconds = config.InkFadeSeconds });
    }

    private static void Cleanup(Session session, bool deleteDirectory)
    {
        session.Journal.Dispose();
        session.Cancel.Dispose();
        session.HoldStop.Dispose();
        session.TelemetryStop.Dispose();
        if (deleteDirectory && Directory.Exists(session.Directory)) Directory.Delete(session.Directory, recursive: true);
    }

    private void Notify(string title, string body)
    {
        Notified?.Invoke(title, body);
        if (config.Notifications) _ = Desktop.NotifyAsync(title, body);
    }

    private void Track(Task task)
    {
        lock (workers)
        {
            workers.RemoveAll(t => t.IsCompleted);
            workers.Add(task);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (current is not null) await StopAsync(discard: false, "Daemon shutting down.");
        Task[] pending;
        lock (workers) pending = workers.ToArray();
        await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30)).ContinueWith(_ => { });
    }

    private sealed class Session(string id, string directory, JournalWriter journal, long acceptedAt)
    {
        public long AcceptedAt { get; } = acceptedAt;
        public string Id { get; } = id;
        public string Directory { get; } = directory;
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public SessionMachine Machine { get; } = new();
        public JournalWriter Journal { get; } = journal;
        public CancellationTokenSource Cancel { get; } = new();
        public CancellationTokenSource HoldStop { get; } = new();
        public Task? HoldPump { get; set; }
        public CancellationTokenSource TelemetryStop { get; } = new();
        public ConcurrentQueue<string> Warnings { get; } = new();
        public ConcurrentQueue<Capability> Capabilities { get; } = new();
        public IPreparedCapture? Prepared { get; set; }
        public Task? Preparing { get; set; }
        public CaptureSource? Source { get; set; }
        public SessionClock? Clock { get; set; }
        public IRecording? Recording { get; set; }
        public IOverlayHost? Overlay { get; set; }
        public Task? OverlayPump { get; set; }
        public IInteractionSource? Telemetry { get; set; }
        public Task? TelemetryPump { get; set; }
        public Task<(bool, string)>? Stopping { get; set; }
        public long? DurationUs { get; set; }
        public RecordingResult? Result { get; set; }
    }
}
