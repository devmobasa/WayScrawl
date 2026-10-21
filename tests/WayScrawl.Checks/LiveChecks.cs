using WayScrawl.Core;
using WayScrawl.Platform.Media;

namespace WayScrawl.Checks;

// Captures this desktop. Run only in an attended session: dotnet run --project tests/WayScrawl.Checks -- --live
internal static class LiveChecks
{
    public static async Task RunAsync(Harness h)
    {
        h.Section = "live-recorder";
        string directory = h.TempDirectory("live");
        var backend = new GpuScreenRecorderBackend();
        var prepared = await backend.PrepareAsync(new CaptureRequest("focused", null, directory), CancellationToken.None);
        h.Check(prepared.Source.Geometry.PixelWidth > 0 && prepared.Source.Geometry.Source.StartsWith("monitor:", StringComparison.Ordinal),
            "Focused monitor geometry: " + prepared.Source.Detail);
        var clock = new SessionClock();
        await using var recording = await prepared.StartAsync(clock, CancellationToken.None);
        await recording.Ready.WaitAsync(TimeSpan.FromSeconds(15));
        long ready = clock.ElapsedUs;
        await Task.Delay(2000);
        var result = await recording.StopAsync(CancellationToken.None);
        var again = await recording.StopAsync(CancellationToken.None);
        h.Check(result.Finalized && ReferenceEquals(result, again), "Graceful finalization; a second stop joins the first.");
        h.Check(result.VideoClock.Quality == ClockQuality.Measured && result.VideoClock.OffsetUs > 0 &&
                result.VideoClock.OffsetUs <= ready, $"First frame measured at {result.VideoClock.OffsetUs} µs, before readiness at {ready} µs.");
        var probe = await Ffmpeg.ProbeAsync(result.VideoPath, CancellationToken.None);
        h.Check(probe.Width == prepared.Source.Geometry.PixelWidth && probe.DurationUs > 1_500_000 && probe.AudioCodec is null,
            $"Probe matches the output ({probe.Width}x{probe.Height}, {probe.DurationUs} µs, no audio requested).");
    }
}
