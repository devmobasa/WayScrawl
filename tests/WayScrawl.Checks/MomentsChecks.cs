using WayScrawl.Core;
using WayScrawl.Platform;

namespace WayScrawl.Checks;

internal static class MomentsChecks
{
    public static void Run(Harness h)
    {
        h.Section = "moments";
        var bundle = CoreChecks.SampleBundle() with
        {
            DurationUs = 12_000_000,
            Keyframes = [new(0, "frames/frame-00.png", "g0", "Initial"), new(5_000_000, "frames/frame-01.png", "g0", "Annotation")],
            Transcript = [new(0, 4_000_000, "Open settings.", TimingQuality.MeasuredSegment),
                new(6_000_000, 8_000_000, "the panel is empty.", TimingQuality.MeasuredWord,
                    [new(6_000_000, 6_300_000, "the"), new(6_300_000, 6_800_000, "panel"), new(6_800_000, 7_000_000, "is"), new(7_000_000, 8_000_000, "empty.")])],
            Events = [new(1_000_000, "input.press", "left at 10,20"), new(2_000_000, "input.press", "left at 12,22"),
                new(3_000_000, "input.press", "left at 14,22"), new(4_000_000, "window.focus", "firefox: secret title"),
                new(6_000_000, "workspace", "code"), new(8_100_000, "ink.clear", "s1"), new(8_500_000, "ink.fade", "s2"), new(9_000_000, "draw.on", "")]
        };
        string moments = string.Join('\n', Moments.Compose(bundle));
        h.Check(moments == "- #1 (0:00) [Initial]\n    · clicked left ×3\n    \"Open settings.\"\n    · switched to firefox\n" +
            "- #2 (0:05) [Annotation]\n    · workspace code\n    \"the panel is empty.\"\n    · cleared ink",
            "Moments folds clicks, orders narration and actions, assigns keyframe stretches and withholds titles/coordinates/fades/modes.");
        h.Check(string.Join('\n', Moments.Compose(bundle with { WindowTitles = true })).Contains("firefox: secret title"), "Explicit title metadata permits titles.");
        h.Check(MomentLookup.Resolve(bundle, "THE panel, is EMPTY!") is { AtUs: 6_000_000, Text: "the panel is empty." }, "Phrase matching ignores case and punctuation and uses word times.");
        h.Check(MomentLookup.Resolve(bundle, "empty panel") is { AtUs: 6_000_000 }, "Phrase fallback uses best token overlap.");
        h.Check(MomentLookup.Resolve(bundle, "0:07.5").AtUs == 7_500_000, "Timestamp lookup accepts fractional seconds.");
        h.Reject(() => MomentLookup.Resolve(bundle, "no matching words"), "Unmatched phrases are reported.");
        h.Reject(() => MomentLookup.Resolve(bundle, "0:70"), "Invalid timestamps are refused.");
        var boundary = bundle with { Transcript = [], Events = [new(4_900_000, "input.press", "left at 1,1"), new(5_100_000, "input.press", "left at 1,1")] };
        h.Check(Moments.Compose(boundary).Count(l => l.Contains("clicked left")) == 2 && !string.Join(' ', Moments.Compose(boundary)).Contains("×2"),
            "Click folding cannot transfer actions across keyframe ownership boundaries.");
        var privateReasons = bundle with { Transcript = [], Keyframes = [bundle.Keyframes[0] with
            { Reason = "After left at 812,433 click; Focused firefox: secret title; more private title" }],
            Events = [new(1_000_000, "draw.on", ""), new(1_100_000, "input.press", "left at 10,20"),
                new(1_200_000, "draw.off", ""), new(1_300_000, "input.press", "right at 30,40")] };
        string privateText = PasteText.Compose(privateReasons, "/tmp/evidence");
        h.Check(!privateText.Contains("812,433") && !privateText.Contains("secret title") && !privateText.Contains("more private title") &&
            !privateText.Contains("clicked left") && privateText.Contains("clicked right"),
            "Paste sanitizes legacy keyframe reasons and excludes pointer presses used for drawing.");
        var legacyPrivate = privateReasons with { Events = [new(0, "window.open", "firefox: private open title"),
            new(0, "window.layout", System.Text.Json.JsonSerializer.Serialize(new[] {
                new WindowSnapshot("window", "firefox", new(0, 0, 50, 50), true, "private layout title") }, WayScrawlJson.Journal))],
            Omissions = [new(0, "duplicate", "Spoken cue looks like Focused firefox: private omitted title.")] };
        string legacySummary = BundleStore.Summary(legacyPrivate);
        h.Check(!legacySummary.Contains("private open title") && !legacySummary.Contains("private layout title") && !legacySummary.Contains("secret title") &&
            !legacySummary.Contains("private omitted title"),
            "Imported summaries apply title privacy to legacy frame, omission, open-window and layout metadata.");
        h.Check(string.Join(' ', Moments.Compose(bundle with { Transcript = [], Events = [],
            Keyframes = [bundle.Keyframes[0] with { Reason = "Annotation Pin 2" }] })).Contains("[Pin ②]"),
            "Pin keyframe labels match the visible numbered pin.");
        byte[] Flat(byte n) => Enumerable.Repeat(n, 16).ToArray();
        var samples = Enumerable.Range(0, 32).Select(i => new FrameSample(i * 250_000L, Flat(i < 12 ? (byte)0 : (byte)200))).ToArray();
        var cue = new TranscriptSegment(2_800_000, 3_200_000, "look", TimingQuality.MeasuredWord, [new(2_800_000, 3_200_000, "look")]);
        var plan = KeyframePlanner.Plan(8_000_000, [], [], [cue], samples);
        h.Check(plan.Selected.Any(c => c.Kind == "cue" && c.AtUs >= 3_000_000 && c.AtUs <= 4_000_000), "Strong cues use a settled scene in their timing window.");
        var weak = KeyframePlanner.Plan(8_000_000, [], [], [cue with { StartUs = 1_000_000, EndUs = 1_200_000, Text = "this", Words = [new(1_000_000, 1_100_000, "this")] }], samples);
        h.Check(weak.Selected.All(c => c.Kind != "cue"), "Weak words alone do not create cues.");
        var segmentOnly = KeyframePlanner.Plan(8_000_000, [], [], [cue with { Words = null,
            Timing = TimingQuality.MeasuredSegment }], samples);
        h.Check(segmentOnly.Selected.All(c => c.Kind != "cue") && segmentOnly.Omissions.All(o => !o.Detail.Contains("Spoken cue")),
            "Segment-only narration does not invent a word cue time.");
        var still = samples.Select(s => s with { Gray = Flat(0) }).ToArray();
        var duplicate = KeyframePlanner.Plan(8_000_000, [], [new(6_000_000, "marker", "keep")], [cue], still);
        h.Check(duplicate.Selected.All(c => c.Kind != "cue") && duplicate.Omissions.Any(o => o.Detail.Contains("Spoken cue") && o.Reason == "duplicate"),
            "Cues matching a selected screen are dropped as duplicates.");
    }
}
