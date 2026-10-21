using Avalonia;

namespace WayScrawl.Review;

internal static class Program
{
    public static int Main(string[] args)
    {
        switch (args)
        {
            case ["--smoke", var bundle]:
                // Before any AppBuilder call: smoke mode must run without a display.
                return Smoke.RunAsync(bundle).GetAwaiter().GetResult();
            case [] or [not ['-', ..]]:
                return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        Console.Error.WriteLine("""
            usage: wayscrawl-review [BUNDLE_DIR]        open the review window, selecting BUNDLE_DIR
                   wayscrawl-review --smoke BUNDLE_DIR  load a bundle headlessly and print a summary
            """);
        return args is ["--help" or "-h"] ? 0 : 64;
    }

    // Also the entry point the XAML previewer looks for.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
