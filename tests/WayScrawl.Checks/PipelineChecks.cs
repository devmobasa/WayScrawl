using SkiaSharp;
using WayScrawl.Application;
using WayScrawl.Core;
using WayScrawl.Platform;
using WayScrawl.Platform.Media;
using WayScrawl.Rendering;

namespace WayScrawl.Checks;

internal static class PipelineChecks
{
    private sealed class FakeTranscriber : ITranscriber
    {
        public Task<TranscriptionResult> TranscribeAsync(string audioPath, CancellationToken cancellationToken) => Task.FromResult(
            new TranscriptionResult(
                [
                    new(500_000, 1_000_000, " Look at this bar.", TimingQuality.MeasuredWord,
                        [
                            new(500_000, 600_000, "Look"), new(600_000, 700_000, "at"), new(700_000, 800_000, "this"),
                            new(800_000, 1_000_000, "bar.")
                        ]),
                    new(1_100_000, 1_200_000, " .", TimingQuality.MeasuredSegment)
                ],
                new("ok", "fake", "none", "none", "en", "", "fixture")));
    }

    private sealed class LossyTranscriber : ITranscriber
    {
        public Task<TranscriptionResult> TranscribeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new TranscriptionResult([new(500_000, 1_000_000, "猫が見える", TimingQuality.MeasuredWord,
                [new(500_000, 1_000_000, "���が見える")])], new("ok", "fixture", "none", "none", "ja", "", "split UTF-8 token fixture")));
    }

    public static async Task RunAsync(Harness h)
    {
        await CliChecks.WhisperAsync(h);
        h.Section = "pipeline";
        string root = h.TempDirectory("pipeline");
        var paths = new WayScrawlPaths(Path.Combine(root, "run"), Path.Combine(root, "state"), Path.Combine(root, "data"),
            Path.Combine(root, "config"));
        var config = new WayScrawlConfig(Notifications: false, CopyPathToClipboard: false, FrameLongEdge: 1920);

        // The red bar is burned into the video from 1.0 s, where stroke s1 is drawn.
        string captured = await SessionAsync(root, "captured", burnInk: true);
        var captureRecord = EvidenceBuilder.ReadRecord(captured);
        EvidenceBuilder.WriteRecord(captured, captureRecord with { WindowTitles = false, InkFadeSeconds = 1.5 });
        using (var journal = new JournalWriter(Path.Combine(captured, SessionRecord.JournalName)))
        {
            journal.Event(new(4_500_000, "window.focus", "firefox: secret capture title"));
            journal.Event(new(4_600_000, "window.open", "firefox: secret open title"));
            journal.Event(new(4_700_000, "window.layout", System.Text.Json.JsonSerializer.Serialize(new[] {
                new WindowSnapshot("window", "firefox", new(0, 0, 640, 360), true, "secret layout title") }, WayScrawlJson.Journal)));
        }
        // Processing settings deliberately disagree with the recorded policy.
        string bundlePath =
            await new EvidenceBuilder(paths, config with { WindowTitles = true, InkFadeSeconds = 10 }, new FakeTranscriber())
                .BuildAsync(captured, CancellationToken.None);
        var bundle = await BundleStore.ReadAsync(bundlePath);
        h.Check(bundle.Composition.Mode == CompositionMode.CapturedInk,
            "Ink in the pixels is detected as CapturedInk: " + bundle.Composition.Basis);
        var annotation = bundle.Keyframes.First(k => k.Reason == "Annotation s1");
        h.Check(annotation.AtUs >= 1_200_000 && annotation.AtUs <= 1_600_000, $"Annotation frame after drawing ({annotation.AtUs}).");
        h.Check(annotation.Crop is { } crop && crop.X <= 100 && crop.X + crop.Width >= 300 && crop.Y <= 100,
            "Detail crop contains the stroke in source pixels.");
        var undone = bundle.Keyframes.First(k => k.Reason == "Annotation s2");
        h.Check(undone.AtUs >= 4_050_000 && undone.AtUs < 4_200_000, $"Undone stroke is captured before its removal ({undone.AtUs}).");
        h.Check(bundle.Keyframes.Any(k => k.Reason.StartsWith("Marker", StringComparison.Ordinal)), "Marker frame present.");
        // The settled scene change is detected; it looks like the later s2 frame, so it is kept as an omission.
        h.Check(bundle.Keyframes.Any(k => k.Reason.StartsWith("Screen changed", StringComparison.Ordinal)) ||
                bundle.Omissions.Any(o => o.Reason == "duplicate" && o.Detail.StartsWith("Screen changed", StringComparison.Ordinal)),
            "Scene change detected and accounted for.");
        h.Check(bundle.Keyframes.All(k => k.PtsUs is not null && k.MediaId == "m0"), "Frames record their decoded PTS.");
        h.Check(bundle.Transcript.Length == 1 && bundle.Transcript[0].Text == "Look at this bar." &&
                    Math.Abs(bundle.Transcript[0].StartUs - 500_000) <= 40_000,
            "Transcript maps audio time to session time and drops punctuation-only segments " +
            $"({bundle.Transcript.FirstOrDefault()?.StartUs}).");
        var video = bundle.Media.Single().Streams.Single(s => s.Kind == "video");
        h.Check(video.Clock.Quality == ClockQuality.Measured && video.Clock.OffsetUs == -video.FirstPtsUs,
            "First frame maps to session zero.");
        var (_, sha) = await BundleStore.HashAsync(Path.Combine(bundlePath, "media/recording.mkv"), CancellationToken.None);
        h.Check(sha == bundle.Media[0].Sha256 && File.Exists(Path.Combine(captured, "recording.mkv")),
            "Bundle media digest verified; session media retained.");
        using (var frame = SKBitmap.Decode(Path.Combine(bundlePath, annotation.Path)))
            h.Check(IsInk(frame.GetPixel(200, 104)), "Captured ink is visible in the exported frame.");
        h.Check(EvidenceBuilder.ReadRecord(captured) is { State: "published", Bundle: { } b } && b == bundlePath,
            "Session record points at its bundle.");
        string summary = await File.ReadAllTextAsync(Path.Combine(bundlePath, "summary.md"));
        h.Check(summary.Contains("detail crop") && summary.Contains("Look at this bar") && summary.Contains("CapturedInk"),
            "Summary lists crops, narration and composition.");
        h.Check(!bundle.WindowTitles && bundle.Events.Any(e => e.Kind == "window.focus" && e.Detail == "firefox") &&
                    bundle.Events.Any(e => e.Kind == "window.open" && e.Detail == "firefox") &&
                    WindowLayout.At(bundle.Events, 4_700_000).All(w => w.Title is null) && !summary.Contains("secret capture title") &&
                    !summary.Contains("secret open title") && !summary.Contains("secret layout title") &&
                    !PasteText.Compose(bundle, bundlePath).Contains("secret capture title"),
            "Processing honors captured title privacy across events, keyframe reasons, summary and paste.");
        await h.RejectAsync<IOException>(() => new EvidenceBuilder(paths, config, null).BuildAsync(captured, CancellationToken.None),
            "Reprocessing never overwrites a published bundle.");

        string existingFrame = Path.Combine(root, "existing-frame.png");
        await File.WriteAllTextAsync(existingFrame, "keep");
        var frameRefusal = await ProcessRunner.RunAsync(CliChecks.Dotnet, [CliChecks.Cli, "frame", bundlePath, "1.0", existingFrame],
            CancellationToken.None, environment: new Dictionary<string, string> { ["XDG_RUNTIME_DIR"] = paths.Runtime });
        string linkedFrame = Path.Combine(root, "linked-frame.png");
        File.CreateSymbolicLink(linkedFrame, existingFrame);
        var linkRefusal = await ProcessRunner.RunAsync(CliChecks.Dotnet, [CliChecks.Cli, "frame", bundlePath, "1.0", linkedFrame],
            CancellationToken.None, environment: new Dictionary<string, string> { ["XDG_RUNTIME_DIR"] = paths.Runtime });
        h.Check(frameRefusal.ExitCode != 0 && linkRefusal.ExitCode != 0 && await File.ReadAllTextAsync(existingFrame) == "keep",
            "Explicit frame output preserves existing files and refuses symlink targets.");

        string frameDirectory = Path.Combine(root, "frame-output");
        Directory.CreateDirectory(frameDirectory);
        var frameResults = new List<(string Id, bool Safe, string Output)>();
        try
        {
            foreach (string id in new[] { Path.Combine(root, "escaped"), "../x" })
            {
                await File.WriteAllTextAsync(Path.Combine(bundlePath, "manifest.json"),
                    System.Text.Json.JsonSerializer.Serialize(bundle with { Id = id }, BundleStore.Json));
                var result = await ProcessRunner.RunAsync("bash",
                    ["-c", "cd -- \"$1\"; shift; exec \"$@\"", "frame-check", frameDirectory,
                        CliChecks.Dotnet, CliChecks.Cli, "frame", bundlePath, "1.0"], CancellationToken.None,
                    environment: new Dictionary<string, string> { ["XDG_RUNTIME_DIR"] = paths.Runtime });
                string output = result.Text.Split(" (session time", StringSplitOptions.None)[0].Trim();
                frameResults.Add((id, result.ExitCode == 0 && Path.GetDirectoryName(output) == frameDirectory &&
                    File.Exists(output), result.Text + result.Error));
            }
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(bundlePath, "manifest.json"),
                System.Text.Json.JsonSerializer.Serialize(bundle, BundleStore.Json));
        }
        h.Check(frameResults.All(result => result.Safe),
            "Default frame outputs stay directly in the working directory for absolute and ../x IDs: " +
            string.Join("; ", frameResults.Select(result => $"{result.Id}: {result.Output.Trim()}")));

        var zoom = await ZoomBuilder.BuildAsync(bundlePath, "0:03", span: 2, frames: 8, full: true);
        h.Check(zoom.TimesUs.Length == 8 && zoom.TimesUs[0] >= 2_500_000 && zoom.TimesUs[^1] < 4_500_000,
            "Zoom samples one bounded window with decoded session times.");
        string extraction48 = Path.Combine(root, "window-48");
        Directory.CreateDirectory(extraction48);
        var manyFrames = await Ffmpeg.ExtractWindowAsync(
            Path.Combine(captured, "recording.mkv"), 0, 4_000_000, 0, 48, extraction48, CancellationToken.None);
        h.Check(manyFrames.Length == 48 && manyFrames.Zip(manyFrames.Skip(1)).All(p => p.First.PtsUs < p.Second.PtsUs),
            "Window extraction retains actual timestamps for all 48 selected frames.");
        string stillVideo = Path.Combine(root, "static-low-rate.mkv");
        await ProcessRunner.RunCheckedAsync("ffmpeg",
            [
                "-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=blue:size=64x64:rate=1:duration=2", "-c:v", "libx264", "-preset",
                "ultrafast", stillVideo
            ],
            CancellationToken.None);
        string stillFrames = Path.Combine(root, "static-window");
        Directory.CreateDirectory(stillFrames);
        var stillWindow = await Ffmpeg.ExtractWindowAsync(stillVideo, 200_000, 300_000, 0, 12, stillFrames, CancellationToken.None);
        using (var stillImage = stillWindow.Length == 1 ? SKBitmap.Decode(stillWindow[0].Path) : null)
            h.Check(stillWindow.Length == 1 && stillWindow[0].PtsUs == 0 && stillWindow[0].PresentationPtsUs == 200_000 &&
                        stillImage is not null && stillImage.GetPixel(32, 32).Blue > 220,
                "A tiny interval on static low-rate video returns the preceding visible frame with its true PTS.");
        await SparseZoom(h, root, stillVideo, paths.Runtime);
        int fullHeight;
        using (var image = SKBitmap.Decode(zoom.Path)) fullHeight = image.Height;
        var croppedZoom = await ZoomBuilder.BuildAsync(bundlePath, "0:03", span: 2, frames: 8);
        using (var image = SKBitmap.Decode(croppedZoom.Path))
            h.Check(image.Height != fullHeight, "Zoom uses the nearest detail crop unless full is requested.");
        var phraseZoom = await ZoomBuilder.BuildAsync(bundlePath, "look AT, this bar!", frames: 4);
        h.Check(phraseZoom.Matched?.Trim() == "Look at this bar." && phraseZoom.Narration.Length == 1,
            "Zoom reports the matched narration and nearby speech.");
        await File.WriteAllTextAsync(Path.Combine(bundlePath, MediaFiles.RemovedNote), "{}");
        await h.RejectAsync<InvalidDataException>(
            () => ZoomBuilder.BuildAsync(bundlePath, "0:01"), "Retired media refuses zoom with a retention diagnostic.");
        File.Delete(Path.Combine(bundlePath, MediaFiles.RemovedNote));
        var offsetBundle = bundle with { Media = [bundle.Media[0] with { Streams = bundle.Media[0].Streams.Select(s =>
            s.Kind == "video" ? s with { Clock = s.Clock with { OffsetUs = s.Clock.OffsetUs + 500_000 } } : s).ToArray() }] };
        string manifest = Path.Combine(bundlePath, "manifest.json");
        string originalManifest = await File.ReadAllTextAsync(manifest);
        await File.WriteAllTextAsync(manifest, System.Text.Json.JsonSerializer.Serialize(offsetBundle, BundleStore.Json));
        var shiftedZoom = await ZoomBuilder.BuildAsync(bundlePath, "0:03", span: 2, frames: 4, full: true);
        using (var image = SKBitmap.Decode(shiftedZoom.Path))
        {
            // The fixture turns its lower rectangle white at media 2.8 s: with +0.5 s
            // session offset it must change at session 3.3 s, between cells 2 and 3.
            // Contact-sheet cells are 640x360, three columns, with 8px gaps and 28px labels.
            var before = image.GetPixel(8 + 648 + 400, 8 + 250);
            var after = image.GetPixel(8 + 2 * 648 + 400, 8 + 250);
            h.Check(before.Red < 100 && after.Red > 220 && shiftedZoom.TimesUs[1] < 3_300_000 && shiftedZoom.TimesUs[2] >= 3_300_000,
                "Zoom clock conversion puts the real pixel transition between session-time 3.0 and 3.5 cells.");
        }
        await File.WriteAllTextAsync(manifest, originalManifest);

        // Without ink in the pixels, the exporter composites the vectors itself.
        string clean = await SessionAsync(root, "clean", burnInk: false);
        using (var fadeJournal = new JournalWriter(Path.Combine(clean, SessionRecord.JournalName)))
            fadeJournal.Removed(2_500_000, ["s1"], "fade");
        string cleanBundle = await new EvidenceBuilder(paths, config, null).BuildAsync(clean, CancellationToken.None);
        var cleanRead = await BundleStore.ReadAsync(cleanBundle);
        h.Check(cleanRead.Composition.Mode == CompositionMode.CleanSource, "Missing ink is detected as CleanSource.");
        h.Check(cleanRead.Transcription?.Status == "disabled" && cleanRead.Warnings.Any(w => w.Contains("re-rendered")),
            "No transcriber is reported, and re-rendered ink is disclosed.");
        var cleanFrame = cleanRead.Keyframes.First(k => k.Reason == "Annotation s1");
        using (var frame = SKBitmap.Decode(Path.Combine(cleanBundle, cleanFrame.Path)))
            h.Check(IsInk(frame.GetPixel(200, 104)), "Vector ink is rendered onto clean frames.");
        var markerFrame = cleanRead.Keyframes.First(k => k.Reason.StartsWith("Marker", StringComparison.Ordinal));
        using (var frame = SKBitmap.Decode(Path.Combine(cleanBundle, markerFrame.Path)))
            h.Check(IsInk(frame.GetPixel(200, 104)) && !IsInk(frame.GetPixel(200, 154)),
                "Rendering follows visibility: earlier ink drawn, later ink not yet.");

        h.Check(cleanRead.Strokes.Single(s => s.Id == "s1").RemovedUs == 2_500_000 && cleanFrame.AtUs < 2_500_000,
            "Fade composition is detected on the pre-removal annotation frame.");
        foreach (bool capturedPins in new[] { false, true })
        {
            string pinSession = await SessionAsync(root, "pins-" + capturedPins, burnInk: false);
            string pinJournal = Path.Combine(pinSession, SessionRecord.JournalName);
            await File.WriteAllTextAsync(pinJournal, "");
            var pin = new Pin("p1", "g0", 1, 200, 100, 1_000_000, 3_000_000);
            using (var writer = new JournalWriter(pinJournal))
            {
                writer.Event(new(capturedPins ? 900_000 : 800_000, "marker", "before pin"));
                writer.Pin(pin);
            }
            if (capturedPins)
            {
                string png = Path.Combine(root, "pin.png");
                using var image = new SKBitmap(640, 360);
                using (var canvas = new SKCanvas(image)) { canvas.Clear(SKColors.Transparent); InkRenderer.DrawPin(canvas, pin); }
                await File.WriteAllBytesAsync(png, Imaging.EncodePng(image));
                string source = Path.Combine(pinSession, "recording.mkv"), temporary = Path.Combine(pinSession, "ink.mkv");
                await ProcessRunner.RunCheckedAsync("ffmpeg",
                    [
                        "-nostdin", "-v", "error", "-i", source, "-i", png, "-filter_complex",
                        "[0:v][1:v]overlay=0:0:enable='between(t,1,3)'", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "copy",
                        temporary
                    ],
                    CancellationToken.None);
                File.Move(temporary, source, overwrite: true);
            }
            string pinPath = await new EvidenceBuilder(paths, config, null).BuildAsync(pinSession, CancellationToken.None);
            var pinBundle = await BundleStore.ReadAsync(pinPath);
            h.Check(pinBundle.Pins.Length == 1 &&
                        pinBundle.Composition.Mode == (capturedPins ? CompositionMode.CapturedInk : CompositionMode.CleanSource),
                "Pin-only session detects composition: " + pinBundle.Composition.Mode);
            var pinFrame = pinBundle.Keyframes.First(k => k.Reason.Contains("Pin 1"));
            using var decoded = SKBitmap.Decode(Path.Combine(pinPath, pinFrame.Path));
            int ring = Enumerable.Range(209, 4).Max(x => decoded.GetPixel(x, 100).Red);
            h.Check(ring > 160, $"Pin-only exported frame contains the white pin ring (captured={capturedPins}, red={ring}).");
        }

        var cleanZoom = await ZoomBuilder.BuildAsync(cleanBundle, "0:01.8", span: 1, frames: 4, full: true);
        using (var image = SKBitmap.Decode(cleanZoom.Path))
            h.Check(IsInk(image.GetPixel(208, 112)), "Zoom burns visible vectors into CleanSource cells.");
        var partialStrokeZoom = await ZoomBuilder.BuildAsync(cleanBundle, "0:01.05", span: 0.2, frames: 2, full: true);
        using (var image = SKBitmap.Decode(partialStrokeZoom.Path))
            h.Check(!IsInk(image.GetPixel(308, 112)) && IsInk(image.GetPixel(108, 112)),
                "Zoom shows only stroke points recorded by each cell time.");
        string unicodeSession = await SessionAsync(root, "unicode-words", burnInk: false);
        string unicodePath =
            await new EvidenceBuilder(paths, config, new LossyTranscriber()).BuildAsync(unicodeSession, CancellationToken.None);
        var unicode = await BundleStore.ReadAsync(unicodePath);
        h.Check(unicode.Transcript.Single() is { Text : "猫が見える", Words : null, Timing : TimingQuality.MeasuredSegment } &&
                    string.Join(' ', Moments.Compose(unicode)).Contains("猫が見える"),
            "Lossy UTF-8 token fragments cannot replace intact segment narration.");
        string shiftedAudio = await SessionAsync(root, "word-offset", burnInk: false);
        var shiftedRecord = EvidenceBuilder.ReadRecord(shiftedAudio);
        EvidenceBuilder.WriteRecord(shiftedAudio, shiftedRecord with { Recording = shiftedRecord.Recording! with
            { AudioClock = shiftedRecord.Recording.AudioClock! with { OffsetUs = -700_000 } } });
        var shiftedPath = await new EvidenceBuilder(paths, config, new FakeTranscriber()).BuildAsync(shiftedAudio, CancellationToken.None);
        var speech = (await BundleStore.ReadAsync(shiftedPath)).Transcript.Single();
        h.Check(speech is { Words : null, Timing : TimingQuality.MeasuredSegment, StartUs : 0, Text : "Look at this bar." } &&
                    speech.EndUs < 310_000 &&
                    string.Join(' ', Moments.Compose(await BundleStore.ReadAsync(shiftedPath))).Contains("Look at this bar."),
            "Clipping away words falls back to intact segment narration without inventing partial text.");

        string delayedVideo = await SessionAsync(root, "video-stream-offset", burnInk: false);
        string originalVideo = Path.Combine(delayedVideo, "recording.mkv"), shiftedVideo = Path.Combine(delayedVideo, "shifted.mkv");
        await ProcessRunner.RunCheckedAsync("ffmpeg",
            [
                "-nostdin", "-v", "error", "-i", originalVideo, "-itsoffset", "1", "-i", originalVideo, "-map", "1:v:0", "-map", "0:a:0",
                "-c", "copy", "-avoid_negative_ts", "disabled", shiftedVideo
            ],
            CancellationToken.None);
        File.Move(shiftedVideo, originalVideo, overwrite: true);
        await File.WriteAllTextAsync(Path.Combine(delayedVideo, SessionRecord.JournalName), "");
        var delayedProbe = await Ffmpeg.ProbeAsync(originalVideo, CancellationToken.None);
        h.Check(
            delayedProbe.VideoStartUs - delayedProbe.FormatStartUs > 900_000, "Scene fixture has independent video and container starts.");
        string delayedPath = await new EvidenceBuilder(paths, config, null).BuildAsync(delayedVideo, CancellationToken.None);
        var delayedBundle = await BundleStore.ReadAsync(delayedPath);
        h.Check(delayedBundle.Omissions.Any(
                    o => o.Detail.StartsWith("Screen changed", StringComparison.Ordinal) && o.AtUs is >= 2_800_000 and <= 3_500_000) ||
                    delayedBundle.Keyframes.Any(k => k.Reason.StartsWith("Screen changed", StringComparison.Ordinal) &&
                                                     k.RequestedUs is >= 2_800_000 and <= 3_500_000),
            "Scene samples map from the first video frame even when audio sets an earlier container start.");

        // A killed recorder leaves an unindexed container: recover it as an explicit partial result.
        string killed = await SessionAsync(root, "killed", burnInk: true);
        string killedVideo = Path.Combine(killed, "recording.mkv");
        using (var file = new FileStream(killedVideo, FileMode.Open)) file.SetLength(file.Length / 2);
        var killedRecord = EvidenceBuilder.ReadRecord(killed);
        EvidenceBuilder.WriteRecord(killed, killedRecord with { Recording = killedRecord.Recording! with { Finalized = false } });
        string partialPath = await new EvidenceBuilder(paths, config, null).BuildAsync(killed, CancellationToken.None);
        var partial = await BundleStore.ReadAsync(partialPath);
        h.Check(partial.Media[0].Finalization == "partial" && partial.Warnings.Any(w => w.Contains("remuxing")),
            "A truncated recording is recovered and labelled partial.");
        h.Check(partial.Keyframes.All(k => k.PtsUs <= 5_000_000) && File.Exists(killedVideo),
            "Recovered frames come from available media; the original is kept.");

        // Finalized media whose video ends before its audio: frames clamp to the last video packet.
        string shortVideo = await SessionAsync(root, "short-video", burnInk: false, videoSeconds: 1.2);
        string shortPath = await new EvidenceBuilder(paths, config, null).BuildAsync(shortVideo, CancellationToken.None);
        var shortBundle = await BundleStore.ReadAsync(shortPath);
        h.Check(shortBundle.Keyframes.All(k => k.PtsUs <= 1_250_000) && shortBundle.Warnings.Any(w => w.Contains("Video ends")),
            "Frames never pass the last video packet, and the gap is disclosed.");

        string windowSession = await SessionAsync(root, "window-crop", burnInk: false);
        await File.WriteAllTextAsync(Path.Combine(windowSession, SessionRecord.JournalName), "");
        EvidenceBuilder.WriteRecord(windowSession, EvidenceBuilder.ReadRecord(windowSession) with { WindowTitles = true });
        using (var writer = new JournalWriter(Path.Combine(windowSession, SessionRecord.JournalName)))
        {
            writer.Event(new(0, "window.layout",
                System.Text.Json.JsonSerializer.Serialize(
                    new[] { new WindowSnapshot("window", "editor", new(100, 80, 200, 120), true, "retained layout title") },
                    WayScrawlJson.Journal)));
            writer.Event(new(0, "window.focus", "editor: retained by capture consent"));
        }
        string windowPath = await new EvidenceBuilder(paths, config, null).BuildAsync(windowSession, CancellationToken.None);
        var windowBundle = await BundleStore.ReadAsync(windowPath);
        h.Check(windowBundle.WindowTitles && windowBundle.Events.Any(e => e.Detail == "editor: retained by capture consent") &&
                    WindowLayout.At(windowBundle.Events, 0).Single().Title == "retained layout title",
            "A captured title opt-in survives processing with titles disabled.");
        h.Check(
            windowBundle.Keyframes.All(k => k.Crop is { Width : < 300, Height : < 200 }) && windowBundle.Keyframes.All(k => k.Width == 640),
            "Without ink, focused-window detail crops are exported while full frames remain in the bundle.");

        string exportPath = Path.Combine(root, "shared.mp4");
        var exported = await VideoExporter.ExportAsync(bundlePath, exportPath, "0,0,401,201", 300, paths.Runtime);
        var exportProbe = await Ffmpeg.ProbeAsync(exportPath, CancellationToken.None);
        h.Check(exportProbe.Width == 300 && exportProbe.Height == 150 && exportProbe.AudioCodec == "aac" && exported.Warning is null,
            "MP4 export keeps AAC audio and applies an even crop/size without re-rendering captured ink.");
        await h.RejectAsync<IOException>(
            () => VideoExporter.ExportAsync(bundlePath, exportPath, runtime: paths.Runtime), "Export preserves an existing output file.");
        var cleanExport = await VideoExporter.ExportAsync(cleanBundle, Path.Combine(root, "clean.mp4"), "full", 1920, paths.Runtime);
        h.Check(cleanExport.Width == 640 && cleanExport.Height == 360 && cleanExport.Warning?.Contains("without vector ink") == true,
            "CleanSource export keeps original size and discloses missing vector ink.");
        await File.WriteAllTextAsync(Path.Combine(bundlePath, MediaFiles.RemovedNote), "{}");
        await h.RejectAsync<InvalidDataException>(
            () => VideoExporter.ExportAsync(bundlePath, Path.Combine(root, "retired.mp4"), runtime: paths.Runtime),
            "Retired source media refuses MP4 export.");
        File.Delete(Path.Combine(bundlePath, MediaFiles.RemovedNote));
        string tools = Path.Combine(root, "fake-encoder-tools");
        Directory.CreateDirectory(tools);
        string encoderLog = Path.Combine(root, "encoder-attempts.txt"), wrapper = Path.Combine(tools, "ffmpeg");
        await File.WriteAllTextAsync(wrapper, """
            #!/bin/sh
            case "$*" in *-encoders*) printf ' V....D h264_nvenc unavailable\n'; exit 0;; esac
            for encoded_output in "$@"; do :; done
            for arg in "$@"; do
              if [ "$arg" = h264_nvenc ]; then
                printf 'partial nvenc output' > "$encoded_output"
                printf 'nvenc-failed\n' >> "$WAYSCRAWL_TEST_FFMPEG_LOG"
                exit 1
              fi
              if [ "$arg" = libx264 ]; then
                if [ -e "$encoded_output" ]; then
                  printf 'partial output was not removed\n' >&2
                  exit 1
                fi
                printf 'software-retry\n' >> "$WAYSCRAWL_TEST_FFMPEG_LOG"
              fi
            done
            exec /usr/bin/ffmpeg "$@"
            """);
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string fallback = Path.Combine(root, "fallback.mp4");
        var fallbackResult = await ProcessRunner.RunCheckedAsync(CliChecks.Dotnet,
            [CliChecks.Cli, "export", bundlePath, "--out", fallback, "--crop", "full", "--size", "320"], CancellationToken.None,
            new Dictionary<string, string> { ["PATH"] = tools + ":/usr/bin:/bin", ["WAYSCRAWL_TEST_FFMPEG_LOG"] = encoderLog,
                ["XDG_RUNTIME_DIR"] = Path.Combine(root, "fallback-run") });
        h.Check(await File.ReadAllTextAsync(encoderLog) == "nvenc-failed\nsoftware-retry\n" &&
            (await Ffmpeg.ProbeAsync(fallback, CancellationToken.None)).Width == 320 && fallbackResult.Text.Contains("libx264"),
            "CLI export retries NVENC initialization failure with the real software encoder and publishes a valid MP4.");
        await RetentionChecks.RunAsync(h, Path.Combine(captured, "recording.mkv"));

        h.Section = "render";
        using (var canvasBitmap = new SKBitmap(64, 64))
        {
            using (var canvas = new SKCanvas(canvasBitmap))
            {
                canvas.Clear(SKColors.Black);
                InkRenderer.Draw(canvas, new Stroke("t", "g0", "#00ff00", 8, [new(0, 32, 32)]));
            }
            h.Check(canvasBitmap.GetPixel(32, 32) == SKColors.Lime, "A tap renders a dot.");
        }
        var sheetFrames = new List<(SKBitmap, string)> { (new SKBitmap(320, 180), "a"), (new SKBitmap(320, 180), "b") };
        using (var sheet = SKBitmap.Decode(ContactSheet.Compose(sheetFrames, cellWidth: 200)))
            h.Check(sheet.Width == 2 * 200 + 3 * 8, "Contact sheet lays out labelled cells.");
        foreach (var (image, _) in sheetFrames) image.Dispose();
    }

    private static async Task SparseZoom(Harness h, string root, string video, string runtime)
    {
        string directory = Path.Combine(root, "sparse-zoom.wayscrawl");
        await Demo.CreateAsync(directory);
        var demo = await BundleStore.ReadAsync(directory);
        Directory.CreateDirectory(Path.Combine(directory, "media"));
        File.Copy(video, Path.Combine(directory, "media/recording.mkv"));
        var (bytes, hash) = await BundleStore.HashAsync(video, CancellationToken.None);
        foreach (long offset in new[] { 0L, 500_000L })
        {
            var media = CoreChecks.SampleBundle().Media[0] with
            {
                Bytes = bytes, Sha256 = hash,
                Streams = [new("video", "h264", "1/1000", 0, 2_000_000,
                    new(offset, ClockQuality.Measured, "fixture", "sparse fixture", 0), 64, 64, 1)]
            };
            var bundle = demo with
            {
                DurationUs = 2_000_000, Geometries = [new("g0", 0, 0, 64, 64, 64, 64)], Media = [media],
                Composition = new(CompositionMode.CleanSource, "fixture: clean blue video"), Strokes = [], Events = [],
                Transcript = [], Keyframes = [demo.Keyframes[0]], Pins = [new("gap-pin", "g0", 1, 32, 32, offset + 100_000)]
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"),
                System.Text.Json.JsonSerializer.Serialize(bundle, BundleStore.Json));
            string At(long us) => (us / 1_000_000d).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
            var before = await ZoomBuilder.BuildAsync(directory, At(offset + 75_000), span: 0.1, frames: 1, full: true, runtime: runtime);
            var after = await ZoomBuilder.BuildAsync(directory, At(offset + 225_000), span: 0.1, frames: 1, full: true, runtime: runtime);
            using var beforeImage = SKBitmap.Decode(before.Path);
            using var afterImage = SKBitmap.Decode(after.Path);
            // Cells scale 64 source pixels to 640 with an 8px border. Sample inside the
            // dark pin disk, away from its white numeral and antialiased outer ring.
            var absent = beforeImage.GetPixel(8 + 260, 8 + 320);
            var present = afterImage.GetPixel(8 + 260, 8 + 320);
            h.Check(absent.Blue > 220 && present.Blue < 80 && present.Red < 80,
                "A zoom inside a sparse-video gap renders the pin at presentation time, with clock offset " + offset + ".");
            h.Check(after.TimesUs is [var time] && time == offset + 200_000 && after.SourcePtsUs is [0],
                "A sparse zoom reports the reconstructed presentation time rather than the earlier decoded PTS.");
        }
    }

    private static bool IsInk(SKColor c) => c.Red > 200 && c.Green < 110 && c.Blue < 110;

    private static async Task<string> SessionAsync(string root, string name, bool burnInk, double videoSeconds = 5)
    {
        string directory = Path.Combine(root, "state", "sessions", name);
        Directory.CreateDirectory(directory);
        string video = Path.Combine(directory, "recording.mkv");
        string ink = burnInk ? ",drawbox=x=100:y=100:w=200:h=9:color=0xff3b30:t=fill:enable='between(t,1.0,99)'" : "";
        await ProcessRunner.RunCheckedAsync("ffmpeg", ["-nostdin", "-v", "error", "-y",
            "-f", "lavfi", "-i", FormattableString.Invariant($"color=c=0x202830:size=640x360:rate=30:duration={videoSeconds}"),
            "-f", "lavfi", "-i", "sine=frequency=440:duration=5",
            "-vf", "drawbox=x=0:y=200:w=640:h=160:color=white:t=fill:enable='gte(t,2.8)'" + ink,
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv444p", "-c:a", "libopus", video],
            CancellationToken.None);
        var source = new CaptureSource("test", new Geometry("g0", 0, 0, 640, 360, 640, 360, "monitor:TEST"),
            CompositionMode.Unknown, "synthetic");
        var clock = new ClockMap(0, ClockQuality.Measured, "CLOCK_MONOTONIC", "fixture: first frame at session zero", 33_333);
        EvidenceBuilder.WriteRecord(directory, new SessionRecord(name, DateTimeOffset.UtcNow, "recorded", "test", source, 0,
            "recording.mkv", null, 4_900_000, new RecordingResult(video, true, clock, clock with { Quality = ClockQuality.Estimated }, []),
            [], []));
        using var journal = new JournalWriter(Path.Combine(directory, SessionRecord.JournalName));
        journal.Stroke(new("s1", "g0", "#ff3b30", 9, [new(1_000_000, 100, 104), new(1_100_000, 200, 104), new(1_200_000, 300, 104)]));
        journal.Event(new(2_000_000, "marker", "check this", "coordinator", "exact", 1));
        journal.Stroke(new("s2", "g0", "#ff3b30", 9, [new(3_900_000, 100, 154), new(4_050_000, 300, 154)]));
        journal.Removed(4_200_000, ["s2"], "undo");
        return directory;
    }
}
