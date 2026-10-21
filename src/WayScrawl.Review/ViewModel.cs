using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Media.Imaging;

namespace WayScrawl.Review;

public abstract class ViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    // Fire-and-forget from property setters and events. async void, so an unexpected fault
    // reaches the dispatcher's unhandled-exception path instead of vanishing with the task.
    protected static async void Forget(Task task) => await task;
}

// Refuses re-entry, so a slow daemon reply cannot queue a second toggle behind the first.
// Actions report their own expected failures; anything else is a bug and reaches the dispatcher.
public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool running;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true;
        RaiseCanExecuteChanged();
        try { await execute(); }
        finally
        {
            running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

// Bitmap decoding needs the Avalonia platform, which smoke mode never starts. Width, when given,
// is a target for thumbnails.
public delegate Bitmap ImageDecoder(PngAsset png, int? width);

// One bundle image. Reading, checking and decoding happen off the UI thread; the result is
// published on the caller's context, which is the UI thread in the window.
public sealed class ImageSlot(string relativePath) : ViewModel, IDisposable
{
    private Bitmap? image;
    private string? error;
    private bool disposed;

    public string RelativePath { get; } = relativePath;
    public Bitmap? Image { get => image; private set => Set(ref image, value); }
    public string? Error { get => error; private set => Set(ref error, value); }
    // The bytes passed every check (and decoded, when a decoder was supplied).
    public bool Verified { get; private set; }

    public async Task LoadAsync(string bundleDirectory, ImageDecoder? decoder, int? width,
        CancellationToken cancellationToken)
    {
        try
        {
            Bitmap? bitmap = await Task.Run(() =>
            {
                var png = BundleAssets.ReadPng(bundleDirectory, RelativePath);
                return decoder?.Invoke(png, width);
            }, cancellationToken);
            if (disposed || cancellationToken.IsCancellationRequested)
            {
                bitmap?.Dispose();
                return;
            }
            Image = bitmap;
            Verified = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception problem) when (Errors.IsBundle(problem))
        {
            if (!disposed) Error = problem.Message;
        }
    }

    // Controls drop the bitmap when Image becomes null; the compositor keeps its own reference
    // to anything it is still drawing, so disposing afterwards is safe.
    public void Dispose()
    {
        disposed = true;
        var old = image;
        Image = null;
        old?.Dispose();
    }
}

public sealed record Row(string Lead, string Text, string Note);

internal static class Format
{
    public static string Seconds(long us) =>
        (us / 1_000_000d).ToString("0.000", CultureInfo.InvariantCulture) + " s";

    public static string Clock(long us)
    {
        var time = TimeSpan.FromMicroseconds(Math.Max(0, us));
        return time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    public static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";

    public static string OneLine(string text) => text.ReplaceLineEndings(" ");
}
