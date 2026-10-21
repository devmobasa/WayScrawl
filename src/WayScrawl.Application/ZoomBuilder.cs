using System.Globalization;
using SkiaSharp;
using WayScrawl.Core;
using WayScrawl.Platform;
using WayScrawl.Platform.Media;
using WayScrawl.Rendering;

namespace WayScrawl.Application;

public sealed record ZoomResult(string Path, long[] TimesUs, string? Matched, string[] Narration)
{
    public long[] SourcePtsUs { get; init; } = [];
}

public static class ZoomBuilder
{
    public static async Task<ZoomResult> BuildAsync(string directory, string at, double span = 2, int frames = 12,
        bool full = false, CancellationToken cancellationToken = default, string? runtime = null)
    {
        if (!double.IsFinite(span) || span is <= 0 or > 60 || frames is < 1 or > 48)
            throw new ArgumentException("Span must be 0–60 seconds (exclusive of zero), frames 1–48.");
        using var lease = OperationLease.Acquire(directory, runtime ?? WayScrawlPaths.FromEnvironment().Runtime);
        var bundle = await BundleStore.ReadAsync(directory, cancellationToken);
        var match = MomentLookup.Resolve(bundle, at);
        var (_, video, media) = MediaFiles.Require(directory, bundle);
        var probe = await Ffmpeg.ProbeAsync(media, cancellationToken);
        long from = Math.Max(0, match.AtUs - (long)(span * 250_000));
        long to = Math.Min(bundle.DurationUs, from + (long)(span * 1_000_000));
        long startPts = Math.Max(probe.VideoStartUs, from - video.Clock.OffsetUs);
        long endPts = Math.Min(probe.FormatStartUs + probe.DurationUs, to - video.Clock.OffsetUs);
        if (endPts <= startPts) throw new InvalidDataException("No video covers this moment.");
        string zoom = System.IO.Path.Combine(directory, "zoom");
        Directory.CreateDirectory(zoom);
        if ((File.GetAttributes(zoom) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The zoom folder must not be a symlink.");
        string temporary = System.IO.Path.Combine(zoom, "." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var cells = new List<(SKBitmap, string)>();
        try
        {
            var extracted =
                await Ffmpeg.ExtractWindowAsync(media, startPts, endPts, probe.FormatStartUs, frames, temporary, cancellationToken);
            var times = new List<long>();
            foreach (var frame in extracted)
            {
                long time = frame.PresentationPtsUs + video.Clock.OffsetUs;
                using var bitmap = SKBitmap.Decode(frame.Path) ?? throw new InvalidDataException("Zoom frame is unreadable.");
                var nearest = bundle.Keyframes.MinBy(k => Math.Abs(k.AtUs - time))!;
                if (bundle.Composition.Mode == CompositionMode.CleanSource)
                {
                    using var canvas = new SKCanvas(bitmap);
                    foreach (var stroke in bundle.Strokes.Where(s => s.GeometryId == nearest.GeometryId && s.VisibleAt(time)))
                        InkRenderer.DrawAt(canvas, stroke, time);
                    var geometry = bundle.Geometries.First(g => g.Id == nearest.GeometryId);
                    foreach (var pin in bundle.Pins.Where(p => p.GeometryId == nearest.GeometryId && p.VisibleAt(time)))
                        InkRenderer.DrawPin(canvas, pin, (float)(geometry.PixelWidth / geometry.LogicalWidth));
                }
                SKBitmap cell;
                if (!full && nearest.Crop is { } crop)
                {
                    using var cut = Imaging.Crop(bitmap, new(crop.X, crop.Y, crop.X + crop.Width, crop.Y + crop.Height));
                    cell = Imaging.Fit(cut, 640);
                }
                else cell = Imaging.Fit(bitmap, 640);
                string label = $"Sample {cells.Count + 1} {PasteText.Time(time)}.{time / 100_000 % 10} " +
                    FormattableString.Invariant($"(source PTS {frame.PtsUs / 1_000_000d:0.000}s)");
                cells.Add((cell, label));
                times.Add(time);
            }
            if (cells.Count == 0) throw new InvalidDataException("No video frames cover this moment.");
            string output =
                System.IO.Path.Combine(zoom, $"zoom-{match.AtUs / 1000}-{span.ToString("0.###", CultureInfo.InvariantCulture)}.png");
            string pending = System.IO.Path.Combine(temporary, "sheet.png");
            await File.WriteAllBytesAsync(pending, ContactSheet.Compose(cells), cancellationToken);
            File.Move(pending, output, overwrite: true);
            return new(output, times.ToArray(), match.Text,
                bundle.Transcript.Where(t => t.EndUs >= from && t.StartUs <= to).Select(t => t.Text).ToArray())
                { SourcePtsUs = extracted.Select(frame => frame.PtsUs).ToArray() };
        }
        finally
        {
            foreach (var (bitmap, _) in cells) bitmap.Dispose();
            Directory.Delete(temporary, recursive: true);
        }
    }
}
