namespace WayScrawl.Core;

public enum TimingQuality { Unavailable, Estimated, MeasuredSegment, MeasuredWord, Synthetic }
public enum Availability { Unknown, Unsupported, Available, Denied, NotImplemented }
public enum ClockQuality { Unavailable, Estimated, Measured, Synthetic }

// CapturedInk: the overlay is already in the source pixels, so exporters must not redraw it.
// CleanSource: ink is absent from the pixels and exporters composite the vectors.
// Unknown: not established for this capture source; exporters must not guess.
public enum CompositionMode { Unknown, CapturedInk, CleanSource, NoInk }

public readonly record struct PointD(double X, double Y);

// One upright source image and its mapping from global logical coordinates.
// Revisions are valid for [FromUs, ToUs); ToUs null means until the session ends.
public sealed record Geometry(
    string Id, double LogicalX, double LogicalY,
    double LogicalWidth, double LogicalHeight, int PixelWidth, int PixelHeight,
    string Source = "", int Transform = 0, long FromUs = 0, long? ToUs = null)
{
    // Input is global logical space; output is upright source pixel EDGE space.
    // Rotation must already have been normalized by the capture adapter.
    public PointD ToSource(PointD point)
    {
        if (!double.IsFinite(LogicalX) || !double.IsFinite(LogicalY) ||
            !double.IsFinite(LogicalWidth) || !double.IsFinite(LogicalHeight) ||
            LogicalWidth <= 0 || LogicalHeight <= 0 || PixelWidth <= 0 || PixelHeight <= 0 ||
            !double.IsFinite(point.X) || !double.IsFinite(point.Y))
            throw new ArgumentOutOfRangeException(nameof(point), "Invalid geometry or point.");

        return new((point.X - LogicalX) * PixelWidth / LogicalWidth,
                   (point.Y - LogicalY) * PixelHeight / LogicalHeight);
    }
}

public sealed record InkPoint(long AtUs, double X, double Y);

// Points are source pixel-edge coordinates. RemovedUs is the exclusive end of visibility
// (undo or clear); null means the stroke stays visible until the session ends.
public sealed record Stroke(string Id, string GeometryId, string Color, double Width,
    InkPoint[] Points, long? RemovedUs = null)
{
    public bool VisibleAt(long atUs) => Points.Length > 0 && Points[0].AtUs <= atUs &&
        (RemovedUs is null || atUs < RemovedUs);
}

// Fidelity: exact (owned clock), arrival (stamped when received), coarse (sampled or
// aggregated), synthetic. Source names the producer; observations from different
// sources are never merged into one claim.
public sealed record TimelineEvent(long AtUs, string Kind, string Detail,
    string Source = "coordinator", string Fidelity = "exact", long Sequence = 0);

public sealed record TranscriptWord(long StartUs, long EndUs, string Text);
public sealed record TranscriptSegment(long StartUs, long EndUs, string Text, TimingQuality Timing,
    TranscriptWord[]? Words = null);

public sealed record Pin(string Id, string GeometryId, int Number, double X, double Y, long AtUs, long? RemovedUs = null)
{
    public bool VisibleAt(long atUs) => AtUs <= atUs && (RemovedUs is null || atUs < RemovedUs);
}

public sealed record CropRegion(string Path, int X, int Y, int Width, int Height,
    int ExportWidth, int ExportHeight);

// AtUs is the session time the frame represents. RequestedUs is what selection asked for;
// PtsUs is the presentation time actually decoded from MediaId's video stream.
public sealed record Keyframe(long AtUs, string Path, string GeometryId, string Reason,
    long? RequestedUs = null, long? PtsUs = null, string? MediaId = null,
    int Width = 0, int Height = 0, CropRegion? Crop = null);

public sealed record Capability(string Name, Availability Status, string Detail);

// sessionUs = streamPtsUs + OffsetUs. UncertaintyUs bounds the error of that mapping.
public sealed record ClockMap(long OffsetUs, ClockQuality Quality, string Clock, string Method,
    long UncertaintyUs);

public sealed record MediaStream(string Kind, string Codec, string TimeBase, long FirstPtsUs,
    long DurationUs, ClockMap Clock, int Width = 0, int Height = 0, double FramesPerSecond = 0,
    int SampleRate = 0, int Channels = 0);

// Finalization: complete (recorder exited cleanly and the container probes), partial.
public sealed record MediaAsset(string Id, string Path, string Mime, long Bytes, string Sha256,
    string Finalization, string Backend, MediaStream[] Streams);

public sealed record Composition(CompositionMode Mode, string Basis);

public sealed record InteractionSourceInfo(string Name, string Fidelity, long Received,
    long Dropped, long Gaps, string Detail);

public sealed record TranscriptionInfo(string Status, string Engine, string Runtime, string Model,
    string Language, string Extraction, string Detail);

public sealed record Omission(long AtUs, string Reason, string Detail);

public sealed record Bundle(
    string Schema, string Id, bool Synthetic, DateTimeOffset CreatedUtc, long DurationUs,
    Geometry[] Geometries, MediaAsset[] Media, Composition Composition, Stroke[] Strokes,
    TimelineEvent[] Events, InteractionSourceInfo[] Sources, TranscriptionInfo? Transcription,
    TranscriptSegment[] Transcript, Keyframe[] Keyframes, Omission[] Omissions,
    Capability[] Capabilities, string[] Warnings)
{
    [System.Text.Json.Serialization.JsonRequired]
    public Pin[] Pins { get; init; } = [];
    public bool WindowTitles { get; init; }
    public const string CurrentSchema = "wayscrawl.bundle/0.3";
    public const string ContactSheet = "contact-sheet.png";
}

// The 0.1 document shape, kept only so older bundles can be read and migrated explicitly.
public sealed record BundleV01(
    string Schema, string Id, bool Synthetic, DateTimeOffset CreatedUtc,
    long DurationUs, Geometry Geometry, Stroke[] Strokes,
    TimelineEvent[] Events, TranscriptSegment[] Transcript, Keyframe[] Keyframes,
    Capability[] Capabilities, string[] Warnings)
{
    public const string SchemaName = "wayscrawl.bundle/0.1";

    // In memory only. The file on disk keeps its 0.1 label.
    public Bundle Migrate() => new(Schema, Id, Synthetic, CreatedUtc, DurationUs,
        [Geometry], [], new Composition(Synthetic ? CompositionMode.CapturedInk : CompositionMode.Unknown,
            "Migrated from wayscrawl.bundle/0.1, which did not record composition."),
        Strokes, Events, [], null, Transcript, Keyframes, [], Capabilities, Warnings);
}
