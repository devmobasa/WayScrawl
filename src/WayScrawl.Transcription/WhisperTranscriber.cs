using Whisper.net;
using System.Text;
using Whisper.net.LibraryLoader;
using WayScrawl.Core;

namespace WayScrawl.Transcription;

// whisper.cpp through Whisper.net. Runs inside the transcription worker process only.
// Token timing is retained only when native timestamps form valid ordered words.
public sealed class WhisperTranscriber(string model, string language, string runtime) : ITranscriber
{
    public async Task<TranscriptionResult> TranscribeAsync(string audioPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(model))
            throw new FileNotFoundException($"Whisper model not found: {model}. Set whisper.model in config.json.", model);
        RuntimeOptions.RuntimeLibraryOrder = runtime switch
        {
            "cpu" => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
            "cuda" => [RuntimeLibrary.Cuda],
            _ => [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx]
        };
        using var factory = WhisperFactory.FromPath(model, new WhisperFactoryOptions { UseGpu = runtime != "cpu" });
        var builder = factory.CreateBuilder();
        builder = language == "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(language);
        using var processor = builder.WithTokenTimestamps().Build();
        await using var audio = File.OpenRead(audioPath);
        var segments = new List<TranscriptSegment>();
        string? detected = null;
        await foreach (var segment in processor.ProcessAsync(audio, cancellationToken))
        {
            detected ??= segment.Language;
            var words = Words(segment);
            segments.Add(new(segment.Start.Ticks / 10, segment.End.Ticks / 10, segment.Text,
                words is null ? TimingQuality.MeasuredSegment : TimingQuality.MeasuredWord, words));
        }
        string loaded = RuntimeOptions.LoadedLibrary?.ToString() ?? runtime;
        return new(segments.ToArray(), new TranscriptionInfo("ok", "whisper.cpp (Whisper.net 1.9.1)", loaded,
            Path.GetFileName(model), detected ?? language, "", $"{segments.Count} segments; native token timing where available (experimental Whisper timestamps)."));
    }

    private static TranscriptWord[]? Words(SegmentData segment)
    {
        var words = new List<TranscriptWord>();
        var text = new StringBuilder();
        long from = 0, to = 0, segmentStart = segment.Start.Ticks / 10, segmentEnd = segment.End.Ticks / 10;
        void Finish()
        {
            string value = text.ToString().Trim();
            if (value.Length > 0) words.Add(new(from, to, value));
            text.Clear();
        }
        foreach (var token in segment.Tokens)
        {
            if (token.Text is not { Length: > 0 } piece || piece.StartsWith("<|", StringComparison.Ordinal) || piece.StartsWith("[_", StringComparison.Ordinal)) continue;
            if (token.Start < 0 || token.End < token.Start || token.End <= 0 && piece.Any(char.IsLetterOrDigit)) return null;
            if (char.IsWhiteSpace(piece[0]) && text.Length > 0) Finish();
            long start = Math.Clamp(token.Start * 10_000, segmentStart, segmentEnd);
            long end = Math.Clamp(token.End * 10_000, segmentStart, segmentEnd);
            if (text.Length == 0) from = start;
            to = Math.Max(start, end);
            text.Append(piece);
        }
        Finish();
        if (!TranscriptText.HasLosslessWords(segment.Text, words) || words.Any(w => w.EndUs < w.StartUs) ||
            words.Zip(words.Skip(1)).Any(p => p.First.EndUs > p.Second.StartUs)) return null;
        return words.ToArray();
    }
}
