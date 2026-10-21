using System.Globalization;
using System.Text.Json;
using SkiaSharp;
using WayScrawl.Core;
using WayScrawl.Platform;
using WayScrawl.Platform.Media;
using WayScrawl.Rendering;

namespace WayScrawl.Application;

// Offline processing of one finished session directory into a published bundle.
// Runs in a worker process; the raw media stays the source of truth.
public sealed class EvidenceBuilder(WayScrawlPaths paths, WayScrawlConfig config, ITranscriber? transcriber,
    Action<string>? progress = null)
{
    private const string MediaPath = "media/recording.mkv";
    private const int SampleWidth = 160, SampleHeight = 90;
    private const double SampleFps = 4;

    public static SessionRecord ReadRecord(string sessionDirectory) =>
        JsonSerializer.Deserialize<SessionRecord>(File.ReadAllBytes(Path.Combine(sessionDirectory, SessionRecord.FileName)),
            WayScrawlJson.Options) ?? throw new InvalidDataException("Empty session record.");

    public static void WriteRecord(string sessionDirectory, SessionRecord record)
    {
        string path = Path.Combine(sessionDirectory, SessionRecord.FileName);
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(record, WayScrawlJson.Options));
        File.Move(temporary, path, overwrite: true);
    }

    public async Task<string> BuildAsync(string sessionDirectory, CancellationToken cancellationToken)
    {
        using var lease = OperationLease.Acquire(sessionDirectory, paths.Runtime);
        var record = ReadRecord(sessionDirectory);
        var recording = record.Recording ?? throw new InvalidDataException("The session has no finished recording.");
        var journal = SessionJournal.Read(Path.Combine(sessionDirectory, SessionRecord.JournalName));
        string video = Path.Combine(sessionDirectory, record.VideoFile);
        var warnings = new List<string>(record.Warnings);
        var capabilities = new List<Capability>(record.Capabilities);

        progress?.Invoke("Probing media");
        if (!recording.Finalized)
        {
            // A killed recorder leaves a container without a trustworthy index or duration;
            // rewrap whatever streams exist (stream copy) and describe the result as partial.
            progress?.Invoke("Recovering an unfinalized recording");
            string repaired = Path.Combine(sessionDirectory, "recording.recovered.mkv");
            File.Delete(repaired);
            await ProcessRunner.RunCheckedAsync(Ffmpeg.FfmpegTool,
                ["-nostdin", "-v", "error", "-i", video, "-map", "0", "-c", "copy", repaired], cancellationToken);
            video = repaired;
            warnings.Add("The recorder was terminated; the media was recovered by remuxing and may end early.");
        }
        MediaProbe probe = await Ffmpeg.ProbeAsync(video, cancellationToken);
        if (!recording.Finalized)
            warnings.Add("The recorder did not finalize normally; the media may be truncated.");
        long duration = Math.Max(0, record.DurationUs ?? probe.DurationUs);
        long frameUs = probe.FramesPerSecond > 0 ? (long)(1_000_000 / probe.FramesPerSecond) : 33_333;
        // The last video packet bounds every frame request; the container duration can include
        // audio that outlived the video, even in a cleanly finalized file.
        long lastVideoPts = await Ffmpeg.LastVideoPtsAsync(video, cancellationToken) ?? probe.VideoStartUs;

        // sessionUs = videoPtsUs + videoClock.OffsetUs. The backend measured the first frame.
        ClockMap videoClock = recording.VideoClock.Quality == ClockQuality.Unavailable
            ? new ClockMap(duration - probe.DurationUs - probe.VideoStartUs, ClockQuality.Estimated, "CLOCK_MONOTONIC",
                "Estimated from stop time minus media duration; no first-frame timestamp was available.", 500_000)
            : recording.VideoClock with { OffsetUs = recording.VideoClock.OffsetUs - probe.VideoStartUs };
        if (recording.VideoClock.Quality == ClockQuality.Unavailable)
            warnings.Add("Video timing is estimated (±500 ms); annotation alignment is approximate.");
        ClockMap? audioClock = recording.AudioClock is { } audio && probe.AudioCodec is not null
            ? audio with { OffsetUs = audio.OffsetUs - probe.VideoStartUs }
            : null;

        var geometry = record.Source.Geometry with { Id = "g0" };
        double layoutSx = probe.Width / (double)geometry.PixelWidth, layoutSy = probe.Height / (double)geometry.PixelHeight;
        var strokes = journal.Strokes;
        var pins = journal.Pins;
        if (geometry.PixelWidth != probe.Width || geometry.PixelHeight != probe.Height)
        {
            // Strokes were mapped to the expected output pixels; follow the actual negotiated size.
            double sx = probe.Width / (double)geometry.PixelWidth, sy = probe.Height / (double)geometry.PixelHeight;
            pins = pins.Select(p => p with { AtUs = Math.Min(p.AtUs, duration), RemovedUs = p.RemovedUs is { } removed ? Math.Min(removed, duration) : null }).ToArray();
        strokes = strokes.Select(s => s with
            {
                Width = s.Width * sx,
                Points = s.Points.Select(p => p with { X = p.X * sx, Y = p.Y * sy }).ToArray()
            }).ToArray();
            pins = pins.Select(p => p with { X = p.X * sx, Y = p.Y * sy }).ToArray();
            warnings.Add($"Recorded size {probe.Width}x{probe.Height} differs from the output's " +
                $"{geometry.PixelWidth}x{geometry.PixelHeight}; strokes were rescaled.");
            geometry = geometry with { PixelWidth = probe.Width, PixelHeight = probe.Height };
        }
        pins = pins.Select(p => p with { AtUs = Math.Min(p.AtUs, duration), RemovedUs = p.RemovedUs is { } removed ? Math.Min(removed, duration) : null }).ToArray();
        strokes = strokes.Select(s => Clamp(s, duration)).ToArray();
        var events = journal.Events.Select(e => record.WindowTitles ? e : WindowLayout.WithoutTitles(e))
            .Select(e => WindowLayout.Scale(e.AtUs > duration ? e with { AtUs = duration } : e, layoutSx, layoutSy)).ToArray();

        progress?.Invoke("Sampling frames");
        var samples = new List<FrameSample>();
        await foreach (var sample in Ffmpeg.SampleGrayAsync(video, SampleFps, SampleWidth, SampleHeight, cancellationToken))
        {
            long at = sample.AtUs + probe.VideoStartUs + videoClock.OffsetUs;
            if (at >= 0 && at <= duration) samples.Add(new(at, sample.Pixels));
        }

        var (transcript, transcription) = await TranscribeAsync(sessionDirectory, video, probe, audioClock, duration,
            warnings, cancellationToken);

        progress?.Invoke("Selecting keyframes");
        long mediaEnd = Math.Clamp(lastVideoPts + videoClock.OffsetUs, 0, duration);
        if (mediaEnd < duration - 1_000_000)
            warnings.Add($"Video ends at {mediaEnd / 1_000_000d:0.0} s of a {duration / 1_000_000d:0.0} s session; later events have no frames.");
        var plan = KeyframePlanner.Plan(mediaEnd, strokes, events, transcript, samples,
            new PlannerOptions(MaxFrames: Math.Max(3, config.MaxFrames), StartUs: probe.VideoStartUs + videoClock.OffsetUs,
                BurstGapUs: record.InkFadeSeconds is > 0 and var fade ? (long)(fade * 1_000_000) : 1_500_000), pins);

        progress?.Invoke($"Extracting {plan.Selected.Length} frames");
        var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var keyframes = new List<Keyframe>();
        var sheet = new List<(SKBitmap, string)>();
        Composition? composition = strokes.Length == 0 && pins.Length == 0
            ? new Composition(CompositionMode.NoInk, "No annotations were drawn.")
            : null;
        async Task<(SKBitmap Full, long Pts, long At)> ExtractAsync(FrameCandidate candidate)
        {
            long requestedPts = Math.Clamp(candidate.AtUs - videoClock.OffsetUs, probe.VideoStartUs, lastVideoPts);
            var (png, actualPts) = await Ffmpeg.ExtractPngAsync(video, requestedPts, probe.FormatStartUs, cancellationToken);
            long pts = actualPts ?? requestedPts;
            return (Imaging.Decode(png), pts, Math.Clamp(pts + videoClock.OffsetUs, 0, duration));
        }

        // Composition is decided before any frame is exported, from the first annotation frame.
        if (composition is null && plan.Selected.FirstOrDefault(c => strokes.Any(s => s.VisibleAt(c.AtUs)) || pins.Any(p => p.VisibleAt(c.AtUs))) is { } probeFrame)
        {
            var (full, _, at) = await ExtractAsync(probeFrame);
            using (full) composition = DetectComposition(full, strokes, at, pins, (float)(geometry.PixelWidth / geometry.LogicalWidth));
        }
        composition ??= new Composition(CompositionMode.Unknown, "No annotation frame was available to verify composition.");

        PixelRect? previousWindowCrop = null;
        try
        {
            for (int index = 0; index < plan.Selected.Length; index++)
            {
                var candidate = plan.Selected[index];
                var (full, pts, at) = await ExtractAsync(candidate);
                using var owned = full;
                if (composition.Mode == CompositionMode.CleanSource)
                    using (var canvas = new SKCanvas(full))
                    {
                        foreach (var stroke in strokes.Where(s => s.VisibleAt(at))) InkRenderer.DrawAt(canvas, stroke, at);
                        foreach (var pin in pins.Where(p => p.VisibleAt(at)))
                            InkRenderer.DrawPin(canvas, pin, (float)(geometry.PixelWidth / geometry.LogicalWidth));
                    }

                string name = $"frames/frame-{index:00}.png";
                using var overview = Imaging.Fit(full, config.FrameLongEdge);
                images[name] = Imaging.EncodePng(overview);
                CropRegion? crop = null;
                bool windowOnly = !strokes.Any(s => s.VisibleAt(at)) && !pins.Any(p => p.VisibleAt(at));
                bool previewAdded = false;
                string caption = $"#{index + 1}  {PasteText.Time(at)}";
                if (CropPlanner.Plan(strokes, at, full.Width, full.Height, pins: pins, events: events, previous: previousWindowCrop) is { } rect)
                {
                    using var cut = Imaging.Crop(full, new SKRectI(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height));
                    using var detail = Imaging.Fit(cut, config.CropLongEdge);
                    string cropName = $"frames/frame-{index:00}-detail.png";
                    images[cropName] = Imaging.EncodePng(detail);
                    crop = new(cropName, rect.X, rect.Y, rect.Width, rect.Height, detail.Width, detail.Height);
                    if (windowOnly) { sheet.Add((Imaging.Fit(detail, 640), caption)); previewAdded = true; }
                }
                previousWindowCrop = !strokes.Any(s => s.VisibleAt(at)) && !pins.Any(p => p.VisibleAt(at)) && crop is { } windowCrop
                    ? new(windowCrop.X, windowCrop.Y, windowCrop.Width, windowCrop.Height) : null;
                keyframes.Add(new(at, name, geometry.Id, candidate.Reason, candidate.AtUs, pts, "m0",
                    overview.Width, overview.Height, crop));
                if (!previewAdded) sheet.Add((Imaging.Fit(overview, 640), caption));
            }
            images[Bundle.ContactSheet] = ContactSheet.Compose(sheet);
        }
        finally
        {
            foreach (var (image, _) in sheet) image.Dispose();
        }
        if (composition.Mode == CompositionMode.CleanSource)
            warnings.Add("Ink was not present in the captured pixels; frames show re-rendered vector ink.");

        progress?.Invoke("Hashing media");
        var (bytes, sha) = await BundleStore.HashAsync(video, cancellationToken);
        var streams = new List<MediaStream>
        {
            new("video", probe.VideoCodec, probe.VideoTimeBase, probe.VideoStartUs, probe.DurationUs, videoClock,
                probe.Width, probe.Height, probe.FramesPerSecond)
        };
        if (probe.AudioCodec is { } audioCodec)
            streams.Add(new("audio", audioCodec, probe.AudioTimeBase ?? "unknown", probe.AudioStartUs ?? 0, probe.DurationUs,
                audioClock ?? new ClockMap(0, ClockQuality.Unavailable, "CLOCK_MONOTONIC", "No audio clock was recorded.", 0),
                SampleRate: probe.AudioSampleRate, Channels: probe.AudioChannels));
        var media = new MediaAsset("m0", MediaPath, "video/x-matroska", bytes, sha,
            recording.Finalized ? "complete" : "partial", record.Backend, streams.ToArray());

        capabilities.Add(new("composition", composition.Mode == CompositionMode.Unknown ? Availability.Unknown : Availability.Available,
            composition.Basis));
        var bundle = new Bundle(Bundle.CurrentSchema, record.Id, false, record.CreatedUtc, duration,
            [geometry], [media], composition, strokes, events, journal.Sources, transcription, transcript,
            keyframes.OrderBy(k => k.AtUs).ToArray(), plan.Omissions, capabilities.ToArray(), warnings.ToArray()) { Pins = pins, WindowTitles = record.WindowTitles };

        string destination = Path.Combine(config.BundleDirectory ?? paths.Bundles, record.Id + ".wayscrawl");
        progress?.Invoke("Publishing " + destination);
        await BundleStore.WriteAsync(destination, bundle, images,
            new Dictionary<string, string> { [MediaPath] = video }, cancellationToken);
        // Publication is the commit point; a failure to annotate the session afterwards is reported,
        // not turned into a processing failure.
        try { WriteRecord(sessionDirectory, record with { State = "published", Bundle = destination }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            progress?.Invoke("Published, but the session record could not be updated: " + error.Message);
        }
        return destination;
    }

    private async Task<(TranscriptSegment[], TranscriptionInfo)> TranscribeAsync(string sessionDirectory, string video,
        MediaProbe probe, ClockMap? audioClock, long duration, List<string> warnings, CancellationToken cancellationToken)
    {
        const string extraction = "ffmpeg -vn -ac 1 -ar 16000 -c:a pcm_s16le";
        if (probe.AudioCodec is null || audioClock is null)
            return ([], new("unavailable", "none", "none", "none", "none", "none", "The recording has no audio stream."));
        if (transcriber is null)
            return ([], new("disabled", "none", "none", "none", "none", "none", "Transcription is not configured."));
        progress?.Invoke("Transcribing narration");
        string wav = Path.Combine(sessionDirectory, "narration.wav");
        try
        {
            await Ffmpeg.ExtractWavAsync(video, wav, cancellationToken);
            var result = await transcriber.TranscribeAsync(wav, cancellationToken);
            // The WAV starts at the first audio sample.
            long offset = (probe.AudioStartUs ?? 0) + audioClock.OffsetUs;
            var segments = result.Segments
                .Where(s => s.Text.Any(char.IsLetterOrDigit))
                .Select(s => s.Words is { } words && !TranscriptText.HasLosslessWords(s.Text, words)
                    ? s with { Words = null, Timing = TimingQuality.MeasuredSegment } : s)
                .Select(s => s with
                {
                    StartUs = Math.Clamp(s.StartUs + offset, 0, duration),
                    EndUs = Math.Clamp(s.EndUs + offset, 0, duration),
                    Text = s.Text.Trim(),
                    Words = s.Words?.Where(w => w.EndUs + offset > 0 && w.StartUs + offset < duration)
                        .Select(w => w with { StartUs = Math.Clamp(w.StartUs + offset, 0, duration), EndUs = Math.Clamp(w.EndUs + offset, 0, duration) }).ToArray()
                })
                .Where(s => s.EndUs >= s.StartUs)
                .Select(s => s.Words is { } words && !TranscriptText.HasLosslessWords(s.Text, words)
                    ? s with { Words = null, Timing = TimingQuality.MeasuredSegment } : s).ToArray();
            var info = result.Info with { Extraction = extraction };
            return (segments, segments.Length == 0 ? info with { Status = "no speech", Detail = "No speech was recognized." } : info);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException
                                          or System.ComponentModel.Win32Exception)
        {
            warnings.Add("Transcription failed; the audio is retained in the media file: " + error.Message);
            return ([], new("failed", "whisper.cpp", "unknown", "unknown", "unknown", extraction, error.Message));
        }
        finally
        {
            File.Delete(wav);
        }
    }

    private static Stroke Clamp(Stroke stroke, long duration) => stroke with
    {
        Points = stroke.Points.Select(p => p.AtUs > duration ? p with { AtUs = duration } : p).ToArray(),
        RemovedUs = stroke.RemovedUs > duration ? duration : stroke.RemovedUs
    };

    // Looks for each stroke's colour along its own path in the captured frame.
    public static Composition DetectComposition(SKBitmap frame, IReadOnlyList<Stroke> strokes, long atUs, IReadOnlyList<Pin>? pins = null, float pinScale = 1)
    {
        int hits = 0, total = 0;
        foreach (var stroke in strokes.Where(s => s.VisibleAt(atUs)))
        {
            var color = InkRenderer.ParseColor(stroke.Color);
            int step = Math.Max(1, stroke.Points.Length / 32);
            for (int i = 0; i < stroke.Points.Length && stroke.Points[i].AtUs <= atUs; i += step)
            {
                total++;
                if (NearColor(frame, (int)stroke.Points[i].X, (int)stroke.Points[i].Y, color, 2)) hits++;
            }
        }
        foreach (var pin in (pins ?? []).Where(p => p.VisibleAt(atUs)))
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI / 4;
                total++;
                if (NearColor(frame, (int)(pin.X + Math.Cos(angle) * 11 * pinScale), (int)(pin.Y + Math.Sin(angle) * 11 * pinScale), SKColors.White, 1) &&
                    NearColor(frame, (int)(pin.X + Math.Cos(angle) * 8 * pinScale), (int)(pin.Y + Math.Sin(angle) * 8 * pinScale), new SKColor(25, 25, 28), 1)) hits++;
            }
        if (total == 0) return new(CompositionMode.Unknown, "No visible ink at the first annotation frame.");
        double ratio = hits / (double)total;
        string basis = $"{hits}/{total} sampled ink points matched the stroke colour in the captured frame at " +
            $"{(atUs / 1_000_000d).ToString("0.000", CultureInfo.InvariantCulture)} s.";
        return ratio >= 0.5 ? new(CompositionMode.CapturedInk, "Verified: " + basis)
            : ratio <= 0.1 ? new(CompositionMode.CleanSource, "Verified: " + basis)
            : new(CompositionMode.Unknown, "Ambiguous: " + basis);
    }

    private static bool NearColor(SKBitmap frame, int x, int y, SKColor color, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int px = x + dx, py = y + dy;
                if (px < 0 || py < 0 || px >= frame.Width || py >= frame.Height) continue;
                var c = frame.GetPixel(px, py);
                int dr = c.Red - color.Red, dg = c.Green - color.Green, db = c.Blue - color.Blue;
                if (dr * dr + dg * dg + db * db < 60 * 60) return true;
            }
        return false;
    }
}
