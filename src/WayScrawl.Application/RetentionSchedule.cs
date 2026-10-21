using WayScrawl.Platform;

namespace WayScrawl.Application;

public static class RetentionSchedule
{
    public static async Task RunAsync(Action<string> log, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var (file, prefix) = SelfCommand.Resolve();
                    var result = await ProcessRunner.RunAsync("nice", ["-n", "19", "ionice", "-c", "3", file, .. prefix, "clean", "--automatic"], cancellationToken);
                    log("Retention: " + result.Text.Trim() + (result.ExitCode == 0 ? "" : "\n" + result.Error));
                }
                catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
                { log("Retention: " + error.Message); }
                await Task.Delay(TimeSpan.FromDays(1), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
