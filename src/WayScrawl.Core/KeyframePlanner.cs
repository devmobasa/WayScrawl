namespace WayScrawl.Core;

public sealed record FrameCandidate(long AtUs, string Kind, string Reason, int Priority, bool Protected);

public sealed record FramePlan(FrameCandidate[] Selected, Omission[] Omissions);

// Gray8 thumbnail at a session time, used for scene changes and near-duplicate checks.
public sealed record FrameSample(long AtUs, byte[] Gray);

public sealed record PlannerOptions(
    int MaxFrames = 12, long SettleUs = 300_000, long BurstGapUs = 1_500_000,
    double SceneThreshold = 6.0, double DuplicateThreshold = 1.5, long MergeWindowUs = 400_000,
    long NarrationRadiusUs = 1_500_000, long StartUs = 0);

// WayScrawl's own selection policy, not a reproduction of any upstream algorithm. Distinct annotation states
// and explicit markers are protected: a count budget never silently removes them.
public static class KeyframePlanner
{
    public static FramePlan Plan(long durationUs, IReadOnlyList<Stroke> strokes, IReadOnlyList<TimelineEvent> events,
        IReadOnlyList<TranscriptSegment> transcript, IReadOnlyList<FrameSample> samples, PlannerOptions? options = null, IReadOnlyList<Pin>? pins = null)
    {
        var o = options ?? new PlannerOptions();
        var candidates = new List<FrameCandidate>();
        long end = Math.Max(0, durationUs);
        // StartUs is the first captured frame; nothing earlier can be shown.
        long start = Math.Clamp(o.StartUs, 0, end);
        long Clamp(long t) => Math.Clamp(t, start, end);

        candidates.Add(new(Clamp(start + Math.Min(500_000, (end - start) / 4)), "initial", "Initial view", 2, false));
        candidates.Add(new(Clamp(end - 200_000), "final", "Final view", 2, false));

        foreach (var marker in events.Where(e => e.Kind == "marker"))
            candidates.Add(new(Clamp(marker.AtUs + 150_000), "marker",
                "Marker" + (marker.Detail.Length > 0 ? ": " + marker.Detail : ""), 0, true));

        // Annotation bursts: strokes separated by less than BurstGapUs form one visible state.
        var annotationStrokes = strokes.Concat((pins ?? []).Select(p =>
            new Stroke("Pin " + p.Number, p.GeometryId, "#ffffff", 1, [new(p.AtUs, p.X, p.Y)], p.RemovedUs)));
        var ordered = annotationStrokes.Where(s => s.Points.Length > 0).OrderBy(s => s.Points[0].AtUs).ToList();
        var transitions = ordered.Where(s => s.RemovedUs is not null).Select(s => s.RemovedUs!.Value)
            .Concat(events.Where(e => e.Kind is "window.focus" or "workspace").Select(e => e.AtUs))
            .Order().ToList();
        for (int i = 0; i < ordered.Count;)
        {
            int j = i;
            long burstEnd = ordered[i].Points[^1].AtUs;
            while (j + 1 < ordered.Count && ordered[j + 1].Points[0].AtUs - burstEnd < o.BurstGapUs &&
                !transitions.Any(t => t >= burstEnd && t <= ordered[j + 1].Points[0].AtUs))
            {
                j++;
                burstEnd = Math.Max(burstEnd, ordered[j].Points[^1].AtUs);
            }
            long next = j + 1 < ordered.Count ? ordered[j + 1].Points[0].AtUs : end;
            long transition = transitions.FirstOrDefault(t => t > burstEnd, long.MaxValue);
            // Settle after drawing, but stay before the next undo, clear, focus change or stroke.
            long at = Math.Min(burstEnd + o.SettleUs, Math.Min(next, transition) - 50_000);
            at = Clamp(Math.Max(at, burstEnd));
            string ids = string.Join(", ", ordered.Skip(i).Take(j - i + 1).Select(s => s.Id));
            candidates.Add(new(at, "annotation", "Annotation " + ids, 0, true));
            i = j + 1;
        }

        var scenes = SceneChanges(samples, o).ToArray();
        foreach (var segment in transcript)
        {
            if (segment.Words is not { Length: > 0 } words) continue;
            var cues = words.SelectMany(w => MomentLookup.Tokens(w.Text).Select(t => (Text: t, At: w.StartUs)));
            foreach (var cue in cues)
            {
                bool strong = cue.Text is "now" or "here" or "look" or "watch";
                if (!strong && cue.Text is not ("this" or "that" or "there" or "see")) continue;
                var nearby = scenes.Where(s => s.AtUs >= cue.At - 300_000 && s.AtUs <= cue.At + 1_200_000).ToArray();
                if (!strong && nearby.Length == 0) continue;
                long at = nearby.Length > 0 ? nearby[0].AtUs : cue.At + 150_000;
                candidates.Add(new(Clamp(at), "cue", "Spoken cue: " + cue.Text, 1, false));
            }
        }
        foreach (var change in scenes)
            candidates.Add(new(Clamp(change.AtUs), "scene", $"Screen changed (Δ {change.Score:0.0})", 3, false));

        // A frame shortly after a click shows its effect. Presses while drawing are ink, not clicks.
        bool drawing = false;
        foreach (var item in events)
        {
            if (item.Kind == "draw.on") drawing = true;
            else if (item.Kind == "draw.off") drawing = false;
            else if (item.Kind == "input.press" && !drawing)
                candidates.Add(new(Clamp(item.AtUs + 350_000), "click", "After " + item.Detail + " click", 3, false));
        }

        foreach (var focus in events.Where(e => e.Kind == "window.focus"))
            candidates.Add(new(Clamp(focus.AtUs + 400_000), "window", "Focused " + focus.Detail, 4, false));

        foreach (var segment in transcript)
        {
            long middle = (segment.StartUs + segment.EndUs) / 2;
            if (candidates.Any(c => Math.Abs(c.AtUs - middle) < o.NarrationRadiusUs)) continue;
            string words = segment.Text.Trim();
            candidates.Add(new(Clamp(middle), "narration",
                "Narration: " + (words.Length > 60 ? words[..60] + "…" : words), 5, false));
        }

        bool SameInk(long a, long b) => strokes.All(s => s.VisibleAt(a) == s.VisibleAt(b) &&
            (!s.VisibleAt(a) || s.Points.Count(p => p.AtUs <= a) == s.Points.Count(p => p.AtUs <= b))) &&
            (pins ?? []).All(p => p.VisibleAt(a) == p.VisibleAt(b));
        return Select(candidates, samples, o, transitions, SameInk);
    }

