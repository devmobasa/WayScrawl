using System.Threading.Channels;

namespace WayScrawl.Core;

// No service silently substitutes synthetic data when a real backend is missing.
// Microphone is a source name, or null for no audio.
public sealed record CaptureRequest(string Output, string? Microphone, string WorkDirectory,
    int FramesPerSecond = 30);

public sealed record CaptureSource(string Backend, Geometry Geometry, CompositionMode Composition,
    string Detail);

// Finalized is false when the recorder had to be forced or died; the file is then partial.
public sealed record RecordingResult(string VideoPath, bool Finalized, ClockMap VideoClock,
    ClockMap? AudioClock, string[] Diagnostics);

public interface ICaptureBackend
{
    string Name { get; }
    Task<Capability[]> ProbeAsync(CancellationToken cancellationToken);
    // Selection/consent happens before media time zero is established.
    Task<IPreparedCapture> PrepareAsync(CaptureRequest request, CancellationToken cancellationToken);
}

public interface IPreparedCapture : IAsyncDisposable
{
    CaptureSource Source { get; }
    // The coordinator starts one clock after selection, then supplies it to all adapters.
    Task<IRecording> StartAsync(ISessionClock clock, CancellationToken cancellationToken);
}

public interface IRecording : IAsyncDisposable
{
    // Completes when the first frame is known to exist; faults if the recorder fails first.
    Task Ready { get; }
    // Completes when the recorder exits for any reason, including an unrequested exit.
    Task<RecordingResult> Completion { get; }
    // Returns only after the container has been finalized or the stop was forced.
    Task<RecordingResult> StopAsync(CancellationToken cancellationToken);
}

public enum OverlayInputMode { PassThrough, Drawing, Holding }

public abstract record OverlayEvent(long AtUs);
public sealed record StrokeCompleted(long AtUs, Stroke Stroke) : OverlayEvent(AtUs);
public sealed record PinPlaced(long AtUs, Pin Pin) : OverlayEvent(AtUs);
public sealed record StrokesRemoved(long AtUs, string[] StrokeIds, string Reason) : OverlayEvent(AtUs);
public sealed record ModeChanged(long AtUs, OverlayInputMode Mode, string Reason) : OverlayEvent(AtUs);
public sealed record MarkerRequested(long AtUs, string Detail) : OverlayEvent(AtUs);
public sealed record OverlayClosed(long AtUs, string Reason) : OverlayEvent(AtUs);

public interface IOverlayHost : IAsyncDisposable
{
    OverlayInputMode Mode { get; }
    int InkCount { get; }
    ChannelReader<OverlayEvent> Events { get; }
    // One surface on the recorded output. Strokes are reported in Geometry source pixels.
    Task ShowAsync(Geometry geometry, string outputName, ISessionClock clock, CancellationToken cancellationToken);
    // Completes after the compositor has received the new input region and keyboard policy.
    Task SetInputModeAsync(OverlayInputMode mode, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
    Task UndoAsync(CancellationToken cancellationToken);
    Task HideAsync(CancellationToken cancellationToken);
}

public sealed record TranscriptionResult(TranscriptSegment[] Segments, TranscriptionInfo Info);

public interface ITranscriber
{
    // Times are relative to the start of the supplied audio file.
    Task<TranscriptionResult> TranscribeAsync(string audioPath, CancellationToken cancellationToken);
}

public sealed record InteractionSample(long AtUs, string Source, string Fidelity,
    string Kind, string Payload, long Sequence);

public interface IInteractionSource
{
    Capability[] Describe();
    InteractionSourceInfo Statistics { get; }
    IAsyncEnumerable<InteractionSample> ReadAsync(ISessionClock clock, CancellationToken cancellationToken);
}

public interface IAgentSink
{
    // Destination is explicit; this contract does not infer a recipient.
    Task SendAsync(string bundleDirectory, string destination, CancellationToken cancellationToken);
}

// Ephemeral input control is separate from interaction observations: these events are never
// persisted. Only overlay ink can make a held drawing span part of a recording.
public sealed record HoldControlEvent(bool Pressed, string? Problem = null);
public interface IHoldControl
{
    IAsyncEnumerable<HoldControlEvent> ReadAsync(ISessionClock clock, CancellationToken cancellationToken);
}
