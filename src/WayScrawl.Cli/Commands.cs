using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using WayScrawl.Application;
using WayScrawl.Core;
using WayScrawl.Platform;
using WayScrawl.Platform.Hyprland;
using WayScrawl.Platform.Media;
using WayScrawl.Transcription;

namespace WayScrawl.Cli;

internal static partial class Commands
{
    public static async Task<int> ClientAsync(WayScrawlPaths paths, string command, string[] rest,
        CancellationToken cancellationToken)
    {
        if (command == DaemonCommands.Status && rest.Length > 0)
        {
            var options = new CliArguments(rest, [], ["--waybar", "--follow"]);
            if (!options.Has("--waybar")) throw new ArgumentException("--follow requires --waybar.");
            return await WaybarAsync(paths, options.Has("--follow"), cancellationToken);
        }
        string? argument = rest.Length > 0 ? string.Join(' ', rest) : null;
        bool autoStart = command is DaemonCommands.Start or DaemonCommands.Toggle;
        DaemonResponse response;
        try
        {
            response = await DaemonClient.SendAsync(paths, command, argument, autoStart, cancellationToken);
        }
        catch (DaemonUnavailableException) when (command == DaemonCommands.Status)
        {
            Console.WriteLine("Daemon: not running");
            return 0;
        }
        var status = response.Status;
        Console.WriteLine(response.Message);
        if (command == DaemonCommands.Status)
        {
            Console.WriteLine($"State: {status.State}{(status.SessionId is null ? "" : " " + status.SessionId)}");
            if (status.State == "Recording")
                Console.WriteLine($"Elapsed: {status.ElapsedUs / 1_000_000d:0.0} s; overlay: {status.Draw?.ToString() ?? "none"}");
            Console.WriteLine($"Backend: {status.Backend}; processing: {status.Processing}; config problems: {status.ConfigProblems}");
            if (status.LastBundle is not null) Console.WriteLine("Last bundle: " + status.LastBundle);
            if (status.LastError is not null) Console.WriteLine("Last error: " + status.LastError);
        }
        return response.Ok ? 0 : 3;
    }

