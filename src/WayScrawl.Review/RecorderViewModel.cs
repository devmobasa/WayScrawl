using WayScrawl.Application;
using WayScrawl.Core;

namespace WayScrawl.Review;

// A separate client of the daemon, like the CLI. It only sends what the user clicks, so closing
// the window never stops or cancels a recording; a daemon it starts is detached by setsid.
public sealed class RecorderViewModel : ViewModel
{
    private readonly WayScrawlPaths paths;
    // The wayscrawl CLI hosts the daemon. Without it a stopped daemon cannot be started from here:
    // DaemonClient's default would relaunch this executable instead.
    private readonly (string File, string[] Prefix)? cli;
    private readonly Action<string> report;
    private DaemonStatus? status;
    private string statusText = "Connecting to the daemon…";
    private string? lastBundle;
    private bool observed;

    public RecorderViewModel(WayScrawlPaths paths, (string File, string[] Prefix)? cli, Action<string> report)
    {
        this.paths = paths;
        this.cli = cli;
        this.report = report;
        ToggleCommand = new AsyncCommand(ToggleAsync);
        DrawCommand = new AsyncCommand(DrawAsync, () => IsRecording && status?.Draw is not null);
    }

    public AsyncCommand ToggleCommand { get; }
    public AsyncCommand DrawCommand { get; }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public string? LastError => status?.LastError is { } error ? Format.OneLine(error) : null;
    public bool IsRecording => status?.State is "Preparing" or "Recording";
    public bool IsDrawing => status?.Draw is OverlayInputMode.Drawing or OverlayInputMode.Holding;
    public string RecordLabel => IsRecording ? "■ Stop" : "● Record";
    public string DrawLabel => status?.Draw == OverlayInputMode.Holding ? "Draw: hold" : IsDrawing ? "Draw: on" : "Draw: off";

    // Raised when the daemon reports a newly published bundle, in case the watcher missed it.
    public event Action? BundlePublished;

    // Polls once a second until cancelled. The query runs on the thread pool; results are
    // applied on the caller's (UI) context.
    public async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                var (current, problem) = await Task.Run(() => QueryAsync(cancellationToken), cancellationToken);
                Apply(current, problem);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task<(DaemonStatus?, string?)> QueryAsync(CancellationToken cancellationToken)
    {
        // A wedged daemon must not stall the indicator for DaemonClient's 60 s reply timeout.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var response = await DaemonClient.SendAsync(paths, DaemonCommands.Status, null, false, timeout.Token);
            return (response.Status, null);
        }
        catch (DaemonUnavailableException)
        {
            return (null, "Daemon not running");
        }
        // Includes ArgumentException: an over-long socket path must show here, not close the window.
        catch (Exception error) when (Errors.IsAction(error) ||
                                      error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (null, "Daemon not responding: " + error.Message);
        }
    }

    private void Apply(DaemonStatus? current, string? problem)
    {
        status = current;
        if (current is null) StatusText = problem ?? "Daemon not running";
        else
        {
            string state = current.State switch
            {
                "Preparing" or "Recording" => $"{current.State} {Format.Clock(current.ElapsedUs)}",
                _ => current.State
            };
            StatusText = $"{state} · processing {current.Processing}";
            // The first status only establishes what already exists; any later change, including the
            // first bundle ever published, refreshes the list.
            if (current.LastBundle != lastBundle)
            {
                lastBundle = current.LastBundle;
                if (observed && lastBundle is not null) BundlePublished?.Invoke();
            }
            observed = true;
        }
        Raise(nameof(LastError));
        Raise(nameof(IsRecording));
        Raise(nameof(IsDrawing));
        Raise(nameof(RecordLabel));
        Raise(nameof(DrawLabel));
        DrawCommand.RaiseCanExecuteChanged();
    }

    private async Task ToggleAsync()
    {
        // Send what the button showed, never a toggle: with a stale label a toggle could stop a
        // recording the user meant to keep, or start one after another client stopped it.
        bool stop = IsRecording;
        try
        {
            var response = await Task.Run(() => DaemonClient.SendAsync(paths,
                stop ? DaemonCommands.Stop : DaemonCommands.Start, null,
                autoStart: !stop && cli is not null, CancellationToken.None, cli));
            Apply(response.Status, null);
            report(response.Message);
        }
        catch (OperationCanceledException)
        {
            // The reply did not arrive in time; the outcome is unknown, so only refresh status.
            report("The daemon did not answer in time; check the status line.");
            var (current, problem) = await Task.Run(() => QueryAsync(CancellationToken.None));
            Apply(current, problem);
        }
        catch (DaemonUnavailableException error) when (cli is null)
        {
            report(error.Message + " The wayscrawl command was not found next to wayscrawl-review to start it.");
        }
        catch (Exception error) when (Errors.IsAction(error))
        {
            report("Record/stop failed: " + error.Message);
        }
    }

    private async Task DrawAsync()
    {
        try
        {
            var response = await Task.Run(() => DaemonClient.SendAsync(paths, DaemonCommands.Draw,
                IsDrawing ? "off" : "on", autoStart: false, CancellationToken.None));
            Apply(response.Status, null);
            report(response.Message);
        }
        catch (OperationCanceledException)
        {
            report("The daemon did not answer in time; check the status line.");
            var (current, problem) = await Task.Run(() => QueryAsync(CancellationToken.None));
            Apply(current, problem);
        }
        catch (Exception error) when (Errors.IsAction(error))
        {
            report("Draw toggle failed: " + error.Message);
        }
    }
}
