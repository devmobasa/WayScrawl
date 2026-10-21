namespace WayScrawl.Core;

public static class TranscriptText
{
    // Whisper.net token strings may contain separately decoded UTF-8 byte fragments. A
    // finer timestamp must never replace intact segment text with lossy reconstruction.
    public static bool HasLosslessWords(string text, IReadOnlyList<TranscriptWord> words)
    {
        if (words.Count == 0 || words.Any(w => w is null || w.Text is null || w.Text.Contains('\uFFFD'))) return false;
        static string Compact(string value) => new(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return Compact(text) == Compact(string.Concat(words.Select(w => w.Text)));
    }
}
