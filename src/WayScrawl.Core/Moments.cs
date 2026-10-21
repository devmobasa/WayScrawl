namespace WayScrawl.Core;

public static class Moments
{
    private sealed record Entry(long AtUs, string Text, bool Narration = false);

    public static string[] Compose(Bundle bundle)
    {
        var lines = new List<string>();
        var actions = Actions(bundle).ToArray();
        for (int i = 0; i < bundle.Keyframes.Length; i++)
        {
            var frame = bundle.Keyframes[i];
            long from = i == 0 ? 0 : frame.AtUs;
            long to = i + 1 < bundle.Keyframes.Length ? bundle.Keyframes[i + 1].AtUs : long.MaxValue;
            lines.Add($"- #{i + 1} ({PasteText.Time(frame.AtUs)}) [{FrameReason(frame.Reason, bundle.WindowTitles)}]");
            var segments = bundle.Transcript.Where(t => Midpoint(t) >= from && Midpoint(t) < to).ToArray();
            var owned = actions.Where(e => e.AtUs >= from && e.AtUs < to).ToArray();
            var entries = new List<Entry>(owned);
            foreach (var segment in segments)
            {
                // Word timing lets an event sit at a nearby clause boundary, without inventing
                // sub-segment timing for legacy transcripts.
                if (segment.Words is not { Length: > 0 } words)
                { entries.Add(new(Midpoint(segment), PasteText.OneLine(segment.Text), true)); continue; }
                int start = 0;
                for (int w = 0; w < words.Length; w++)
                {
                    bool clause = words[w].Text.TrimEnd().EndsWith('.') || words[w].Text.TrimEnd().EndsWith(',') ||
                        words[w].Text.TrimEnd().EndsWith(';') || words[w].Text.TrimEnd().EndsWith('?');
                    if (w + 1 < words.Length && (!clause || !owned.Any(e => Math.Abs(e.AtUs - words[w].EndUs) <= 1_200_000))) continue;
                    entries.Add(new(words[start].StartUs + (words[w].EndUs - words[start].StartUs) / 2,
                        string.Join(' ', words.Skip(start).Take(w - start + 1).Select(x => x.Text.Trim())), true));
                    start = w + 1;
                }
            }
            foreach (var entry in entries.OrderBy(e => e.AtUs).ThenBy(e => e.Narration))
                lines.Add(entry.Narration ? $"    \"{PasteText.OneLine(entry.Text)}\"" : "    · " + entry.Text);
        }
        return lines.ToArray();
    }

    // Old manifests can contain click coordinates and titles in their frame reasons.
    // Render those machine-generated reason fields under the current bundle's privacy policy too.
    public static string FrameReason(string reason, bool windowTitles)
    {
        reason = PasteText.OneLine(reason);
        int focus = reason.StartsWith("Focused ", StringComparison.Ordinal) ? 0 : reason.IndexOf("; Focused ", StringComparison.Ordinal);
        if (focus > 0) focus += 2;
        if (!windowTitles && focus >= 0)
        {
            // Legacy title strings may themselves contain the merge separator; their end
            // cannot be recovered reliably. Prefer a generic focus label to exposing them.
            reason = reason[..focus] + "Window changed";
        }
        return string.Join("; ", reason.Split("; ").Select(part =>
        {
            if (part.StartsWith("Annotation Pin ", StringComparison.Ordinal) && int.TryParse(part[15..], out int number))
                return "Pin " + (number is >= 1 and <= 20 ? char.ConvertFromUtf32(0x2460 + number - 1) : number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (part.StartsWith("After ", StringComparison.Ordinal) && part.EndsWith(" click", StringComparison.Ordinal))
                return "After " + part[6..^6].Split(" at ", 2)[0].Split(" outside ", 2)[0] + " click";
            return part;
        }));
    }

    private static long Midpoint(TranscriptSegment segment) => segment.StartUs + (segment.EndUs - segment.StartUs) / 2;

    private static IEnumerable<Entry> Actions(Bundle bundle)
    {
        string? button = null;
        long first = 0, last = 0;
        int repeats = 0, owner = -1;
        bool drawing = false;
        Entry Click() => new(first, "clicked " + button + (repeats > 1 ? " ×" + repeats : ""));
        foreach (var e in bundle.Events)
        {
            if (e.Kind is "draw.on" or "draw.off")
            {
                drawing = e.Kind == "draw.on";
                if (repeats > 0) { yield return Click(); repeats = 0; button = null; }
                continue;
            }
            string? action = e.Kind switch
            {
                "window.focus" => "switched to " + (bundle.WindowTitles ? e.Detail : e.Detail.Split(':', 2)[0]),
                "workspace" => "workspace " + e.Detail,
                "marker" => "⚑ " + e.Detail,
                "pin" => "Pin " + e.Detail,
                "ink.clear" => "cleared ink",
                _ => null
            };
            if (e.Kind == "input.press" && !drawing)
            {
                string next = e.Detail.Split(" at ", 2)[0].Split(" outside ", 2)[0];
                int nextOwner = Math.Max(0, Array.FindLastIndex(bundle.Keyframes, k => k.AtUs <= e.AtUs));
                if (button != next || e.AtUs - last > 3_000_000 || owner != nextOwner)
                {
                    if (repeats > 0) yield return Click();
                    button = next; first = e.AtUs; repeats = 0; owner = nextOwner;
                }
                repeats++; last = e.AtUs;
                continue;
            }
            if (action is null) continue;
            if (repeats > 0) { yield return Click(); repeats = 0; button = null; }
            yield return new(e.AtUs, PasteText.OneLine(action));
        }
        if (repeats > 0) yield return Click();
    }
}
