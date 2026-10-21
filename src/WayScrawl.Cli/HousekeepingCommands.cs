using WayScrawl.Application;
using WayScrawl.Core;
using WayScrawl.Platform;

namespace WayScrawl.Cli;

internal static partial class Commands
{
    public static async Task<int> WaybarAsync(WayScrawlPaths paths, bool follow, CancellationToken cancellationToken)
    {
        async Task<DaemonStatus> Query(CancellationToken token) =>
            (await DaemonClient.SendAsync(paths, DaemonCommands.Status, null, false, token)).Status;
        await foreach (string line in StatusFeed.LinesAsync(Query, follow, cancellationToken))
        { Console.WriteLine(line); await Console.Out.FlushAsync(cancellationToken); }
        return 0;
    }

    public static async Task<int> CleanAsync(WayScrawlPaths paths, string[] arguments, CancellationToken cancellationToken)
    {
        var options = new CliArguments(arguments, [], ["--dry-run", "--automatic"]);
        var config = WayScrawlConfig.Load(paths).Config;
        var report = await new RetentionCleaner(paths, config)
                         .RunAsync(DateTimeOffset.UtcNow, options.Has("--dry-run"), cancellationToken: cancellationToken);
        foreach (var action in report.Actions)
            Console.WriteLine(
                $"{(options.Has("--dry-run") ? "Planned" : action.Complete ? "Completed" : "Pending")} {action.Kind}: {action.Bundle}; " +
                $"freed data bytes: {action.FreedBytes}; bytes in Trash: {action.TrashedBytes}" +
                (action.TrashPath is null ? "" : " → " + action.TrashPath) + (action.Problem is null ? "" : "; " + action.Problem));
        string[] warnings = report.Actions.Where(a => a.Warning is not null).Select(a => a.Warning!).ToArray();
        foreach (string warning in warnings) Console.WriteLine("Warning: " + warning);
        if (options.Has("--automatic") && config.Notifications && warnings.Length > 0)
            await Desktop.NotifyAsync("WayScrawl retention", string.Join("\n", warnings));
        foreach (string skipped in report.Skipped) Console.WriteLine("Skipped: " + skipped);
        Console.WriteLine(RetentionReport.SpaceNote);
        return report.Actions.Any(a => !a.Complete) ? 1 : 0;
    }

    public static async Task<int> PinAsync(WayScrawlPaths paths, string[] arguments, CancellationToken cancellationToken)
    {
        var options = new CliArguments(arguments[1..], [], ["--off"]);
        string directory = await ResolveBundleAsync(paths, arguments[0], cancellationToken);
        using var lease = OperationLease.Acquire(directory, paths.Runtime);
        await BundleStore.ReadAsync(directory, cancellationToken);
        string file = Path.Combine(directory, ".pinned");
        if (PathSafety.IsLink(file)) throw new IOException("Pin path must not contain symlinks.");
        if (options.Has("--off")) File.Delete(file);
        else await File.WriteAllTextAsync(file, "Protected from automatic retention.\n", cancellationToken);
        Console.WriteLine((options.Has("--off") ? "Unpinned " : "Pinned ") + directory);
        return 0;
    }

    public static async Task<int> ExportAsync(WayScrawlPaths paths, string[] arguments, CancellationToken cancellationToken)
    {
        var options = new CliArguments(arguments[1..], ["--out", "--crop", "--size"], []);
        string directory = await ResolveBundleAsync(paths, arguments[0], cancellationToken);
        var bundle = await BundleStore.ReadAsync(directory, cancellationToken);
        string output = options.Get("--out", "");
        if (output.Length == 0)
        {
            string videos;
            try { videos = (await ProcessRunner.RunCheckedAsync("xdg-user-dir", ["VIDEOS"], cancellationToken)).Text.Trim(); }
            catch (System.ComponentModel.Win32Exception)
            {
                videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
            }
            if (!Path.IsPathRooted(videos)) throw new InvalidDataException("xdg-user-dir VIDEOS did not return an absolute directory.");
            output = Path.Combine(videos, "WayScrawl", BundleFileStem(bundle.Id) + ".mp4");
        }
        var result = await VideoExporter.ExportAsync(directory, output, options.Get("--crop", "auto"), options.Integer("--size", 1920),
            paths.Runtime, Console.Error.WriteLine, cancellationToken);
        Console.WriteLine($"{result.Path} ({result.Width}×{result.Height}, {result.Encoder})");
        if (result.Warning is not null) Console.Error.WriteLine(result.Warning);
        return 0;
    }
}
