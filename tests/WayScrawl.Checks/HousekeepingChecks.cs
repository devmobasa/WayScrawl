using System.Text.Json;
using WayScrawl.Application;
using WayScrawl.Core;
using WayScrawl.Platform;

namespace WayScrawl.Checks;

internal static class HousekeepingChecks
{
    public static void Core(Harness h)
    {
        h.Section = "window-crop";
        var window = new WindowSnapshot("0xa", "editor", new(100, 100, 200, 200), true);
        TimelineEvent Layout(WindowSnapshot[] windows, long at = 0) =>
            new(at, "window.layout", JsonSerializer.Serialize(windows, WayScrawlJson.Journal));
        var crop = CropPlanner.Plan([], 100, 1000, 800, events: [Layout([window])]);
        h.Check(crop == new PixelRect(80, 84, 240, 232), "Focused small window gets two-percent frame padding.");
        h.Check(CropPlanner.Plan([], 100, 1000, 800, events: [Layout([window with { Rect = new(0, 0, 600, 800) }])]) is null,
            "A window covering at least 60 percent uses the full frame.");
        h.Check(CropPlanner.Plan([], 100, 1000, 800, events: [Layout([window with { Rect = new(103, 102, 200, 200) }])], previous: crop) ==
                    crop,
            "Small window motion reuses a crop with over 70 percent overlap.");
        h.Check(CropPlanner.Plan([], 200, 1000, 800, events: [Layout([window]), new(150, "telemetry.disconnected", "")]) is null,
            "Disconnected telemetry cannot keep a stale window crop.");
        var largeInk = new Stroke("s", "g", "#ffffff", 10, [new(0, 0, 0), new(50, 999, 799)]);
        h.Check(CropPlanner.Plan([largeInk], 100, 1000, 800, events: [Layout([window])]) is null,
            "Visible ink keeps its full-frame policy instead of a window fallback.");

        h.Section = "export-plan";
        var bundle = CoreChecks.SampleBundle();
        h.Check(ExportPlan.Create(bundle, 1920, 1080, "full", 3840) is { Width: 1920, Height: 1080 }, "MP4 export never upscales.");
        h.Check(ExportPlan.Create(bundle, 640, 360, "1,3,401,201", 300)
            is { Width : 300, Height : 150, Crop : { Width : 400, Height : 200, X : 1, Y : 3 } },
            "Explicit export crops retain origin and round H.264 dimensions down to even pixels.");
        foreach (string invalid in new[] { "1,2,3", "-1,0,10,10", "0,0,10000,10", "0,0,1,1", "wrong" })
            h.Reject(() => ExportPlan.Create(bundle, 640, 360, invalid), "Invalid export crop refused: " + invalid);
        h.Check(ExportPlan.Create(bundle, 200, 200).Crop == new PixelRect(0, 0, 40, 40), "Auto export uses the union of detail crops.");

        h.Section = "retention-config";
        string root = h.TempDirectory("retention-config");
        var paths = new WayScrawlPaths(root, root, root, root);
        h.Check(
            WayScrawlConfig.Load(paths).Config.Retention is { MediaDays : 7, BundleDays : 30 }, "Retention defaults to seven/thirty days.");
        foreach (string invalid in new[] { "true", "{\"mediaDays\":\"bad\",\"bundleDays\":90}", "{\"mediaDays\":31,\"bundleDays\":30}",
                     "{\"purge\":\"yes\"}", "{\"orphanDays\":0}", "{\"orphanDays\":36501}", "{\"orphanDays\":\"bad\"}" })
        {
            File.WriteAllText(paths.ConfigFile, "{\"retention\":" + invalid + ",\"maxFrames\":7}");
            var loaded = WayScrawlConfig.Load(paths);
            h.Check(loaded.Config.Retention is null && loaded.Config.MaxFrames == 7 && loaded.Problems.Length > 0,
                "Invalid retention disables automatic cleanup and preserves valid settings.");
        }
        File.WriteAllText(paths.ConfigFile, "{\"retention\":null}");
        h.Check(WayScrawlConfig.Load(paths).Config.Retention is null, "Explicit null keeps everything.");
        File.WriteAllText(paths.ConfigFile, "{\"retention\":{\"purge\":true}}");
        h.Check(WayScrawlConfig.Load(paths).Config.Retention is { Purge: true }, "Purge requires explicit valid opt-in.");
    }

    public static async Task FeedAsync(Harness h)
    {
        h.Section = "waybar";
        var recording = new DaemonStatus("Recording", "s", 42_000_000, OverlayInputMode.PassThrough, null, null, 0, "fixture");
        var drawing = recording with { Draw = OverlayInputMode.Drawing, ElapsedUs = 43_000_000 };
        var processing = recording with { State = "Idle", SessionId = null, Processing = 2 };
        object?[] states = [null, recording, recording with { ElapsedUs = 42_500_000 }, drawing, processing,
            new IOException("connection reset"), new JsonException("truncated reply"), new System.Net.Sockets.SocketException(),
            recording with { ElapsedUs = 44_000_000 }];
        int index = 0;
        Task<DaemonStatus> Query(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var state = states[Math.Min(index++, states.Length - 1)];
            return state switch
            {
                null => Task.FromException<DaemonStatus>(new DaemonUnavailableException("offline")),
                Exception error => Task.FromException<DaemonStatus>(error),
                DaemonStatus status => Task.FromResult(status),
                _ => throw new InvalidOperationException()
            };
        }
        var lines = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await foreach (string line in StatusFeed.LinesAsync(Query, true, timeout.Token))
        { lines.Add(line); if (lines.Count == 6) break; }
        string Text(int i) { using var doc = JsonDocument.Parse(lines[i]); return doc.RootElement.GetProperty("text").GetString()!; }
        h.Check(lines.Count == 6 && Text(0) == "" && Text(1) == "● 0:42" && Text(2) == "✎ 0:43" && Text(3) == "2" && Text(4) == "" &&
                    Text(5) == "● 0:44",
            "Waybar deduplicates JSON states and reconnects after offline, reset, truncated and socket failures.");
    }
}
