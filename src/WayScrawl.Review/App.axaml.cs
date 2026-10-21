using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using WayScrawl.Application;

namespace WayScrawl.Review;

public partial class App : Avalonia.Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var paths = WayScrawlPaths.FromEnvironment();
            string? notice = null;
            WayScrawlConfig config;
            try
            {
                config = WayScrawlConfig.Load(paths).Config;
            }
            catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // A broken config must not hide existing bundles; the daemon reports it separately.
                config = new();
                notice = error.Message + " Showing the default bundles folder.";
            }
            string? cli = SelfCommand.FindCompanion("wayscrawl", "WayScrawl.Cli");
            var model = new MainViewModel(paths, config.BundleDirectory ?? paths.Bundles,
                desktop.Args is [var bundle] ? bundle : null, cli is null ? null : (cli, []), Decode, notice);
            var window = new MainWindow { DataContext = model };
            window.Closed += (_, _) => model.Dispose();
            desktop.MainWindow = window;
            model.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }

    // Runs on the thread pool with bytes BundleAssets has already bounded and checked.
    private static Bitmap Decode(PngAsset png, int? width)
    {
        using var stream = png.Open();
        try
        {
            return width is { } target && target < png.Width
                ? Bitmap.DecodeToWidth(stream, target, BitmapInterpolationMode.MediumQuality)
                : new Bitmap(stream);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new InvalidDataException($"{png.Path} could not be decoded: {error.Message}", error);
        }
    }
}
