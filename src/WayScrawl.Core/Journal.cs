using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WayScrawl.Core;

public static class WayScrawlJson
{
    // Manifests write every member, including nulls, so required parameters always round-trip.
    public static readonly JsonSerializerOptions Options = Create(indented: true, JsonIgnoreCondition.Never);
    // Daemon protocol lines: one object per line, nulls kept like manifests.
    public static readonly JsonSerializerOptions Line = Create(indented: false, JsonIgnoreCondition.Never);
    // Journal lines omit nulls; every omitted member there has a default value.
    public static readonly JsonSerializerOptions Journal = Create(indented: false, JsonIgnoreCondition.WhenWritingNull);

    private static JsonSerializerOptions Create(bool indented, JsonIgnoreCondition ignore) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        DefaultIgnoreCondition = ignore,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
}

// Written by the daemon when recording starts and rewritten at each state change.
// The processing worker reads it together with journal.jsonl and the raw media.
public sealed record SessionRecord(
    string Id, DateTimeOffset CreatedUtc, string State, string Backend, CaptureSource Source,
    long OriginMonotonicUs, string VideoFile, string? Microphone, long? DurationUs,
    RecordingResult? Recording, Capability[] Capabilities, string[] Warnings, string? Bundle = null)
{
    public const string FileName = "session.json";
    public const string JournalName = "journal.jsonl";
    // Capture policy travels with the raw session; reprocessing must not adopt newer settings.
    public bool WindowTitles { get; init; }
    public double? InkFadeSeconds { get; init; }
}

// One line of journal.jsonl. Type selects which optional member is present.
public sealed record JournalEntry(string Type, long AtUs, TimelineEvent? Event = null,
    Stroke? Stroke = null, string[]? StrokeIds = null, string? Reason = null,
    InteractionSourceInfo? Source = null, Pin? Pin = null)
{
    public const string EventType = "event";
    public const string PinType = "pin";
    public const string StrokeType = "stroke";
    public const string RemovedType = "removed";
    public const string SourceType = "source";
}

public sealed record SessionJournal(TimelineEvent[] Events, Stroke[] Strokes, InteractionSourceInfo[] Sources)
{
    public Pin[] Pins { get; init; } = [];
    public const int MaxLineBytes = 1024 * 1024;

    // Tolerates a torn final line from a crash; any other malformed line is an error.
    public static SessionJournal Read(string path)
    {
        var events = new List<TimelineEvent>();
        var strokes = new Dictionary<string, Stroke>(StringComparer.Ordinal);
        var pins = new Dictionary<string, Pin>(StringComparer.Ordinal);
        var sources = new Dictionary<string, InteractionSourceInfo>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                JournalEntry? entry;
                try
                {
                    if (lines[i].Length > MaxLineBytes) throw new InvalidDataException("Journal line too long.");
                    entry = JsonSerializer.Deserialize<JournalEntry>(lines[i], WayScrawlJson.Journal);
                }
                catch (JsonException) when (i == lines.Length - 1)
                {
                    break;
                }
                catch (JsonException error)
                {
                    throw new InvalidDataException($"Invalid journal line {i + 1}: {error.Message}");
                }
                switch (entry)
                {
                    case { Type: JournalEntry.EventType, Event: { } item }:
                        events.Add(item);
                        break;
                    case { Type: JournalEntry.StrokeType, Stroke: { } stroke }:
                        strokes[stroke.Id] = stroke;
                        break;
                    case { Type: JournalEntry.PinType, Pin: { } pin }:
                        pins[pin.Id] = pin;
                        break;
                    case { Type: JournalEntry.RemovedType, StrokeIds: { } ids }:
                        foreach (string id in ids)
                        {
                            if (strokes.TryGetValue(id, out var removed) && removed.RemovedUs is null)
                                strokes[id] = removed with { RemovedUs = entry.AtUs };
                            if (pins.TryGetValue(id, out var removedPin) && removedPin.RemovedUs is null)
                                pins[id] = removedPin with { RemovedUs = entry.AtUs };
                        }
                        break;
                    case { Type: JournalEntry.SourceType, Source: { } source }:
                        sources[source.Name] = source;
                        break;
                    default:
                        throw new InvalidDataException($"Invalid journal line {i + 1}.");
                }
            }
        }
        return new(events.OrderBy(e => e.AtUs).ThenBy(e => e.Sequence).ToArray(),
            strokes.Values.OrderBy(s => s.Points[0].AtUs).ToArray(), sources.Values.ToArray()) { Pins = pins.Values.OrderBy(p => p.AtUs).ToArray() };
    }
}
