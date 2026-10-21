using System.Globalization;
using WayScrawl.Core;
using WayScrawl.Platform.Hyprland;

namespace WayScrawl.Platform.Media;

// Supervised gpu-screen-recorder capturing one monitor through KMS.
// -write-first-frame-ts gives CLOCK_MONOTONIC for the first captured frame, which
// maps video presentation time onto the session clock without guessing launch latency.
public sealed class GpuScreenRecorderBackend(string tool = "gpu-screen-recorder") : ICaptureBackend
{
    public string Name => "gpu-screen-recorder";

    public async Task<Capability[]> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(tool, ["--list-capture-options"], cancellationToken);
            return [new("videoCapture", result.ExitCode == 0 ? Availability.Available : Availability.Unknown,
                "gpu-screen-recorder capture options: " + string.Join(", ",
                    result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))];
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return [new("videoCapture", Availability.Unsupported, tool + " is not installed.")];
        }
    }

    public async Task<IPreparedCapture> PrepareAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        var monitor = HyprlandIpc.Select(await HyprlandIpc.MonitorsAsync(cancellationToken), request.Output);
        if (monitor.Transform != 0)
            throw new NotSupportedException($"{monitor.Name} is rotated; only upright outputs are supported.");
        var source = new CaptureSource(Name, monitor.ToGeometry("g0"), CompositionMode.Unknown,
            $"{monitor.Name} {monitor.Width}x{monitor.Height} at scale {monitor.Scale.ToString(CultureInfo.InvariantCulture)} ({monitor.Description})");
        return new Prepared(tool, request, monitor.Name, source);
    }

    private sealed class Prepared(string tool, CaptureRequest request, string output, CaptureSource source) : IPreparedCapture
    {
        public CaptureSource Source => source;

        public Task<IRecording> StartAsync(ISessionClock clock, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string video = Path.Combine(request.WorkDirectory, "recording.mkv");
            var arguments = new List<string>
            {
                "-w", output, "-f", request.FramesPerSecond.ToString(CultureInfo.InvariantCulture),
                "-c", "mkv", "-k", "h264", "-q", "high", "-cursor", "yes",
                "-write-first-frame-ts", "yes", "-v", "no", "-o", video
            };
            if (request.Microphone is { Length: > 0 } microphone) arguments.InsertRange(0, ["-a", microphone, "-ac", "opus"]);
            var process = SupervisedProcess.Start(tool, arguments, request.WorkDirectory);
            return Task.FromResult<IRecording>(new Recording(process, clock, video, request.FramesPerSecond,
                request.Microphone is { Length: > 0 }));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Recording : IRecording
    {
        private readonly SupervisedProcess process;
        private readonly ISessionClock clock;
        private readonly string video;
        private readonly int fps;
        private readonly bool audio;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource watch = new();
        private Task<RecordingResult>? stopping;

        public Recording(SupervisedProcess process, ISessionClock clock, string video, int fps, bool audio)
        {
            this.process = process;
            this.clock = clock;
            this.video = video;
            this.fps = fps;
            this.audio = audio;
            Completion = CompleteAsync();
            _ = WatchReadyAsync();
        }

        public Task Ready => ready.Task;
        public Task<RecordingResult> Completion { get; }

        public Task<RecordingResult> StopAsync(CancellationToken cancellationToken)
        {
            // Repeated stops join the same operation.
            lock (watch) stopping ??= StopOnceAsync();
            return stopping.WaitAsync(cancellationToken);
        }

        private async Task<RecordingResult> StopOnceAsync()
        {
            await process.StopAsync(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(3));
            return await Completion;
        }

        private async Task WatchReadyAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            try
            {
                while (!File.Exists(video + ".ts"))
                {
                    if (process.HasExited)
                        throw new IOException("gpu-screen-recorder exited before the first frame: " +
                            ProcessRunner.LastLine(string.Join('\n', process.RecentOutput())));
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("gpu-screen-recorder produced no frame within 15 seconds.");
                    await Task.Delay(20, watch.Token);
                }
                ready.TrySetResult();
            }
            catch (OperationCanceledException) { ready.TrySetCanceled(); }
            catch (Exception error) { ready.TrySetException(error); }
        }

        private async Task<RecordingResult> CompleteAsync()
        {
            int code = await process.Exited;
            await watch.CancelAsync();
            var diagnostics = process.RecentOutput().TakeLast(40).Append("exit code " + code).ToList();
            long? first = ReadFirstFrame(video + ".ts", diagnostics);
            long frame = 1_000_000 / Math.Max(1, fps);
            // Offset maps stream PTS zero to session time; the processor subtracts the first PTS.
            var videoClock = first is { } monotonic
                ? new ClockMap(monotonic - clock.OriginMonotonicUs, ClockQuality.Measured, "CLOCK_MONOTONIC",
                    "gpu-screen-recorder -write-first-frame-ts; offset applies at the first video PTS", frame)
                : new ClockMap(0, ClockQuality.Unavailable, "CLOCK_MONOTONIC", "No first-frame timestamp was written.", 0);
            ClockMap? audioClock = audio
                ? videoClock with
                {
                    Quality = videoClock.Quality == ClockQuality.Measured ? ClockQuality.Estimated : videoClock.Quality,
                    Method = "Assumes the recorder's shared container timeline; audio onset is not independently measured.",
                    UncertaintyUs = frame + 50_000
                }
                : null;
            bool finalized = code == 0 && !process.Escalated && File.Exists(video);
            return new RecordingResult(video, finalized, videoClock, audioClock, diagnostics.ToArray());
        }

        private static long? ReadFirstFrame(string path, List<string> diagnostics)
        {
            try
            {
                // Header line, then "<monotonic_microsec> <realtime_microsec>".
                string[] fields = File.ReadAllText(path).Split((char[])[' ', '\t', '\n', '\r'],
                    StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 4 && long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out long value))
                    return value;
                diagnostics.Add("Unrecognized first-frame timestamp file.");
            }
            catch (IOException error) { diagnostics.Add("First-frame timestamp unavailable: " + error.Message); }
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) await StopAsync(CancellationToken.None);
            await process.DisposeAsync();
            watch.Dispose();
        }
    }
}
