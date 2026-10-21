using System.Globalization;
using WayScrawl.Core;
using WayScrawl.Platform;
using WayScrawl.Platform.Media;

namespace WayScrawl.Application;

public sealed record VideoExport(string Path, int Width, int Height, string Encoder, string? Warning);

public static class VideoExporter
{
    public static async Task<VideoExport> ExportAsync(string directory, string output, string crop = "auto", int size = 1920,
        string? runtime = null, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        using var lease = OperationLease.Acquire(directory, runtime ?? WayScrawlPaths.FromEnvironment().Runtime);
        var bundle = await BundleStore.ReadAsync(directory, cancellationToken);
        var (_, _, media) = MediaFiles.Require(directory, bundle);
        var probe = await Ffmpeg.ProbeAsync(media, cancellationToken);
        var plan = ExportPlan.Create(bundle, probe.Width, probe.Height, crop, size);
        output = Path.GetFullPath(output);
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("Export output already exists: " + output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string temporary = Path.Combine(Path.GetDirectoryName(output)!, ".wayscrawl-export-" + Guid.NewGuid().ToString("N") + ".mp4");
        string filter = FormattableString.Invariant($"crop={plan.Crop.Width}:{plan.Crop.Height}:{plan.Crop.X}:{plan.Crop.Y}:exact=1,scale={plan.Width}:{plan.Height},setsar=1");
        async Task<bool> Encode(string encoder)
        {
            var result = await ProcessRunner.RunAsync(Ffmpeg.FfmpegTool, ["-nostdin", "-hide_banner", "-v", "error", "-y", "-i", media,
                "-map", "0:v:0", "-map", "0:a:0?", "-vf", filter, "-c:v", encoder, "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", temporary], cancellationToken);
            if (result.ExitCode == 0) return true;
            progress?.Invoke(encoder + ": " + ProcessRunner.LastLine(result.Error));
            return false;
        }
        try
        {
            var encoders = await ProcessRunner.RunCheckedAsync(Ffmpeg.FfmpegTool, ["-hide_banner", "-encoders"], cancellationToken);
            string encoder = encoders.Text.Contains(" h264_nvenc ", StringComparison.Ordinal) ? "h264_nvenc" : "libx264";
            if (!await Encode(encoder))
            {
                if (encoder == "libx264") throw new IOException("Software H.264 export failed.");
                File.Delete(temporary);
                progress?.Invoke("NVENC initialization/encoding failed; retrying with libx264.");
                encoder = "libx264";
                if (!await Encode(encoder)) throw new IOException("H.264 export failed with NVENC and libx264.");
            }
            File.Move(temporary, output, overwrite: false);
            string? warning = bundle.Composition.Mode is CompositionMode.CleanSource or CompositionMode.Unknown
                ? "Export uses the source video without vector ink; only CapturedInk recordings already contain annotations." : null;
            return new(output, plan.Width, plan.Height, encoder, warning);
        }
        finally { File.Delete(temporary); }
    }
}
