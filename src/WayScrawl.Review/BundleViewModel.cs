using WayScrawl.Core;
using WayScrawl.Platform;

namespace WayScrawl.Review;

public sealed class FrameItem(Keyframe frame)
{
    public Keyframe Frame { get; } = frame;
    public string Time { get; } = Format.Seconds(frame.AtUs);
    public string Reason { get; } = Format.OneLine(frame.Reason);
    public ImageSlot Thumbnail { get; } = new(frame.Path);
}

// The frame chosen in the strip: the full frame and, when the bundle has one, its detail crop.
public sealed class FrameDetail(FrameItem item) : IDisposable
{
    public string Caption { get; } = $"{item.Time} · {item.Frame.Path} · {item.Reason}";
    public ImageSlot Full { get; } = new(item.Frame.Path);
    public ImageSlot? Crop { get; } = item.Frame.Crop is { } crop ? new(crop.Path) : null;
    public bool HasCrop => Crop is not null;
    // Without a crop the full frame takes both columns.
    public int FullSpan => HasCrop ? 1 : 2;
    public string CropCaption { get; } = item.Frame.Crop is { } crop
        ? $"Detail crop · {crop.Path} · source {crop.X},{crop.Y} {crop.Width}x{crop.Height}"
        : "";

    public void Dispose()
    {
        Full.Dispose();
        Crop?.Dispose();
    }
}

// Everything the details pane shows for one bundle. The window and smoke mode load it the same
// way; only the image decoder differs.
public sealed class BundleViewModel : ViewModel, IDisposable
{
    public const int ThumbnailWidth = 400;

    private readonly ImageDecoder? decoder;
    private readonly CancellationTokenSource lifetime = new();
    private FrameItem? selectedFrame;
    private FrameDetail? selected;
    private bool disposed;

    private BundleViewModel(string location, Bundle bundle, ImageDecoder? decoder)
    {
        this.decoder = decoder;
        Location = location;
        Bundle = bundle;
        Title = Format.OneLine(bundle.Id);
        Subtitle = $"Recorded {bundle.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Format.Clock(bundle.DurationUs)} · " +
            $"{Format.Count(bundle.Keyframes.Length, "frame")} · {bundle.Schema}" + (bundle.Synthetic ? " · SYNTHETIC" : "");
        CompositionText = $"Composition: {bundle.Composition.Mode} — {Format.OneLine(bundle.Composition.Basis)}";
        Warnings = bundle.Warnings.Select(Format.OneLine).Concat(File.Exists(Path.Combine(location, MediaFiles.RemovedNote))
            ? [MediaFiles.RetiredMessage] : Array.Empty<string>()).ToArray();
        ContactSheet = new(Bundle.ContactSheet);
        Frames = bundle.Keyframes.Select(frame => new FrameItem(frame)).ToArray();
        Transcript = bundle.Transcript.Select(segment => new Row(
            $"{Format.Seconds(segment.StartUs)} – {Format.Seconds(segment.EndUs)}",
            Format.OneLine(segment.Text), segment.Timing.ToString())).ToArray();
        TranscriptNote = bundle.Transcription is { } info
            ? $"Transcription {Format.OneLine(info.Status)} · engine {info.Engine}, model {info.Model}, runtime {info.Runtime}, " +
              $"language {info.Language} — {Format.OneLine(info.Detail)}"
            : "No transcription metadata.";
        Events = bundle.Events.Select(item => new Row(Format.Seconds(item.AtUs), $"{item.Kind} — {Format.OneLine(item.Detail)}",
            $"{item.Source}, {item.Fidelity}")).ToArray();
        Capabilities = bundle.Capabilities.Select(capability => new Row(Format.OneLine(capability.Name),
            Format.OneLine(capability.Detail), capability.Status.ToString())).ToArray();
    }

    public string Location { get; }
    public Bundle Bundle { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string CompositionText { get; }
    public string[] Warnings { get; }
    public bool HasWarnings => Warnings.Length > 0;
    public ImageSlot ContactSheet { get; }
    public FrameItem[] Frames { get; }
    public Row[] Transcript { get; }
    public string TranscriptNote { get; }
    public Row[] Events { get; }
    public Row[] Capabilities { get; }

    public FrameItem? SelectedFrame
    {
        get => selectedFrame;
        set
        {
            // The strip writes null when its items are recreated; keep the current frame then.
            if (value is not null && value != selectedFrame) Forget(SelectFrameAsync(value));
        }
    }

    public FrameDetail? Selected { get => selected; private set => Set(ref selected, value); }

    // Reads and validates only manifest.json; images follow in LoadImagesAsync.
    public static async Task<BundleViewModel> LoadAsync(string directory, ImageDecoder? decoder,
        CancellationToken cancellationToken)
    {
        string location = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        // Parsing and validation run off the UI thread; a manifest may be up to 16 MiB.
        var bundle = await Task.Run(() => BundleStore.ReadAsync(location, cancellationToken), cancellationToken);
        return new(location, bundle, decoder);
    }

    // The first frame (the visible tab), then thumbnails in order, then the contact sheet.
    // One image at a time keeps memory bounded by a single 64 MiB asset.
    public async Task LoadImagesAsync()
    {
        var token = lifetime.Token;
        if (selectedFrame is null && Frames.Length > 0) await SelectFrameAsync(Frames[0]);
        foreach (var frame in Frames)
            await frame.Thumbnail.LoadAsync(Location, decoder, ThumbnailWidth, token);
        await ContactSheet.LoadAsync(Location, decoder, null, token);
    }

    public async Task SelectFrameAsync(FrameItem frame)
    {
        if (disposed) return;
        selectedFrame = frame;
        Raise(nameof(SelectedFrame));
        var detail = new FrameDetail(frame);
        var previous = Selected;
        Selected = detail;
        previous?.Dispose();
        var token = lifetime.Token;
        await detail.Full.LoadAsync(Location, decoder, null, token);
        if (detail.Crop is { } crop && Selected == detail)
            await crop.LoadAsync(Location, decoder, null, token);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Cancelled, not disposed: loads still in flight read its token afterwards.
        lifetime.Cancel();
        ContactSheet.Dispose();
        foreach (var frame in Frames) frame.Thumbnail.Dispose();
        Selected?.Dispose();
    }
}
