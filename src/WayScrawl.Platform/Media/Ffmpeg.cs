using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace WayScrawl.Platform.Media;

public sealed record MediaProbe(
    long DurationUs, int Width, int Height, double FramesPerSecond, string VideoCodec,
    long VideoStartUs, string? AudioCodec, int AudioSampleRate, int AudioChannels, long? AudioStartUs,
    string Container, long Bytes, long FormatStartUs, string VideoTimeBase, string? AudioTimeBase);

// Gray8 thumbnail sampled at a fixed rate; AtUs is relative to the first video frame.
public sealed record SampledFrame(long AtUs, byte[] Pixels);

// Source pixels keep their decoded PTS even when presented later inside a VFR gap.
public sealed record ExtractedFrame(string Path, long PtsUs, long PresentationPtsUs);

public static class Ffmpeg
{
    public const string FfmpegTool = "ffmpeg";
    public const string FfprobeTool = "ffprobe";

    public static async Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunCheckedAsync(FfprobeTool,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path],
            cancellationToken);
        using var json = JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        JsonElement? video = null, audio = null;
        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            string type = stream.GetProperty("codec_type").GetString() ?? "";
            if (type == "video" && video is null) video = stream;
            if (type == "audio" && audio is null) audio = stream;
        }
        if (video is not { } v) throw new InvalidDataException("The recording has no video stream.");
        var format = root.GetProperty("format");
        long duration = Seconds(format, "duration") ?? Seconds(v, "duration") ??
            throw new InvalidDataException("The recording has no duration; it may be truncated.");
        return new MediaProbe(
            duration, v.GetProperty("width").GetInt32(), v.GetProperty("height").GetInt32(),
            Rate(v, "avg_frame_rate") ?? Rate(v, "r_frame_rate") ?? 0,
            v.GetProperty("codec_name").GetString() ?? "unknown",
            Seconds(v, "start_time") ?? 0,
            audio?.GetProperty("codec_name").GetString(),
            audio is { } a && a.TryGetProperty("sample_rate", out var rate)
                ? int.Parse(rate.GetString()!, CultureInfo.InvariantCulture) : 0,
            audio is { } c && c.TryGetProperty("channels", out var channels) ? channels.GetInt32() : 0,
            audio is { } s ? Seconds(s, "start_time") : null,
            format.GetProperty("format_name").GetString() ?? "unknown",
            new FileInfo(path).Length, Seconds(format, "start_time") ?? 0,
            v.TryGetProperty("time_base", out var vb) ? vb.GetString() ?? "unknown" : "unknown",
            audio is { } t && t.TryGetProperty("time_base", out var ab) ? ab.GetString() : null);
    }

    public static async Task<ExtractedFrame[]> ExtractWindowAsync(string input, long startPts, long endPts,
        long formatStartUs, int frames, string output, CancellationToken cancellationToken)
    {
        string N(double n) => n.ToString("0.000000", CultureInfo.InvariantCulture);
        double step = (endPts - startPts) / 1_000_000d / frames;
        string filter = $"trim=start={N(startPts / 1_000_000d)}:end={N(endPts / 1_000_000d)}," +
            $"select=gte(t-{N(startPts / 1_000_000d)}\\,selected_n*{N(step)}),showinfo=checksum=0";
        var timestamps = new List<long>(frames);
        using var process = ProcessRunner.Start(FfmpegTool, ["-nostdin", "-hide_banner", "-nostats", "-v", "info", "-copyts",
            "-ss", N(Math.Max(0, startPts - formatStartUs) / 1_000_000d), "-t", N((endPts - startPts) / 1_000_000d),
            "-i", input, "-vf", filter, "-frames:v", frames.ToString(CultureInfo.InvariantCulture), "-fps_mode", "vfr",
            System.IO.Path.Combine(output, "frame-%02d.png")]);
        // Keep parsed timestamps independently of the bounded diagnostic tail. At 48 frames,
        // ordinary showinfo output alone can exceed the generic tool stderr limit.
        async Task<string> ReadInfo()
        {
            var tail = new System.Text.StringBuilder();
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"pts_time:(-?[0-9.]+)");
                if (match.Success && timestamps.Count < frames && double.TryParse(match.Groups[1].Value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double seconds)) timestamps.Add((long)Math.Round(seconds * 1_000_000));
                tail.AppendLine(line);
                if (tail.Length > 16_384) tail.Remove(0, tail.Length - 16_384);
            }
            return tail.ToString();
        }
        var error = ReadInfo();
        var stdout = ProcessRunner.ReadBoundedAsync(process.StandardOutput);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await stdout;
            if (process.ExitCode != 0) throw new IOException("Zoom extraction failed: " + ProcessRunner.LastLine(await error));
            await error;
        }
        finally
        {
            ProcessRunner.TryKill(process);
            try { await Task.WhenAll(error, stdout); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
        var files = Directory.GetFiles(output, "frame-*.png").Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0)
        {
            // A VFR frame remains displayed until its successor. A short interval may contain
            // no new packet, so recover the preceding frame rather than claiming no video.
            var packets = await ProcessRunner.RunCheckedAsync(FfprobeTool,
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", input], cancellationToken);
            long? previous = null;
            foreach (string line in packets.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (double.TryParse(line.TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                {
                    long time = (long)Math.Round(seconds * 1_000_000);
                    if (time <= startPts && (previous is null || time > previous)) previous = time;
                }
            if (previous is null) return [];
            var (png, actual) = await ExtractPngAsync(input, previous.Value, formatStartUs, cancellationToken);
            string path = System.IO.Path.Combine(output, "frame-00.png");
            await File.WriteAllBytesAsync(path, png, cancellationToken);
            return [new(path, actual ?? previous.Value, startPts)];
        }
        if (timestamps.Count < files.Length) throw new InvalidDataException("Could not establish decoded timestamps for every zoom cell.");
        return files.Select((f, i) => new ExtractedFrame(f, timestamps[i], timestamps[i])).ToArray();
    }

    // Last video packet PTS, read by demuxing only. Needed when a truncated container's
    // duration covers audio that outlived the last flushed video frame.
    public static async Task<long?> LastVideoPtsAsync(string path, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunCheckedAsync(FfprobeTool,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", path],
            cancellationToken);
        long? last = null;
        foreach (string line in result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (double.TryParse(line.TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
                last = Math.Max(last ?? long.MinValue, (long)Math.Round(seconds * 1_000_000));
        return last;
    }

    public static Task ExtractWavAsync(string input, string output, CancellationToken cancellationToken) =>
        ProcessRunner.RunCheckedAsync(FfmpegTool,
            ["-nostdin", "-v", "error", "-y", "-i", input, "-vn", "-ac", "1", "-ar", "16000",
             "-c:a", "pcm_s16le", output], cancellationToken);

    // Input seeking with transcoding is frame-accurate (accurate_seek): ffmpeg jumps to the
    // previous keyframe, then decodes and discards frames before the target. -ss is relative
    // to the container start, so the stream PTS is converted; -copyts keeps showinfo's
    // pts_time in the original timeline, which is returned as the frame actually decoded.
    public static async Task<(byte[] Png, long? PtsUs)> ExtractPngAsync(string input, long ptsUs, long formatStartUs,
        CancellationToken cancellationToken)
    {
        string at = (Math.Max(0, ptsUs - formatStartUs) / 1_000_000d).ToString("0.000000", CultureInfo.InvariantCulture);
        var result = await ProcessRunner.RunAsync(FfmpegTool,
            ["-nostdin", "-v", "info", "-copyts", "-ss", at, "-i", input, "-frames:v", "1", "-vf", "showinfo",
             "-f", "image2pipe", "-c:v", "png", "pipe:1"], cancellationToken);
        if (result.ExitCode != 0 || result.Output.Length < 8)
            throw new InvalidDataException($"No frame exists at {at} s: {ProcessRunner.LastLine(result.Error)}");
        var match = System.Text.RegularExpressions.Regex.Match(result.Error, @"pts_time:(-?[0-9.]+)");
        long? actual = match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float,
            CultureInfo.InvariantCulture, out double seconds) ? (long)Math.Round(seconds * 1_000_000) : null;
        return (result.Output, actual);
    }

    public static async IAsyncEnumerable<SampledFrame> SampleGrayAsync(string input, double fps,
        int width, int height, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Index zero represents the first video frame, independently of an earlier audio
        // stream. Explicit normalization also prevents the output muxer duplicating a gap.
        string filter = FormattableString.Invariant($"setpts=PTS-STARTPTS,fps={fps},scale={width}:{height}:flags=area,format=gray");
        using var process = ProcessRunner.Start(FfmpegTool,
            ["-nostdin", "-v", "error", "-i", input, "-an", "-vf", filter, "-f", "rawvideo", "pipe:1"]);
        var error = ProcessRunner.ReadBoundedAsync(process.StandardError);
        var stream = process.StandardOutput.BaseStream;
        try
        {
            for (long index = 0; ; index++)
            {
                byte[] frame = new byte[width * height];
                int filled = 0;
                while (filled < frame.Length)
                {
                    int read = await stream.ReadAsync(frame.AsMemory(filled), cancellationToken);
                    if (read == 0) break;
                    filled += read;
                }
                if (filled < frame.Length) break;
                yield return new SampledFrame((long)Math.Round(index * 1_000_000 / fps), frame);
            }
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new IOException("ffmpeg sampling failed: " + ProcessRunner.LastLine(await error));
        }
        finally
        {
            ProcessRunner.TryKill(process);
        }
    }

    private static long? Seconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            ? (long)Math.Round(seconds * 1_000_000) : null;

    private static double? Rate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.GetString() is not { } text) return null;
        string[] parts = text.Split('/');
        if (parts.Length != 2 || !double.TryParse(parts[0], CultureInfo.InvariantCulture, out double n) ||
            !double.TryParse(parts[1], CultureInfo.InvariantCulture, out double d) || d == 0 || n == 0)
            return null;
        return n / d;
    }
}
