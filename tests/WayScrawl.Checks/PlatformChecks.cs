using System.Buffers.Binary;
using System.IO.Compression;
using WayScrawl.Core;
using WayScrawl.Platform;
using WayScrawl.Platform.Hyprland;

namespace WayScrawl.Checks;

internal static class PlatformChecks
{
    public static async Task RunAsync(Harness h)
    {
        await CliChecks.RunAsync(h);
        await InstallChecks.RunAsync(h);
        await HousekeepingChecks.FeedAsync(h);
        h.Section = "process";
        await using (var graceful = SupervisedProcess.Start("bash",
            ["-c", "trap 'echo finalized; exit 0' INT; echo ready; while true; do sleep 0.02; done"]))
        {
            await WaitForAsync(() => graceful.RecentOutput().Contains("ready"));
            int code = await graceful.StopAsync(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1));
            h.Check(code == 0 && !graceful.Escalated && graceful.RecentOutput().Contains("finalized"),
                "SIGINT lets a child finalize without escalation.");
            h.Check(!graceful.Signal(2), "An exited child is never signalled.");
        }
        await using (var stubborn = SupervisedProcess.Start("bash",
            ["-c", "trap '' INT TERM; echo ready; while true; do sleep 0.02; done"]))
        {
            await WaitForAsync(() => stubborn.RecentOutput().Contains("ready"));
            int code = await stubborn.StopAsync(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300));
            h.Check(stubborn.Escalated && code == 137, $"A hung child is escalated to SIGKILL (exit {code}).");
        }
        await using (var flood = SupervisedProcess.Start("bash",
            ["-c", "for i in $(seq 1 20000); do echo out $i; echo err $i >&2; done"], maxLines: 100))
        {
            int code = await flood.Exited.WaitAsync(TimeSpan.FromSeconds(30));
            var recent = flood.RecentOutput();
            // The two pumps finish independently; the newest lines come from whichever ended last.
            h.Check(code == 0 && recent.Count == 100 && (recent.Contains("out 20000") || recent.Contains("err 20000")),
                "Saturated stdout and stderr are drained and bounded.");
        }
        // A grandchild that inherits the pipes must not stretch the child's exit past the drain bound.
        await using (var parent = SupervisedProcess.Start("bash", ["-c", "sleep 30 & echo $! ; exit 0"]))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int code = await parent.Exited.WaitAsync(TimeSpan.FromSeconds(10));
            h.Check(code == 0 && watch.Elapsed < TimeSpan.FromSeconds(5), $"Inherited pipes do not delay exit ({watch.ElapsedMilliseconds} ms).");
            if (int.TryParse(parent.RecentOutput().FirstOrDefault(), out int orphan)) WayScrawl.Platform.Native.Libc.Kill(orphan, 9);
        }
        await h.RejectAsync<InvalidDataException>(() => ProcessRunner.RunAsync("head", ["-c", "5000", "/dev/zero"],
            CancellationToken.None, maxOutputBytes: 1000), "Tool output is capped.");
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            await h.RejectAsync<OperationCanceledException>(() => ProcessRunner.RunAsync("sleep", ["30"], cancel.Token),
                "A cancelled tool is killed rather than awaited.");

        h.Section = "hyprland";
        var monitors = HyprlandIpc.ParseMonitors("""
            [{"name":"DP-1","description":"Dell","x":2304,"y":0,"width":3840,"height":2160,"scale":1.6666666,
              "transform":0,"focused":true,"disabled":false},
             {"name":"HDMI-A-1","x":-1080,"y":0,"width":1920,"height":1080,"scale":1,"transform":1,"focused":false}]
            """);
        h.Check(monitors.Length == 2 && Math.Abs(monitors[0].LogicalWidth - 2304) < 0.01, "Fractional logical width.");
        h.Check(monitors[1].LogicalWidth == 1080 && monitors[1].LogicalHeight == 1920, "Rotated output swaps logical size.");
        h.Check(HyprlandIpc.Select(monitors, "focused").Name == "DP-1" && HyprlandIpc.Select(monitors, "HDMI-A-1").X == -1080,
            "Output selection by focus and by name.");
        h.Reject(() => HyprlandIpc.Select(monitors, "DP-9"), "Unknown output rejected.");
        var geometry = monitors[0].ToGeometry("g0");
        h.Check(geometry.Source == "monitor:DP-1" && geometry.PixelWidth == 3840, "Monitor geometry keeps source identity.");

        h.Section = "store";
        string root = h.TempDirectory("store");
        string output = Path.Combine(root, "demo.wayscrawl");
        await Demo.CreateAsync(output);
        Bundle demo = await BundleStore.ReadAsync(output);
        h.Check(demo.Synthetic && demo.Keyframes.Length == 3 && demo.Schema == Bundle.CurrentSchema, "Synthetic round trip.");
        h.Check(demo.Transcript.All(s => s.Timing == TimingQuality.Synthetic), "No fake measured speech.");
        byte[] first = DecodeFixture(Path.Combine(output, "frames/frame-00.png"), 320, 180);
        byte[] second = DecodeFixture(Path.Combine(output, "frames/frame-01.png"), 320, 180);
        _ = DecodeFixture(Path.Combine(output, "contact-sheet.png"), 960, 180);
        int index = 115 * (1 + 320 * 3) + 1 + 55 * 3;
        h.Check(first[index] != 255 && second[index] == 255 && second[index + 1] == 59, "Ink appears only in later synthetic frame.");
        string summary = await File.ReadAllTextAsync(Path.Combine(output, "summary.md"));
        h.Check(summary.Contains("SYNTHETIC DEMO") && summary.Contains("not instructions"), "Summary discloses synthetic content.");

        string sentinel = Path.Combine(output, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");
        await h.RejectAsync<IOException>(() => Demo.CreateAsync(output), "Existing destination refused.");
        h.Check(await File.ReadAllTextAsync(sentinel) == "preserve", "Existing destination preserved.");
        string abandoned = Path.Combine(root, "cancelled.wayscrawl");
        using (var token = new CancellationTokenSource())
        {
            await token.CancelAsync();
            await h.RejectAsync<OperationCanceledException>(() => Demo.CreateAsync(abandoned, token.Token), "Cancelled demo.");
        }
        h.Check(!Directory.Exists(abandoned) && !Directory.EnumerateDirectories(root, ".*partial").Any(),
            "Cancellation never publishes and staging is cleaned.");

        string older = Path.Combine(root, "older.wayscrawl");
        await Demo.CreateAsync(older);
        string olderManifest = Path.Combine(older, "manifest.json");
        var oldBundle = (await BundleStore.ReadAsync(older)) with { CreatedUtc = demo.CreatedUtc.AddDays(-1) };
        await File.WriteAllTextAsync(olderManifest, System.Text.Json.JsonSerializer.Serialize(oldBundle, BundleStore.Json));
        Directory.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(1));
        h.Check(await BundleStore.LatestAsync(root) == output, "Latest uses manifest creation time, not directory time.");

        string damaged = Path.Combine(root, "damaged.wayscrawl");
        Directory.CreateDirectory(damaged);
        await File.WriteAllTextAsync(Path.Combine(damaged, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(
            demo with { Geometries = [demo.Geometries[0] with { LogicalWidth = 0 }] }, BundleStore.Json));
        h.Check(await BundleStore.LatestAsync(root) == output, "A semantically damaged bundle cannot break latest.");

        // Media is linked or copied, never moved, and must match its manifest digest.
        string media = Path.Combine(root, "source.mkv");
        await File.WriteAllBytesAsync(media, Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray());
        var (bytes, sha) = await BundleStore.HashAsync(media, CancellationToken.None);
        var sample = CoreChecks.SampleBundle();
        var withMedia = sample with { Media = [sample.Media[0] with { Bytes = bytes, Sha256 = sha }] };
        var images = new Dictionary<string, byte[]>
        {
            ["frames/frame-00.png"] = await File.ReadAllBytesAsync(Path.Combine(output, "frames/frame-00.png")),
            ["frames/frame-01.png"] = await File.ReadAllBytesAsync(Path.Combine(output, "frames/frame-01.png")),
            ["frames/frame-01-detail.png"] = await File.ReadAllBytesAsync(Path.Combine(output, "frames/frame-02.png")),
            [Bundle.ContactSheet] = await File.ReadAllBytesAsync(Path.Combine(output, "contact-sheet.png"))
        };
        string published = Path.Combine(root, "media.wayscrawl");
        await BundleStore.WriteAsync(published, withMedia, images, new Dictionary<string, string> { ["media/recording.mkv"] = media });
        var read = await BundleStore.ReadAsync(published);
        h.Check(File.Exists(media) && new FileInfo(Path.Combine(published, "media/recording.mkv")).Length == bytes &&
                read.Media[0].Sha256 == sha, "Media published with a verified digest; the original is kept.");
        await h.RejectAsync<InvalidDataException>(() => BundleStore.WriteAsync(Path.Combine(root, "bad.wayscrawl"),
            withMedia with { Media = [withMedia.Media[0] with { Sha256 = new string('0', 64) }] }, images,
            new Dictionary<string, string> { ["media/recording.mkv"] = media }), "Digest mismatch refuses publication.");
        h.Check(!Directory.Exists(Path.Combine(root, "bad.wayscrawl")), "Failed publication leaves nothing behind.");
        await h.RejectAsync<InvalidDataException>(() => BundleStore.WriteAsync(Path.Combine(root, "missing.wayscrawl"),
            withMedia, images), "Media described but not supplied is refused.");
        var extra = new Dictionary<string, byte[]>(images) { ["frames/extra.png"] = images[Bundle.ContactSheet] };
        await h.RejectAsync<InvalidDataException>(() => BundleStore.WriteAsync(Path.Combine(root, "extra.wayscrawl"),
            withMedia, extra, new Dictionary<string, string> { ["media/recording.mkv"] = media }), "Unlisted images are refused.");
    }

    public static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new CheckFailed("Timed out waiting for a condition.");
            await Task.Delay(20);
        }
    }

    private static byte[] DecodeFixture(string path, int width, int height)
    {
        byte[] png = File.ReadAllBytes(path);
        if (!png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) != width ||
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) != height)
            throw new CheckFailed("Unexpected PNG header.");
        using var compressed = new MemoryStream();
        int cursor = 8;
        while (cursor + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(cursor, 4));
            if (length < 0 || length > png.Length - cursor - 12) throw new CheckFailed("Bad PNG chunk.");
            if (png.AsSpan(cursor + 4, 4).SequenceEqual("IDAT"u8))
                compressed.Write(png, cursor + 8, length);
            cursor += length + 12;
        }
        compressed.Position = 0;
        using var decoder = new ZLibStream(compressed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        decoder.CopyTo(raw);
        byte[] bytes = raw.ToArray();
        if (bytes.Length != (1 + width * 3) * height) throw new CheckFailed("Invalid decoded size.");
        return bytes;
    }
}
