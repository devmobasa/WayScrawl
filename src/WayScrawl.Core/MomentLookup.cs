using System.Globalization;

namespace WayScrawl.Core;

public sealed record MomentMatch(long AtUs, string? Text);

public static class MomentLookup
{
    public static string[] Tokens(string text) => new string(text.ToLowerInvariant()
        .Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    public static MomentMatch Resolve(Bundle bundle, string query)
    {
        string[] time = query.Split(':');
        double seconds;
        if (time.Length == 2 && int.TryParse(time[0], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) &&
            double.TryParse(time[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double remainder) && remainder < 60)
            seconds = minutes * 60d + remainder;
        else if (!double.TryParse(query, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            return Phrase(bundle.Transcript, query);
        if (!double.IsFinite(seconds) || seconds < 0 || seconds * 1_000_000 > bundle.DurationUs)
            throw new ArgumentException("The requested moment is outside the recording.");
        return new((long)Math.Round(seconds * 1_000_000), null);
    }

    private static MomentMatch Phrase(TranscriptSegment[] segments, string query)
    {
        string[] wanted = Tokens(query);
        if (wanted.Length == 0) throw new ArgumentException("Supply a timestamp or narration phrase.");
        var tokens = segments.SelectMany(s => s.Words is { Length: > 0 } words
            ? words.SelectMany(w => Tokens(w.Text).Select(t => (Token: t, At: w.StartUs, Segment: s.Text)))
            : Tokens(s.Text).Select(t => (Token: t, At: s.StartUs, Segment: s.Text))).ToArray();
        for (int i = 0; i <= tokens.Length - wanted.Length; i++)
            if (tokens.Skip(i).Take(wanted.Length).Select(t => t.Token).SequenceEqual(wanted))
                return new(tokens[i].At, tokens[i].Segment);
        var best = segments.Select(s => (Segment: s, Score: Tokens(s.Text).Distinct().Intersect(wanted).Count() /
                (double)Math.Max(1, Tokens(s.Text).Distinct().Union(wanted).Count())))
            .OrderByDescending(s => s.Score).FirstOrDefault();
        if (best.Score <= 0 || best.Segment is null) throw new ArgumentException("No narration matches that phrase.");
        return new(best.Segment.StartUs, best.Segment.Text);
    }
}
