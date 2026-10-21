using System.Text.Json;
using WayScrawl.Application;
using WayScrawl.Core;
using WayScrawl.Platform;
using WayScrawl.Review;

namespace WayScrawl.Checks;

// The review app's headless smoke mode loads bundles through the same view-model code as the window.
internal static class ReviewChecks
{
    public static async Task RunAsync(Harness h)
    {
        h.Section = "review";
        string root = OverlayChecks.RepositoryRoot();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        string review = Path.Combine(root, "src", "WayScrawl.Review", "bin", configuration, "net11.0", "wayscrawl-review");
        if (!File.Exists(review))
        {
            Console.WriteLine("Skipped review checks: build src/WayScrawl.Review first.");
            return;
        }
        string work = h.TempDirectory("review");
        string bundle = Path.Combine(work, "demo.wayscrawl");
        await Demo.CreateAsync(bundle);
        var ok = await ProcessRunner.RunAsync(review, ["--smoke", bundle], CancellationToken.None);
        h.Check(ok.ExitCode == 0 && ok.Text.Contains("3 frames"), "The review app loads a bundle headlessly: " + ok.Text.Trim());
        var paths = new WayScrawlPaths(Path.Combine(work, "run"), Path.Combine(work, "state"),
            Path.Combine(work, "data"), Path.Combine(work, "config"));
        using var model = new MainViewModel(paths, work, bundle, cli: null, decoder: null, notice: null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await model.RefreshAsync();
        model.SelectedBundle = model.Bundles.Single();
        while (model.IsLoading || model.Details?.ContactSheet.Verified == false) await Task.Delay(10, timeout.Token);
        h.Check(model.Details is not null && !model.Details.Warnings.Contains(MediaFiles.RetiredMessage),
            "The selected bundle starts with loaded details and no retirement warning.");
        var manifest = await BundleStore.ReadAsync(bundle);
        var removal = new MediaRemoval(MediaRemoval.SchemaName, Guid.NewGuid().ToString("N"), manifest.Id,
            manifest.CreatedUtc, DateTimeOffset.UtcNow, [], [], false, RetentionStage.Complete, [], [], []);
        await File.WriteAllTextAsync(Path.Combine(bundle, MediaFiles.RemovedNote), JsonSerializer.Serialize(removal, WayScrawlJson.Options));
        await model.RefreshAsync();
        while (model.IsLoading || model.Details?.ContactSheet.Verified == false) await Task.Delay(10, timeout.Token);
        h.Check(model.SelectedBundle?.Location == bundle && model.Details?.Warnings.Contains(MediaFiles.RetiredMessage) == true,
            "Refreshing the selected bundle reloads details when retention adds its media-removal marker.");
        var retired = await ProcessRunner.RunAsync(review, ["--smoke", bundle], CancellationToken.None);
        h.Check(retired.ExitCode == 0 && retired.Text.Contains(MediaFiles.RetiredMessage),
            "Review keeps evidence readable and shows the authoritative media-retirement warning.");
        var missing = await ProcessRunner.RunAsync(review, ["--smoke", work], CancellationToken.None);
        h.Check(missing.ExitCode == 1, "A directory without a manifest is rejected.");
        string frame = Path.Combine(bundle, "frames", "frame-01.png");
        File.Delete(frame);
        File.CreateSymbolicLink(frame, "/etc/hostname");
        var tampered = await ProcessRunner.RunAsync(review, ["--smoke", bundle], CancellationToken.None);
        h.Check(tampered.ExitCode == 1 && tampered.Error.Contains("symbolic link"), "A symlinked frame is never opened: " + tampered.Error.Trim());
    }
}
