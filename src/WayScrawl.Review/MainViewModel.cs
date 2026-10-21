using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using WayScrawl.Application;
using WayScrawl.Platform;

namespace WayScrawl.Review;

public sealed class MainViewModel : ViewModel, IDisposable
{
    private readonly ImageDecoder? decoder;
    private readonly string[] extra;
    private readonly string? startBundle;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Timer debounce;
    private FileSystemWatcher? watcher;
    private CancellationTokenSource? loading;
    private BundleListItem? selectedBundle;
    private BundleViewModel? details;
    private string? detailsError;
    private string message;
    private bool isLoading, scanned, syncing, refreshing, refreshAgain;

    public MainViewModel(WayScrawlPaths paths, string bundleRoot, string? startBundle,
        (string File, string[] Prefix)? cli, ImageDecoder? decoder, string? notice)
    {
        BundleRoot = bundleRoot;
        this.decoder = decoder;
        this.startBundle = startBundle is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(startBundle));
        extra = this.startBundle is null ? [] : [this.startBundle];
        message = notice ?? "";
        Recorder = new RecorderViewModel(paths, cli, Report);
        Recorder.BundlePublished += () => Forget(RefreshAsync());
        RefreshCommand = new AsyncCommand(RefreshAsync);
        CopyCommand = new AsyncCommand(CopyAsync, () => Details is not null);
        OpenFolderCommand = new AsyncCommand(OpenFolderAsync, () => SelectedBundle is not null);
        // Publishing renames a staging directory into place and deletion removes many entries;
        // wait for the burst to settle, then rescan once.
        debounce = new Timer(_ => Dispatcher.UIThread.Post(() => Forget(RefreshAsync())));
    }

    public string BundleRoot { get; }
    public ObservableCollection<BundleListItem> Bundles { get; } = [];
    public RecorderViewModel Recorder { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand CopyCommand { get; }
    public AsyncCommand OpenFolderCommand { get; }
    public bool IsEmpty => scanned && Bundles.Count == 0;
    public string EmptyText => $"No bundles in {BundleRoot} yet. Record one, or run wayscrawl demo DIR and open it.";
    public string Message { get => message; private set => Set(ref message, value); }
    public bool IsLoading { get => isLoading; private set => Set(ref isLoading, value); }
    public string? DetailsError { get => detailsError; private set => Set(ref detailsError, value); }

    public BundleListItem? SelectedBundle
    {
        get => selectedBundle;
        set
        {
            // The list clears its selection while entries move during a refresh; ApplyScan restores it.
            if (syncing || !Set(ref selectedBundle, value)) return;
            OpenFolderCommand.RaiseCanExecuteChanged();
            Forget(LoadDetailsAsync(value));
        }
    }

    public BundleViewModel? Details
    {
        get => details;
        private set
        {
            if (Set(ref details, value)) CopyCommand.RaiseCanExecuteChanged();
        }
    }

    public void Start()
    {
        Forget(RefreshAsync());
        Forget(Recorder.PollAsync(lifetime.Token));
    }

    public async Task RefreshAsync()
    {
        if (refreshing)
        {
            refreshAgain = true;
            return;
        }
        refreshing = true;
        try
        {
            do
            {
                refreshAgain = false;
                var items = await Task.Run(() => BundleCatalog.ScanAsync(BundleRoot, extra, lifetime.Token), lifetime.Token);
                ApplyScan(items);
            }
            while (refreshAgain);
            Watch();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (Errors.IsBundle(error))
        {
            Report($"Could not list {BundleRoot}: {error.Message}");
        }
        finally
        {
            refreshing = false;
        }
    }

    // Updates the collection in place so the list keeps its selection and scroll position.
    private void ApplyScan(List<BundleListItem> items)
    {
        var previous = selectedBundle;
        syncing = true;
        try
        {
            for (int i = Bundles.Count - 1; i >= 0; i--)
                if (!items.Contains(Bundles[i])) Bundles.RemoveAt(i);
            for (int i = 0; i < items.Count; i++)
            {
                int at = Bundles.IndexOf(items[i]);
                if (at == i) continue;
                if (at >= 0) Bundles.Move(at, i);
                else Bundles.Insert(i, items[i]);
            }
        }
        finally
        {
            syncing = false;
        }
        bool first = !scanned;
        scanned = true;
        Raise(nameof(IsEmpty));
        if (first)
        {
            SelectedBundle = Bundles.FirstOrDefault(b => b.Location == startBundle) ?? Bundles.FirstOrDefault();
            return;
        }
        var match = previous is null ? null : Bundles.FirstOrDefault(b => b.Location == previous.Location);
        if (ReferenceEquals(match, previous)) Raise(nameof(SelectedBundle));
        else SelectedBundle = match; // Changed on disk (reload) or gone (clear).
    }

    private void Watch()
    {
        if (!Directory.Exists(BundleRoot))
        {
            watcher?.Dispose();
            watcher = null;
            return;
        }
        if (watcher is not null) return;
        // Top level only: a bundle appears by one rename, and its contents never change afterwards.
        watcher = new FileSystemWatcher(BundleRoot)
        {
            NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
            IncludeSubdirectories = false
        };
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnChanged;
        // An overflowed inotify queue loses events; a full rescan recovers.
        watcher.Error += (_, _) => debounce.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        watcher.EnableRaisingEvents = true;
    }

    private void OnChanged(object? sender, FileSystemEventArgs e) =>
        debounce.Change(TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);

    private async Task LoadDetailsAsync(BundleListItem? item)
    {
        loading?.Cancel();
        var cancellation = loading = new CancellationTokenSource();
        var previous = Details;
        Details = null;
        previous?.Dispose();
        DetailsError = item?.Error;
        IsLoading = item is { IsValid: true };
        if (!IsLoading) return;
        try
        {
            var loaded = await BundleViewModel.LoadAsync(item!.Location, decoder, cancellation.Token);
            if (cancellation.IsCancellationRequested)
            {
                loaded.Dispose();
                return;
            }
            Details = loaded;
            IsLoading = false;
            await loaded.LoadImagesAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error) when (Errors.IsBundle(error))
        {
            if (cancellation.IsCancellationRequested) return;
            DetailsError = error.Message;
            IsLoading = false;
        }
    }

    private async Task CopyAsync()
    {
        if (Details is not { } bundle) return;
        try
        {
            await new ClipboardSink().SendAsync(bundle.Location, "clipboard", lifetime.Token);
            Report($"Copied an agent prompt for {bundle.Title} to the clipboard.");
        }
        catch (Exception error) when (Errors.IsAction(error) || error is OperationCanceledException)
        {
            Report("Copy failed: " + error.Message);
        }
    }

    private Task OpenFolderAsync()
    {
        if (SelectedBundle is not { } item) return Task.CompletedTask;
        if (!Directory.Exists(item.Location))
        {
            Report(item.Location + " does not exist.");
            return Task.CompletedTask;
        }
        try
        {
            // ArgumentList, not a shell string: the path reaches xdg-open as one argument.
            var info = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            info.ArgumentList.Add(item.Location);
            using var process = Spawn.Start(info);
            Report("Opened " + item.Location);
        }
        catch (Exception error) when (Errors.IsAction(error))
        {
            Report("Open folder failed: " + error.Message);
        }
        return Task.CompletedTask;
    }

    private void Report(string text) => Message = Format.OneLine(text);

    // Stops polling and watching only. Nothing here talks to the daemon, so a recording
    // in progress carries on after the window closes.
    public void Dispose()
    {
        lifetime.Cancel();
        loading?.Cancel();
        watcher?.Dispose();
        debounce.Dispose();
        Details?.Dispose();
    }
}