    private static FramePlan Select(List<FrameCandidate> candidates, IReadOnlyList<FrameSample> samples, PlannerOptions o, IReadOnlyList<long> transitions, Func<long, long, bool> sameInk)
    {
        var omissions = new List<Omission>();
        var selected = new List<FrameCandidate>();
        foreach (var candidate in candidates.OrderBy(c => c.Protected ? 0 : 1).ThenBy(c => c.Priority).ThenBy(c => c.AtUs))
        {
            int near = selected.FindIndex(s => Math.Abs(s.AtUs - candidate.AtUs) < o.MergeWindowUs && sameInk(s.AtUs, candidate.AtUs) &&
                !transitions.Any(t => t > Math.Min(s.AtUs, candidate.AtUs) && t <= Math.Max(s.AtUs, candidate.AtUs)));
            if (near >= 0)
            {
                // A protected moment folds into the neighbouring frame and keeps its reason.
                if (candidate.Protected)
                    selected[near] = selected[near] with { Reason = selected[near].Reason + "; " + candidate.Reason, Protected = true };
                else
                    omissions.Add(new(candidate.AtUs, "merged", $"{candidate.Reason} is within {o.MergeWindowUs / 1000} ms of {selected[near].Reason}."));
                continue;
            }
            if (!candidate.Protected)
            {
                var twin = selected.FirstOrDefault(s => Difference(samples, s.AtUs, candidate.AtUs) < o.DuplicateThreshold);
                if (twin is not null)
                {
                    omissions.Add(new(candidate.AtUs, "duplicate", $"{candidate.Reason} looks like {twin.Reason}."));
                    continue;
                }
                if (selected.Count >= o.MaxFrames)
                {
                    omissions.Add(new(candidate.AtUs, "budget", $"{candidate.Reason} exceeded the {o.MaxFrames}-frame budget."));
                    continue;
                }
            }
            selected.Add(candidate);
        }
        return new(selected.OrderBy(c => c.AtUs).ToArray(), omissions.OrderBy(x => x.AtUs).ToArray());
    }

    public static IEnumerable<(long AtUs, double Score)> SceneChanges(IReadOnlyList<FrameSample> samples, PlannerOptions o)
    {
        for (int i = 1; i < samples.Count; i++)
        {
            double jump = MeanDifference(samples[i - 1].Gray, samples[i].Gray);
            if (jump < o.SceneThreshold || samples[i].AtUs < 500_000) continue;
            // Wait for two quiet samples so a half-finished transition is not selected.
            int settled = i;
            while (settled + 2 < samples.Count &&
                   (MeanDifference(samples[settled].Gray, samples[settled + 1].Gray) >= o.SceneThreshold / 2 ||
                    MeanDifference(samples[settled + 1].Gray, samples[settled + 2].Gray) >= o.SceneThreshold / 2))
                settled++;
            double score = MeanDifference(samples[i - 1].Gray, samples[Math.Min(settled + 1, samples.Count - 1)].Gray);
            yield return (samples[Math.Min(settled + 1, samples.Count - 1)].AtUs, score);
            i = settled + 1;
        }
    }

    public static double MeanDifference(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return double.PositiveInfinity;
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / (double)a.Length;
    }

    private static double Difference(IReadOnlyList<FrameSample> samples, long a, long b)
    {
        if (samples.Count == 0) return double.PositiveInfinity;
        return MeanDifference(Nearest(samples, a).Gray, Nearest(samples, b).Gray);
    }

    private static FrameSample Nearest(IReadOnlyList<FrameSample> samples, long at) =>
        samples.MinBy(s => Math.Abs(s.AtUs - at))!;
}
