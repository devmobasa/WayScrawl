using WayScrawl.Platform;

namespace WayScrawl.Review;

// One entry in the bundle list. Records compare by value, so a refresh keeps unchanged entries,
// and with them the list's selection, in place.
public sealed record BundleListItem(string Location, string Title, string Detail, string? Error,
    DateTimeOffset SortTime)
{
    public bool IsValid => Error is null;
    public bool MediaRetired { get; init; }

    public static async Task<BundleListItem> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        string location = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        try
        {
            var bundle = await BundleStore.ReadAsync(location, cancellationToken);
            string detail = $"{bundle.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {Format.Clock(bundle.DurationUs)} · " +
                Format.Count(bundle.Keyframes.Length, "frame") + (bundle.Synthetic ? " · synthetic" : "");
            return new(location, Format.OneLine(bundle.Id), detail, null, bundle.CreatedUtc)
            { MediaRetired = File.Exists(Path.Combine(location, MediaFiles.RemovedNote)) };
        }
        catch (Exception error) when (Errors.IsBundle(error))
        {
            // Listed with its reason rather than hidden: a half-written or damaged bundle is evidence too.
            var modified = Directory.Exists(location) ? Directory.GetLastWriteTimeUtc(location) : DateTime.MinValue;
            return new(location, Path.GetFileName(location), "Unreadable bundle", error.Message,
                new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc)));
        }
    }
}

public static class BundleCatalog
{
    // Published bundles are <root>/<session>.wayscrawl; the writer stages them under hidden
    // ".<name>.<guid>.partial" names, which never match. Extra directories (a bundle named on
    // the command line) are listed even when they live elsewhere.
    public static async Task<List<BundleListItem>> ScanAsync(string root, IEnumerable<string> extra,
        CancellationToken cancellationToken)
    {
        var directories = new List<string>();
        if (Directory.Exists(root))
            directories.AddRange(Directory.EnumerateDirectories(root).Where(directory =>
                directory.EndsWith(".wayscrawl", StringComparison.Ordinal) &&
                !Path.GetFileName(directory).StartsWith('.')));
        directories.AddRange(extra);
        var items = new List<BundleListItem>();
        foreach (string directory in directories
                     .Select(d => Path.TrimEndingDirectorySeparator(Path.GetFullPath(d)))
                     .Distinct(StringComparer.Ordinal))
            items.Add(await BundleListItem.ReadAsync(directory, cancellationToken));
        return items.OrderByDescending(item => item.SortTime).ThenBy(item => item.Location, StringComparer.Ordinal)
            .ToList();
    }
}
