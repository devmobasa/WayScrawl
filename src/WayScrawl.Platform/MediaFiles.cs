using WayScrawl.Core;

namespace WayScrawl.Platform;

public static class MediaFiles
{
    public const string RemovedNote = "media-removed.json";
    public const string RetiredMessage = "Source media is retired by retention; check media-removed.json and Trash for completed or interrupted cleanup. Frames and the summary remain available.";
    public static (MediaAsset Asset, MediaStream Video, string Path) Require(string directory, Bundle bundle)
    {
        var asset = bundle.Media.FirstOrDefault(m => m.Streams.Any(s => s.Kind == "video"));
        if (asset is null) throw new InvalidDataException("This bundle has no retained source media (the synthetic demo has no video).");
        string path = System.IO.Path.Combine(directory, asset.Path);
        if (!File.Exists(path) || File.Exists(System.IO.Path.Combine(directory, RemovedNote)))
            throw new InvalidDataException(File.Exists(System.IO.Path.Combine(directory, RemovedNote))
                ? RetiredMessage
                : "The bundle's source media is missing.");
        return (asset, asset.Streams.First(s => s.Kind == "video"), path);
    }
}
