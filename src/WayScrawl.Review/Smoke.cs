namespace WayScrawl.Review;

// wayscrawl-review --smoke BUNDLE_DIR: the list entry and details pane load exactly as in the
// window, including every image path and size check, but nothing is decoded because the
// Avalonia platform is never initialized. Exit 0 with a one-line summary, or 1 with the error.
internal static class Smoke
{
    public static async Task<int> RunAsync(string directory)
    {
        try
        {
            var item = await BundleListItem.ReadAsync(directory, CancellationToken.None);
            if (item.Error is not null)
            {
                Console.Error.WriteLine($"{item.Location}: {item.Error}");
                return 1;
            }
            using var details = await BundleViewModel.LoadAsync(item.Location, decoder: null, CancellationToken.None);
            await details.LoadImagesAsync();
            var images = new List<ImageSlot> { details.ContactSheet };
            images.AddRange(details.Frames.Select(frame => frame.Thumbnail));
            foreach (var frame in details.Frames)
            {
                await details.SelectFrameAsync(frame);
                images.Add(details.Selected!.Full);
                if (details.Selected.Crop is { } crop) images.Add(crop);
            }
            if (images.FirstOrDefault(image => !image.Verified) is { } failed)
            {
                Console.Error.WriteLine($"{item.Location}: {failed.Error ?? failed.RelativePath + " was not loaded."}");
                return 1;
            }
            var bundle = details.Bundle;
            int distinct = images.Select(image => image.RelativePath).Distinct(StringComparer.Ordinal).Count();
            Console.WriteLine($"{details.Title}: {Format.Count(bundle.Keyframes.Length, "frame")}, " +
                $"{Format.Count(bundle.Transcript.Length, "transcript segment")}, {Format.Count(bundle.Events.Length, "event")}, " +
                $"{Format.Count(distinct, "image")} checked ({bundle.Schema}{(bundle.Synthetic ? ", synthetic" : "")})");
            foreach (string warning in details.Warnings) Console.WriteLine("Warning: " + warning);
            return 0;
        }
        catch (Exception error) when (Errors.IsBundle(error))
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