    public static async Task<int> DaemonAsync(WayScrawlPaths paths, bool detached, CancellationToken cancellationToken)
    {
        using var lockFile = DaemonServer.AcquireLock(paths);
        WayScrawlPaths.EnsurePrivateDirectory(paths.State);
        if (detached)
        {
            var log = new StreamWriter(new FileStream(paths.DaemonLog, FileMode.Append, FileAccess.Write, FileShare.Read))
                { AutoFlush = true };
            Console.SetOut(log);
            Console.SetError(log);
        }
        void Log(string message) => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
        var (config, problems) = WayScrawlConfig.Load(paths);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); });
        using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => context.Cancel = true);
        await using var coordinator = Wiring.CreateCoordinator(paths, config, Log, problems.Length);
        foreach (string problem in problems) Log("Config: " + problem);
        if (problems.Length > 0 && config.Notifications)
            await Desktop.NotifyAsync("WayScrawl configuration", string.Join("\n", problems));
        var server = new DaemonServer(paths, coordinator, Log);
        Log($"WayScrawl daemon {Environment.ProcessId} started; backend {config.Backend}.");
        Task retention = config.Retention is null ? Task.CompletedTask : RetentionSchedule.RunAsync(Log, stop.Token);
        try { await server.RunAsync(stop.Token); }
        finally { await stop.CancelAsync(); await retention; }
        Log("Daemon stopping.");
        return 0;
    }

    public static async Task<int> ProcessAsync(WayScrawlPaths paths, string session, CancellationToken cancellationToken)
    {
        var (config, problems) = WayScrawlConfig.Load(paths);
        string model = config.ResolveModel(paths);
        var whisper = config.WhisperOrDefault;
        ITranscriber? transcriber = File.Exists(model)
            ? new WorkerTranscriber(model, whisper.Language, whisper.Runtime, TimeSpan.FromSeconds(whisper.TimeoutSeconds))
            : null;
        if (transcriber is null) Console.Error.WriteLine($"No Whisper model at {model}; narration will not be transcribed.");
        var builder = new EvidenceBuilder(paths, config, transcriber, message => Console.Error.WriteLine(message));
        string bundle = await builder.BuildAsync(Path.GetFullPath(session), cancellationToken);
        Console.WriteLine(bundle);
        return 0;
    }

    // Hidden worker verb: transcribe WAV OUT.json --model PATH --language CODE --runtime auto|cuda|cpu
    public static async Task<int> TranscribeAsync(string[] rest, CancellationToken cancellationToken)
    {
        string Option(string name, string fallback)
        {
            int index = Array.IndexOf(rest, name);
            return index >= 0 && index + 1 < rest.Length ? rest[index + 1] : fallback;
        }
        var transcriber = new WhisperTranscriber(Option("--model", ""), Option("--language", "auto"), Option("--runtime", "auto"));
        var result = await transcriber.TranscribeAsync(rest[0], cancellationToken);
        string temporary = rest[1] + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(result, WayScrawlJson.Options), cancellationToken);
        File.Move(temporary, rest[1], overwrite: true);
        return 0;
    }

    public static int Sessions(WayScrawlPaths paths)
    {
        if (!Directory.Exists(paths.Sessions))
        {
            Console.WriteLine("No sessions.");
            return 0;
        }
        foreach (string directory in Directory.EnumerateDirectories(paths.Sessions).Order())
        {
            try
            {
                var record = EvidenceBuilder.ReadRecord(directory);
                string duration = record.DurationUs is { } us ? $"{us / 1_000_000d:0.0} s" : "-";
                Console.WriteLine($"{record.Id}  {record.State,-9} {duration,8}  {record.Bundle ?? directory}");
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
            {
                Console.WriteLine($"{Path.GetFileName(directory)}  unreadable: {error.Message}");
            }
        }
        return 0;
    }

    public static Task<string> ResolveBundleAsync(WayScrawlPaths paths, string name, CancellationToken cancellationToken) =>
        name == "latest" ? BundleStore.LatestAsync(WayScrawlConfig.Load(paths).Config.BundleDirectory ?? paths.Bundles, cancellationToken)
            : Task.FromResult(Path.GetFullPath(name));

    private static string BundleFileStem(string id) =>
        new(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').Take(128).ToArray());

    public static async Task<int> FrameAsync(string[] rest, CancellationToken cancellationToken)
    {
        var paths = WayScrawlPaths.FromEnvironment();
        string directory = await ResolveBundleAsync(paths, rest[0], cancellationToken);
        using var lease = OperationLease.Acquire(directory, paths.Runtime);
        var bundle = await BundleStore.ReadAsync(directory, cancellationToken);
        if (!double.TryParse(rest[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) || !double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentException("SECONDS must be a non-negative number.");
        if (seconds * 1_000_000 > bundle.DurationUs)
            throw new ArgumentException($"The recording is {bundle.DurationUs / 1_000_000d:0.0} s long.");
        var (_, stream, video) = MediaFiles.Require(directory, bundle);
        var probe = await Ffmpeg.ProbeAsync(video, cancellationToken);
        long pts = (long)(seconds * 1_000_000) - stream.Clock.OffsetUs;
        var (png, actual) = await Ffmpeg.ExtractPngAsync(video, pts, probe.FormatStartUs, cancellationToken);
        string output = rest.Length == 3 ? rest[2]
            : Path.Combine(Environment.CurrentDirectory, $"{BundleFileStem(bundle.Id)}-{seconds.ToString("0.000", CultureInfo.InvariantCulture)}s.png");
        await using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await file.WriteAsync(png, cancellationToken);
        long at = (actual ?? pts) + stream.Clock.OffsetUs;
        Console.WriteLine($"{Path.GetFullPath(output)} (session time {(at / 1_000_000d).ToString("0.000", CultureInfo.InvariantCulture)} s)");
        return 0;
    }

    public static async Task<int> SendAsync(string bundle, bool clipboard, CancellationToken cancellationToken)
    {
        string directory = await ResolveBundleAsync(WayScrawlPaths.FromEnvironment(), bundle, cancellationToken);
        var manifest = await BundleStore.ReadAsync(directory, cancellationToken);
        if (clipboard)
        {
            await new ClipboardSink().SendAsync(directory, "clipboard", cancellationToken);
            Console.WriteLine("Copied a prompt for " + directory);
        }
        else Console.WriteLine(PasteText.Compose(manifest, directory));
        return 0;
    }

    public static int Review(string[] rest)
    {
        string review = SelfCommand.FindCompanion("wayscrawl-review", "WayScrawl.Review")
            ?? throw new InvalidOperationException(
                $"{Path.Combine(AppContext.BaseDirectory, "wayscrawl-review")} was not found. Install with scripts/install.sh.");
        var info = new System.Diagnostics.ProcessStartInfo("setsid") { UseShellExecute = false };
        foreach (string argument in new[] { "-f", review }.Concat(rest)) info.ArgumentList.Add(argument);
        using var process = Spawn.Start(info);
        process.WaitForExit();
        return 0;
    }

    public static async Task<int> DoctorAsync(WayScrawlPaths paths, bool connect, CancellationToken cancellationToken)
    {
        Console.WriteLine(RuntimeInformation.FrameworkDescription);
        Console.WriteLine(RuntimeInformation.OSDescription);
        foreach (string variable in new[] { "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP",
                    "WAYLAND_DISPLAY", "HYPRLAND_INSTANCE_SIGNATURE", "XDG_RUNTIME_DIR" })
            Console.WriteLine(variable + ": " +
                (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)) ? "absent" : "present"));
        foreach (string executable in new[] { "hyprctl", "gpu-screen-recorder", "ffmpeg", "ffprobe", "gst-launch-1.0",
                    "pw-record", "wpctl", "wl-copy", "notify-send", "setsid" })
            Console.WriteLine(executable + ": " + (FindTool(executable) ?? "not found"));
        var (config, problems) = WayScrawlConfig.Load(paths);
        foreach (string problem in problems) Console.WriteLine("Config problem: " + problem);
        Console.WriteLine($"Config: {(File.Exists(paths.ConfigFile) ? paths.ConfigFile : "defaults (no " + paths.ConfigFile + ")")}");
        Console.WriteLine($"Backend: {config.Backend}; output: {config.Output}; microphone: {config.Microphone ?? "off"}");
        string model = config.ResolveModel(paths);
        Console.WriteLine($"Whisper model: {model} ({(File.Exists(model) ? "present" : "MISSING")})");
        Console.WriteLine($"Sessions: {paths.Sessions}\nBundles: {config.BundleDirectory ?? paths.Bundles}");
        try
        {
            foreach (var monitor in await HyprlandIpc.MonitorsAsync(cancellationToken))
                Console.WriteLine($"Monitor {monitor.Name}: {monitor.Width}x{monitor.Height} at {monitor.X},{monitor.Y} " +
                    $"scale {monitor.Scale.ToString(CultureInfo.InvariantCulture)} transform {monitor.Transform}" +
                    (monitor.Focused ? " (focused)" : ""));
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or JsonException)
        {
            Console.WriteLine("Monitors: " + error.Message);
        }
        foreach (var capability in await Wiring.CreateBackend(paths, config).ProbeAsync(cancellationToken))
            Console.WriteLine($"{capability.Name}: {capability.Status} — {capability.Detail}");
        if (connect)
            Console.WriteLine(JsonSerializer.Serialize(WaylandProbe.ConnectOnce(), BundleStore.Json));
        try
        {
            var response = await DaemonClient.SendAsync(paths, DaemonCommands.Status, null, false, cancellationToken);
            Console.WriteLine($"Daemon: running, {response.Status.State}");
        }
        catch (DaemonUnavailableException) { Console.WriteLine("Daemon: not running"); }
        return 0;
    }

    // Hidden diagnostic: maps the overlay in pass-through on an output, draws a test stroke
    // without any input, waits, then unmaps. Optionally records the output meanwhile and
    // reports whether the ink reached the captured pixels.
    public static async Task<int> OverlayProbeAsync(string[] rest, CancellationToken cancellationToken)
    {
        string output = rest.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "focused";
        bool record = rest.Contains("--record");
        var monitor = HyprlandIpc.Select(await HyprlandIpc.MonitorsAsync(cancellationToken), output);
        var geometry = monitor.ToGeometry("g0");
        var clock = new SessionClock();
        await using var overlay = new WayScrawl.Wayland.WaylandOverlay(new("#ff3b30", 10));
        string directory = Path.Combine(Path.GetTempPath(), "wayscrawl-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        IRecording? recording = null;
        try
        {
            if (record)
            {
                var prepared = await new GpuScreenRecorderBackend().PrepareAsync(new(monitor.Name, null, directory), cancellationToken);
                recording = await prepared.StartAsync(clock, cancellationToken);
                await recording.Ready.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            var started = DateTime.UtcNow;
            await overlay.ShowAsync(geometry, monitor.Name, clock, cancellationToken);
            Console.WriteLine($"Overlay mapped on {monitor.Name} in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms (pass-through).");
            double w = geometry.LogicalWidth, h = geometry.LogicalHeight;
            var points = Enumerable.Range(0, 40).Select(i => new PointD(w * (0.30 + i * 0.01), h * 0.5 + Math.Sin(i / 4.0) * h * 0.05)).ToList();
            long drawnAt = clock.ElapsedUs;
            await overlay.DrawAsync(points, cancellationToken);
            await Task.Delay(1500, cancellationToken);
            await overlay.HideAsync(cancellationToken);
            Console.WriteLine("Overlay unmapped.");
            Stroke? stroke = null;
            while (overlay.Events.TryRead(out var item))
            {
                if (item is StrokeCompleted done) stroke = done.Stroke;
                Console.WriteLine($"  event {item.GetType().Name} at {item.AtUs / 1000} ms");
            }
            if (recording is null || stroke is null) return 0;
            var result = await recording.StopAsync(cancellationToken);
            long pts = drawnAt + 700_000 - result.VideoClock.OffsetUs;
            var probe = await Ffmpeg.ProbeAsync(result.VideoPath, cancellationToken);
            var (png, _) = await Ffmpeg.ExtractPngAsync(result.VideoPath, pts + probe.VideoStartUs, probe.FormatStartUs, cancellationToken);
            using var frame = WayScrawl.Rendering.Imaging.Decode(png);
            var composition = EvidenceBuilder.DetectComposition(frame, [stroke], drawnAt + 700_000);
            Console.WriteLine($"Composition with {new GpuScreenRecorderBackend().Name}: {composition.Mode} — {composition.Basis}");
            return 0;
        }
        finally
        {
            if (recording is not null) await recording.DisposeAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string? FindTool(string name)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            try
            {
                if ((File.GetUnixFileMode(path) & (UnixFileMode.UserExecute |
                        UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                    continue;
                return path;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }
}
