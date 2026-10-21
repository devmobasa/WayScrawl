using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using WayScrawl.Core;
using WayScrawl.Platform;
using WayScrawl.Platform.Hyprland;

namespace WayScrawl.Portal;

// Desktop-mediated capture: the ScreenCast portal chooses the source and grants a restricted
// PipeWire remote; gst-launch records from that remote only. Monitors map onto Hyprland's
// layout so the overlay and click positions line up; windows record without the overlay.
public sealed class PortalCaptureBackend(string tokenFile, string? busAddress = null, HyprlandInstance? instance = null)
    : ICaptureBackend
{
    private const uint Monitor = 1, Window = 2;

    public string Name => "portal";

    public async Task<Capability[]> ProbeAsync(CancellationToken cancellationToken)
    {
        var capabilities = new List<Capability>();
        try
        {
            var info = await ScreenCastSession.ProbeAsync(busAddress, cancellationToken);
            capabilities.Add(new("portalScreenCast", Availability.Available,
                $"ScreenCast v{info.Version}, source types {info.SourceTypes}, cursor modes {info.CursorModes}."));
        }
        catch (Exception error) when (error is Tmds.DBus.Protocol.DBusExceptionBase or PortalException or IOException
                                          or TimeoutException)
        {
            capabilities.Add(new("portalScreenCast", Availability.Unsupported, error.Message));
        }
        string? encoder = await GstEncoderAsync(cancellationToken);
        capabilities.Add(new("gstreamer", encoder is null ? Availability.Unsupported : Availability.Available,
            encoder is null ? "gst-launch-1.0 with pipewiresrc and an H.264 encoder is required." : "Encoder " + encoder + "."));
        return capabilities.ToArray();
    }

    public async Task<IPreparedCapture> PrepareAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        string encoder = await GstEncoderAsync(cancellationToken)
            ?? throw new NotSupportedException("gst-launch-1.0 with pipewiresrc and an H.264 encoder is required.");
        string? restore = File.Exists(tokenFile) ? (await File.ReadAllTextAsync(tokenFile, cancellationToken)).Trim() : null;
        var session = await ScreenCastSession.StartAsync(busAddress, Monitor | Window, restore, cancellationToken);
        try
        {
            // Restore tokens are single use; keep the replacement for the next session.
            if (session.RestoreToken is { Length: > 0 } next)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
                await File.WriteAllTextAsync(tokenFile, next, cancellationToken);
                File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var stream = session.Streams[0];
            var source = await DescribeAsync(stream, cancellationToken);
            return new Prepared(session, stream, source, request, encoder);
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task<CaptureSource> DescribeAsync(PortalStream stream, CancellationToken cancellationToken)
    {
        if (stream.SourceType == Monitor && stream.X is { } x && stream.Y is { } y)
        {
            var monitors = await HyprlandMonitorsAsync(cancellationToken);
            if (monitors.FirstOrDefault(m => m.X == x && m.Y == y && !m.Disabled) is { } monitor)
                return new(Name, monitor.ToGeometry("g0"), CompositionMode.Unknown,
                    $"portal monitor {monitor.Name} {monitor.Width}x{monitor.Height} (node {stream.NodeId})");
        }
        // A window (or an unplaceable monitor) has no known place in the global layout.
        int w = stream.Width ?? 1, h = stream.Height ?? 1;
        string kind = stream.SourceType == Window ? "window" : "source";
        return new(Name, new Geometry("g0", 0, 0, w, h, w, h, $"{kind}:{stream.Id ?? stream.NodeId.ToString(CultureInfo.InvariantCulture)}"),
            CompositionMode.Unknown, $"portal {kind} {w}x{h} (node {stream.NodeId}); overlay unavailable for this source");
    }

    private async Task<HyprMonitor[]> HyprlandMonitorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunCheckedAsync("hyprctl", ["-j", "monitors"], cancellationToken,
                (instance ?? HyprlandInstance.Current)?.Environment);
            return HyprlandIpc.ParseMonitors(result.Text);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { return []; }
    }

    private static string? encoderCache;

    public static async Task<string?> GstEncoderAsync(CancellationToken cancellationToken)
    {
        if (encoderCache is not null) return encoderCache.Length == 0 ? null : encoderCache;
        async Task<bool> Exists(string element)
        {
            try { return (await ProcessRunner.RunAsync("gst-inspect-1.0", ["--exists", element], cancellationToken)).ExitCode == 0; }
            catch (System.ComponentModel.Win32Exception) { return false; }
        }
        string? found = null;
        if (await Exists("pipewiresrc") && await Exists("matroskamux") && await Exists("h264parse"))
            foreach (string candidate in new[] { "nvh264enc", "x264enc", "openh264enc" })
                if (await Exists(candidate)) { found = candidate; break; }
        encoderCache = found ?? "";
        return found;
    }

    public static IReadOnlyList<string> Pipeline(string encoder, PortalStream stream, string video, string? microphone, int fps)
    {
        string[] encode = encoder switch
        {
            "x264enc" => ["x264enc", "speed-preset=veryfast", "tune=zerolatency"],
            _ => [encoder]
        };
        // NVENC takes NV12, x264 and OpenH264 I420. 4:2:0 needs even sizes, so the stepped range
        // lets videoscale round an odd window (641x481) to the nearest even size.
        string format = encoder == "nvh264enc" ? "NV12" : "I420";
        // v6 portals name the stream by serial, which is never reused; node ids can be.
        string target = stream.Serial is { } serial
            ? "target-object=" + serial.ToString(CultureInfo.InvariantCulture)
            : "path=" + stream.NodeId.ToString(CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-e", "pipewiresrc", "fd={fd}", target, "do-timestamp=true", "keepalive-time=1000", "!",
            // The rate cap is negotiated with the producer (a videorate stage stalls this source).
            $"video/x-raw,framerate=[0/1,{Math.Max(1, fps).ToString(CultureInfo.InvariantCulture)}/1]", "!",
            "queue", "!", "videoconvert", "!", "videoscale", "!",
            $"video/x-raw,format={format},width=(int)[2,16384,2],height=(int)[2,16384,2]", "!"
        };
        args.AddRange(encode);
        args.AddRange(["!", "h264parse", "!", "queue", "!", "matroskamux", "name=mux", "!", "filesink", "location=" + video]);
        if (microphone is { Length: > 0 })
        {
            args.Add("pulsesrc");
            // gpu-screen-recorder calls the default source "default_input"; pulsesrc needs no device for it.
            if (microphone != "default_input") args.Add("device=" + microphone);
            args.AddRange(["!", "audioconvert", "!", "audioresample", "!", "opusenc", "!", "queue", "!", "mux."]);
        }
        return args;
    }

    private sealed class Prepared(ScreenCastSession session, PortalStream stream, CaptureSource source,
        CaptureRequest request, string encoder) : IPreparedCapture
    {
        public CaptureSource Source => source;

        public async Task<IRecording> StartAsync(ISessionClock clock, CancellationToken cancellationToken)
        {
            string video = Path.Combine(request.WorkDirectory, "recording.mkv");
            using SafeHandle remote = await session.OpenPipeWireRemoteAsync(cancellationToken);
            var info = new ProcessStartInfo("gst-launch-1.0")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, WorkingDirectory = request.WorkDirectory
            };
            foreach (string argument in Pipeline(encoder, stream, video, request.Microphone, request.FramesPerSecond)) info.ArgumentList.Add(argument);
            long launched = clock.ElapsedUs;
            var (process, _) = Spawn.StartWithDescriptor(info, remote);
            var supervised = SupervisedProcess.Adopt(process, "gst-launch-1.0");
            return new Recording(supervised, session, clock, video, launched, request.Microphone is { Length: > 0 });
        }

        public ValueTask DisposeAsync() => session.DisposeAsync();
    }

    private sealed class Recording : IRecording
    {
        private readonly SupervisedProcess process;
        private readonly ScreenCastSession session;
        private readonly ISessionClock clock;
        private readonly string video;
        private readonly long launched;
        private readonly bool audio;
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource watch = new();
        private Task<RecordingResult>? stopping;
        private long firstDataUs = -1;
        private volatile bool revoked;

        public Recording(SupervisedProcess process, ScreenCastSession session, ISessionClock clock, string video, long launched, bool audio)
        {
            this.process = process;
            this.session = session;
            this.clock = clock;
            this.video = video;
            this.launched = launched;
            this.audio = audio;
            Completion = CompleteAsync();
            _ = WatchAsync();
        }

        public Task Ready => ready.Task;
        public Task<RecordingResult> Completion { get; }

        public Task<RecordingResult> StopAsync(CancellationToken cancellationToken)
        {
            lock (watch) stopping ??= StopOnceAsync();
            return stopping.WaitAsync(cancellationToken);
        }

        private async Task<RecordingResult> StopOnceAsync()
        {
            // SIGINT with -e sends end-of-stream, so matroskamux writes its index before exiting.
            await process.StopAsync(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(3));
            return await Completion;
        }

        private async Task WatchAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            try
            {
                // Ready means a frame reached the file. A share that ends first, or a source that
                // produces nothing for 30 s (screencopy only sends frames on damage), fails it.
                while (!File.Exists(video) || new FileInfo(video).Length == 0)
                {
                    if (process.HasExited)
                        throw new IOException("gst-launch exited before recording: " +
                            ProcessRunner.LastLine(string.Join('\n', process.RecentOutput())));
                    if (session.Closed.IsCompleted)
                        throw new IOException("The screen share ended before any frame arrived.");
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("The shared source produced no frame within 30 seconds; " +
                            "a completely still window sends none. Last output: " +
                            ProcessRunner.LastLine(string.Join('\n', process.RecentOutput())));
                    await Task.Delay(20, watch.Token);
                }
                Interlocked.CompareExchange(ref firstDataUs, clock.ElapsedUs, -1);
                ready.TrySetResult();
                // A revoked share, or a vanished portal, ends the session; stop and keep the recording.
                await session.Closed.WaitAsync(watch.Token);
                revoked = true;
                await StopAsync(CancellationToken.None);
            }
            catch (OperationCanceledException) { ready.TrySetCanceled(); }
            catch (Exception error) { ready.TrySetException(error); }
        }

        private async Task<RecordingResult> CompleteAsync()
        {
            int code = await process.Exited;
            await watch.CancelAsync();
            var diagnostics = process.RecentOutput().TakeLast(40).Append("exit code " + code).ToList();
            if (revoked) diagnostics.Add("The portal session was closed (share revoked).");
            long first = Interlocked.Read(ref firstDataUs);
            // Without a first-frame timestamp from the pipeline, the first data on disk bounds the
            // first frame from above and the launch bounds it from below.
            var videoClock = first >= 0
                ? new ClockMap(first, ClockQuality.Estimated, "CLOCK_MONOTONIC",
                    "portal + gst-launch: first container write; the first frame lies between launch and this point",
                    Math.Max(33_333, first - launched))
                : new ClockMap(0, ClockQuality.Unavailable, "CLOCK_MONOTONIC", "No data was written.", 0);
            ClockMap? audioClock = audio ? videoClock with { Method = "Shared gst pipeline clock; not independently measured." } : null;
            bool finalized = code == 0 && !process.Escalated && File.Exists(video);
            return new RecordingResult(video, finalized, videoClock, audioClock, diagnostics.ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited) await StopAsync(CancellationToken.None);
            await process.DisposeAsync();
            watch.Dispose();
        }
    }
}
