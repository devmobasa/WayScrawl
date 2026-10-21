using WayScrawl.Core;
using WayScrawl.Platform;
using WayScrawl.Platform.Hyprland;
using WayScrawl.Platform.Media;
using WayScrawl.Portal;
using WayScrawl.Wayland;

namespace WayScrawl.Checks;

// The portal backend end to end inside the isolated session: portal consent (scripted picker),
// restore-token rotation, descriptor hand-off to gst-launch, graceful finalization.
internal static class PortalChecks
{
    public static async Task RunAsync(Harness h)
    {
        h.Section = "portal";
        var stream = new PortalStream(42, 1, 0, 0, 641, 481, "1");
        var args = PortalCaptureBackend.Pipeline("x264enc", stream, "/tmp/out.mkv", "default_input", 30);
        h.Check(args.Contains("fd={fd}") && args.Contains("path=42") && args.Contains("video/x-raw,framerate=[0/1,30/1]") &&
                args.Contains("pulsesrc") && !args.Any(a => a.StartsWith("device=")),
            "The pipeline names the inherited descriptor and node, caps the producer's rate, and uses the default microphone.");
        h.Check(PortalCaptureBackend.Pipeline("x264enc", stream with { Serial = 77 }, "x.mkv", null, 30).Contains("target-object=77"),
            "A v6 stream serial is preferred over the reusable node id.");
        h.Check(PortalCaptureBackend.Pipeline("nvh264enc", stream, "x.mkv", null, 60).Any(a => a.Contains("format=NV12")) &&
                PortalCaptureBackend.Pipeline("openh264enc", stream, "x.mkv", null, 60).Any(a => a.Contains("format=I420")),
            "Each encoder gets a pixel format it accepts.");
        if (await PortalCaptureBackend.GstEncoderAsync(CancellationToken.None) is { } available)
        {
            // An odd-sized window must still encode: the caps range rounds it to an even size.
            var generated = PortalCaptureBackend.Pipeline(available, stream, "x.mkv", null, 30);
            string caps = generated.First(a => a.StartsWith("video/x-raw", StringComparison.Ordinal));
            var odd = await ProcessRunner.RunAsync("gst-launch-1.0", ["-q", "videotestsrc", "num-buffers=5", "!",
                "video/x-raw,width=641,height=481", "!", "videoconvert", "!", "videoscale", "!", caps, "!", available,
                "!", "h264parse", "!", "fakesink"], CancellationToken.None);
            h.Check(odd.ExitCode == 0, $"A 641x481 source encodes with {available}: {ProcessRunner.LastLine(odd.Error)}");
        }

        string aquamarine = Environment.GetEnvironmentVariable("WAYSCRAWL_TEST_AQUAMARINE")
            ?? Path.Combine(OverlayChecks.RepositoryRoot(), ".cache", "aquamarine");
        string? missing = NestedHyprland.MissingPrerequisite(aquamarine) ??
            (await PortalCaptureBackend.GstEncoderAsync(CancellationToken.None) is null ? "no GStreamer encoder" : null);
        if (missing is not null)
        {
            Console.WriteLine("Skipped isolated portal checks: " + missing + ".");
            return;
        }
        await using var nested = await NestedHyprland.StartAsync(aquamarine);
        await nested.StartPortalAsync();
        var instance = new HyprlandInstance(nested.Runtime, nested.Environment["HYPRLAND_INSTANCE_SIGNATURE"]);
        string work = h.TempDirectory("portal");
        string tokenFile = Path.Combine(work, "token");
        static int Sockets() => Directory.EnumerateFiles("/proc/self/fd")
            .Count(f => (new FileInfo(f).LinkTarget ?? "").StartsWith("socket:", StringComparison.Ordinal));
        int socketsBefore = Sockets();
        var backend = new PortalCaptureBackend(tokenFile, nested.BusAddress, instance);
        var probe = await backend.ProbeAsync(CancellationToken.None);
        h.Check(probe.All(c => c.Status == Availability.Available), "Portal and GStreamer are available: " + string.Join("; ", probe.Select(c => c.Detail)));

        for (int round = 1; round <= 2; round++)
        {
            string directory = Path.Combine(work, "session" + round);
            Directory.CreateDirectory(directory);
            string? tokenBefore = File.Exists(tokenFile) ? await File.ReadAllTextAsync(tokenFile) : null;
            await using var prepared = await backend.PrepareAsync(new CaptureRequest("focused", null, directory), CancellationToken.None);
            h.Check(prepared.Source.Geometry is { Source: "monitor:TEST", PixelWidth: 1920 }, $"Round {round}: the portal stream maps to the TEST monitor ({prepared.Source.Detail}).");
            string tokenAfter = await File.ReadAllTextAsync(tokenFile);
            h.Check(tokenAfter.Length > 0 && tokenAfter != tokenBefore, $"Round {round}: the single-use restore token is rotated.");
            var clock = new SessionClock();
            // A headless output only produces frames on damage: keep drawing on a pass-through overlay.
            var monitor = (await nested.MonitorsAsync()).Single(m => m.Name == "TEST");
            await using var overlay = new WaylandOverlay(new OverlayOptions(Display: nested.Display));
            await overlay.ShowAsync(monitor.ToGeometry("g0"), "TEST", clock, CancellationToken.None);
            using var drawing = new CancellationTokenSource();
            var damage = Task.Run(async () =>
            {
                for (int i = 0; !drawing.IsCancellationRequested; i++)
                {
                    await overlay.DrawAsync([new(100 + i * 7 % 900, 100), new(200 + i * 7 % 900, 160)], CancellationToken.None);
                    await Task.Delay(100);
                }
            });
            await using var recording = await prepared.StartAsync(clock, CancellationToken.None);
            await recording.Ready.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(2000);
            var result = await recording.StopAsync(CancellationToken.None);
            await drawing.CancelAsync();
            await damage;
            h.Check(result.Finalized && result.VideoClock.Quality == ClockQuality.Estimated,
                $"Round {round}: gst-launch finalizes on SIGINT; the clock is labelled estimated. {string.Join(" | ", result.Diagnostics.TakeLast(14))}");
            MediaProbe media;
            try { media = await Ffmpeg.ProbeAsync(result.VideoPath, CancellationToken.None); }
            catch (IOException error)
            {
                throw new CheckFailed($"[portal] Round {round}: unreadable recording ({error.Message}); gst: " +
                    string.Join(" | ", result.Diagnostics.TakeLast(8)));
            }
            h.Check(media.Width == 1920 && media.Height == 1080 && media.DurationUs > 1_000_000,
                $"Round {round}: the recording holds the portal stream ({media.Width}x{media.Height}, {media.DurationUs} µs).");
        }
        // Ending the share on the desktop side (here the portal backend goes away, so the portal
        // closes the session) must end the recording by itself and keep what was captured.
        string revokedDirectory = Path.Combine(work, "revoked");
        Directory.CreateDirectory(revokedDirectory);
        await using (var prepared = await backend.PrepareAsync(new CaptureRequest("focused", null, revokedDirectory), CancellationToken.None))
        {
            var clock = new SessionClock();
            var monitor = (await nested.MonitorsAsync()).Single(m => m.Name == "TEST");
            await using var overlay = new WaylandOverlay(new OverlayOptions(Display: nested.Display));
            await overlay.ShowAsync(monitor.ToGeometry("g0"), "TEST", clock, CancellationToken.None);
            await using var recording = await prepared.StartAsync(clock, CancellationToken.None);
            // Keep damaging until PipeWire has connected; a burst before the consumer is ready
            // can coalesce into one compositor frame and leave the source idle forever.
            for (int i = 0; i < 150 && !recording.Ready.IsCompleted; i++)
            {
                await overlay.DrawAsync([new(100 + i * 7 % 900, 300), new(160 + i * 7 % 900, 340)], CancellationToken.None);
                await Task.Delay(100);
            }
            await recording.Ready.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(1000);
            nested.Stop("xdg-desktop-portal-hyprland");
            var ended = await recording.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            string notes = string.Join(" | ", ended.Diagnostics.TakeLast(3));
            // Either the portal's Closed signal stops it, or the pipeline ends when its stream vanishes.
            h.Check(ended.Diagnostics.Any(d => d.Contains("revoked")) || !ended.Diagnostics.Contains("exit code 0"),
                "A share ended by the desktop ends the recording without a stop request: " + notes);
            // Without its source the pipeline cannot always finalize; the result says so, and the
            // evidence builder's recovery (remux) keeps what was captured.
            string recovered = Path.Combine(revokedDirectory, "recovered.mkv");
            var remux = await ProcessRunner.RunAsync("ffmpeg", ["-nostdin", "-v", "error", "-i", ended.VideoPath,
                "-map", "0", "-c", "copy", recovered], CancellationToken.None);
            if (remux.ExitCode != 0)
                throw new CheckFailed($"[portal] Revoked recording unrecoverable ({new FileInfo(ended.VideoPath).Length} bytes, " +
                    $"finalized {ended.Finalized}): {ProcessRunner.LastLine(remux.Error)}; gst: {string.Join(" | ", ended.Diagnostics.TakeLast(8))}");
            var kept = await Ffmpeg.ProbeAsync(recovered, CancellationToken.None);
            h.Check((ended.Finalized || !ended.Finalized && kept.DurationUs > 300_000),
                $"The revoked recording is kept (finalized {ended.Finalized}, {kept.DurationUs} µs after recovery).");
        }

        // Portal remotes are sockets: after every session is disposed, none may remain.
        await Task.Delay(300);
        h.Check(Sockets() <= socketsBefore, $"No portal or PipeWire socket stays open ({socketsBefore} before, {Sockets()} after).");
    }
}
