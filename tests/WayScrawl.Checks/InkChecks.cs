using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using WayScrawl.Application;
using WayScrawl.Core;
using WayScrawl.Platform;
using WayScrawl.Rendering;

namespace WayScrawl.Checks;

internal static class InkChecks
{
    public static void Run(Harness h)
    {
        h.Section = "ink-contract";
        var pin = new Pin("p1", "g0", 1, 80, 90, 1_000_000, 1_900_000);
        var sample = CoreChecks.SampleBundle() with { Pins = [pin], Transcript =
            [new(0, 500_000, "look now", TimingQuality.MeasuredWord, [new(0, 200_000, "look"), new(200_000, 500_000, "now")])] };
        var roundtrip = BundleStore.Parse(JsonSerializer.SerializeToUtf8Bytes(sample, BundleStore.Json));
        h.Check(roundtrip.Pins.Single() == pin && roundtrip.Transcript.Single().Words!.Length == 2, "Schema 0.3 round trips pins and words.");
        foreach (var invalid in new[] { pin with { X = double.NaN }, pin with { X = 201 }, pin with { Number = 0 },
            pin with { RemovedUs = 999_999 }, pin with { GeometryId = "missing" }, pin with { Id = "s1" } })
            h.Reject(() => BundleRules.Validate(sample with { Pins = [invalid] }), "Invalid pin rejected: " + invalid);
        h.Reject(() => BundleRules.Validate(sample with { Pins = [pin, pin with { Id = "p2" }] }), "Pin numbers are unique.");
        foreach (var words in new[] { new[] { new TranscriptWord(0, 600_000, "late") },
            new[] { new TranscriptWord(200_000, 400_000, "later"), new TranscriptWord(100_000, 200_000, "earlier") } })
            h.Reject(() => BundleRules.Validate(sample with { Transcript = [sample.Transcript[0] with { Words = words }] }),
                "Word timestamps must stay ordered and inside their segment.");
        var legacy = JsonNode.Parse(JsonSerializer.Serialize(sample, BundleStore.Json))!.AsObject();
        legacy["schema"] = "wayscrawl.bundle/0.2";
        legacy.Remove("pins");
        legacy["transcript"]![0]!.AsObject().Remove("words");
        legacy["transcript"]![0]!["timing"] = "MeasuredSegment";
        var migrated = BundleStore.Parse(System.Text.Encoding.UTF8.GetBytes(legacy.ToJsonString()));
        h.Check(migrated.Schema == "wayscrawl.bundle/0.2" && migrated.Pins.Length == 0 && migrated.Transcript[0].Words is null,
            "0.2 migration supplies empty pins/absent words without relabelling the document.");
        legacy["transcript"] = new JsonArray(1);
        h.Reject(() => BundleStore.Parse(System.Text.Encoding.UTF8.GetBytes(legacy.ToJsonString())), "Malformed legacy segments are classified as invalid bundles.");
        var plan = KeyframePlanner.Plan(2_000_000, [], [], [], [], pins: [pin]);
        h.Check(plan.Selected.Any(f => f.Kind == "annotation" && f.AtUs < pin.RemovedUs), "Pins protect an annotation frame before fade.");
        h.Check(CropPlanner.Plan([], 1_200_000, 1920, 1080, pins: [pin]) is not null, "Pins create detail crops.");
        var states = new[] { new Stroke("s1", "g0", "#ff0000", 4, [new(1_000_000, 1, 1), new(1_100_000, 10, 10)], 1_200_000),
            new Stroke("s2", "g0", "#ff0000", 4, [new(1_250_000, 20, 20), new(1_300_000, 30, 30)], 1_500_000) };
        var separate = KeyframePlanner.Plan(2_000_000, states, [], [], []);
        h.Check(separate.Selected.Count(f => f.Kind == "annotation") == 2 && separate.Selected.Where(f => f.Kind == "annotation").First().AtUs < 1_200_000,
            "Removal boundaries split bursts and prevent nearby annotation states merging.");
        using var captured = new SKBitmap(200, 200);
        using (var canvas = new SKCanvas(captured)) { canvas.Clear(SKColors.White); InkRenderer.DrawPin(canvas, pin); }
        h.Check(EvidenceBuilder.DetectComposition(captured, [], 1_500_000, [pin]).Mode == CompositionMode.CapturedInk,
            "A pin-only captured source is recognized.");
        using (var canvas = new SKCanvas(captured)) canvas.Clear(SKColors.White);
        h.Check(EvidenceBuilder.DetectComposition(captured, [], 1_500_000, [pin]).Mode == CompositionMode.CleanSource,
            "A pin-only clean source needs vector composition.");
        string journalPath = Path.Combine(h.TempDirectory("pins"), "journal.jsonl");
        using (var writer = new JournalWriter(journalPath)) { writer.Pin(pin with { RemovedUs = null }); writer.Removed(1_800_000, [pin.Id], "fade"); }
        h.Check(SessionJournal.Read(journalPath).Pins.Single().RemovedUs == 1_800_000, "Pin removal survives the journal round trip.");
    }
}
