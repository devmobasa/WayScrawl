namespace WayScrawl.Core;

public static class BundleRules
{
    public const int MaxManifestBytes = 16 * 1024 * 1024;

    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ||
            path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl))
            return false;
        return path.Split('/').All(p => p.Length > 0 && p is not "." and not "..");
    }

    // One file directly under the named folder, e.g. frames/frame-00.png.
    public static bool IsAssetPath(string path, string folder, string extension) =>
        IsSafeRelativePath(path) && path.StartsWith(folder + "/", StringComparison.Ordinal) &&
        path.EndsWith(extension, StringComparison.Ordinal) && path.Count(c => c == '/') == 1;

    public static void Validate(Bundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.Schema is not (Bundle.CurrentSchema or "wayscrawl.bundle/0.2" or BundleV01.SchemaName))
            throw new InvalidDataException("Unknown schema.");
        if (string.IsNullOrWhiteSpace(bundle.Id) || bundle.DurationUs < 0 ||
            bundle.Geometries is null || bundle.Media is null || bundle.Composition?.Basis is null ||
            bundle.Pins is null || bundle.Strokes is null || bundle.Events is null || bundle.Sources is null ||
            bundle.Transcript is null || bundle.Keyframes is null || bundle.Omissions is null ||
            bundle.Capabilities is null || bundle.Warnings is null)
            throw new InvalidDataException("Missing or invalid bundle fields.");

        var geometries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var geometry in bundle.Geometries)
        {
            if (geometry is null || string.IsNullOrWhiteSpace(geometry.Id) || !geometries.Add(geometry.Id) ||
                geometry.Source is null || geometry.FromUs < 0 || geometry.ToUs <= geometry.FromUs ||
                geometry.Transform is < 0 or > 7)
                throw new InvalidDataException("Invalid geometry revision.");
            geometry.ToSource(new(geometry.LogicalX, geometry.LogicalY));
        }
        if (geometries.Count == 0) throw new InvalidDataException("No geometry.");

        var media = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal) { Bundle.ContactSheet };
        foreach (var asset in bundle.Media)
        {
            if (asset is null || string.IsNullOrWhiteSpace(asset.Id) || !media.Add(asset.Id) ||
                asset.Path is null || !IsSafeRelativePath(asset.Path) ||
                !asset.Path.StartsWith("media/", StringComparison.Ordinal) || !paths.Add(asset.Path) ||
                string.IsNullOrWhiteSpace(asset.Mime) || asset.Bytes < 0 || asset.Backend is null ||
                asset.Sha256 is null || asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit) ||
                asset.Finalization is not ("complete" or "partial") || asset.Streams is null)
                throw new InvalidDataException("Invalid media asset.");
            foreach (var stream in asset.Streams)
                if (stream is null || stream.Kind is not ("video" or "audio") || stream.Codec is null ||
                    stream.TimeBase is null || stream.DurationUs < 0 || stream.Clock is null ||
                    stream.Clock.Clock is null || stream.Clock.Method is null || stream.Clock.UncertaintyUs < 0)
                    throw new InvalidDataException("Invalid media stream.");
        }

        if (bundle.Capabilities.Any(c => c is null || string.IsNullOrWhiteSpace(c.Name) || c.Detail is null) ||
            bundle.Warnings.Any(w => w is null))
            throw new InvalidDataException("Invalid capability or warning.");
        if (bundle.Sources.Any(s => s is null || string.IsNullOrWhiteSpace(s.Name) || s.Fidelity is null ||
                s.Detail is null || s.Received < 0 || s.Dropped < 0 || s.Gaps < 0))
            throw new InvalidDataException("Invalid interaction source.");
        if (bundle.Omissions.Any(o => o is null || o.AtUs < 0 || string.IsNullOrWhiteSpace(o.Reason) || o.Detail is null))
            throw new InvalidDataException("Invalid omission.");
        if (bundle.Transcription is { } info && (info.Status is null || info.Engine is null ||
                info.Runtime is null || info.Model is null || info.Language is null ||
                info.Extraction is null || info.Detail is null))
            throw new InvalidDataException("Invalid transcription metadata.");

        if (bundle.Keyframes.Length == 0) throw new InvalidDataException("No keyframes.");
        long lastFrame = -1;
        foreach (var frame in bundle.Keyframes)
        {
            if (frame is null || frame.AtUs < 0 || frame.AtUs < lastFrame || frame.AtUs > bundle.DurationUs ||
                !geometries.Contains(frame.GeometryId) || string.IsNullOrWhiteSpace(frame.Reason) ||
                !IsAssetPath(frame.Path, "frames", ".png") || !paths.Add(frame.Path) ||
                frame.MediaId is not null && !media.Contains(frame.MediaId) || frame.Width < 0 || frame.Height < 0)
                throw new InvalidDataException("Invalid keyframe.");
            if (frame.Crop is { } crop && (!IsAssetPath(crop.Path, "frames", ".png") || !paths.Add(crop.Path) ||
                    crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 ||
                    crop.ExportWidth <= 0 || crop.ExportHeight <= 0))
                throw new InvalidDataException("Invalid keyframe crop.");
            lastFrame = frame.AtUs;
        }

        long lastEvent = -1;
        foreach (var item in bundle.Events)
        {
            if (item is null || item.AtUs < lastEvent || item.AtUs > bundle.DurationUs ||
                item.AtUs < 0 || string.IsNullOrWhiteSpace(item.Kind) || item.Detail is null ||
                string.IsNullOrWhiteSpace(item.Source) || string.IsNullOrWhiteSpace(item.Fidelity))
                throw new InvalidDataException("Invalid or unordered event.");
            lastEvent = item.AtUs;
        }

        var strokeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stroke in bundle.Strokes)
        {
            if (stroke is null || string.IsNullOrWhiteSpace(stroke.Id) || !strokeIds.Add(stroke.Id) ||
                !geometries.Contains(stroke.GeometryId) ||
                !double.IsFinite(stroke.Width) || stroke.Width <= 0 || !IsColor(stroke.Color) ||
                stroke.Points is null || stroke.Points.Length < 1)
                throw new InvalidDataException("Invalid stroke.");
            long last = -1;
            foreach (var p in stroke.Points)
            {
                if (p is null || p.AtUs < last || p.AtUs < 0 || p.AtUs > bundle.DurationUs ||
                    !double.IsFinite(p.X) || !double.IsFinite(p.Y))
                    throw new InvalidDataException("Invalid stroke point.");
                last = p.AtUs;
            }
            if (stroke.RemovedUs is { } removed && (removed < stroke.Points[0].AtUs || removed > bundle.DurationUs))
                throw new InvalidDataException("Invalid stroke removal time.");
        }

        var pinNumbers = new HashSet<int>();
        foreach (var pin in bundle.Pins)
        {
            var geometry = pin is null ? null : bundle.Geometries.FirstOrDefault(g => g.Id == pin.GeometryId);
            if (pin is null || string.IsNullOrWhiteSpace(pin.Id) || !strokeIds.Add(pin.Id) || geometry is null ||
                pin.Number <= 0 || !pinNumbers.Add(pin.Number) || !double.IsFinite(pin.X) || !double.IsFinite(pin.Y) ||
                pin.X < 0 || pin.X > geometry.PixelWidth || pin.Y < 0 || pin.Y > geometry.PixelHeight ||
                pin.AtUs < 0 || pin.AtUs > bundle.DurationUs ||
                pin.RemovedUs is { } removed && (removed < pin.AtUs || removed > bundle.DurationUs))
                throw new InvalidDataException("Invalid pin.");
        }
        foreach (var segment in bundle.Transcript)
        {
            if (segment is null || segment.StartUs < 0 || segment.EndUs < segment.StartUs ||
                segment.EndUs > bundle.DurationUs || segment.Text is null)
                throw new InvalidDataException("Invalid transcript segment.");
            if (segment.Words is not { } words) continue;
            if (words.Length == 0 || segment.Timing != TimingQuality.MeasuredWord)
                throw new InvalidDataException("Words require measured word timing.");
            long previous = segment.StartUs;
            foreach (var word in words)
            {
                if (word is null || string.IsNullOrWhiteSpace(word.Text) || word.StartUs < previous ||
                    word.EndUs < word.StartUs || word.EndUs > segment.EndUs)
                    throw new InvalidDataException("Invalid or unordered transcript word.");
                previous = word.EndUs;
            }
        }
    }

    public static bool IsColor(string? color) =>
        color is { Length: 7 } && color[0] == '#' && color.AsSpan(1).ToArray().All(Uri.IsHexDigit);
}
