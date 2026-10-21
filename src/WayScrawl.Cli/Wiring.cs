using WayScrawl.Application;
using WayScrawl.Core;
using WayScrawl.Platform.Hyprland;
using WayScrawl.Platform.Media;
using WayScrawl.Wayland;

namespace WayScrawl.Cli;

// The one place that chooses concrete adapters.
internal static class Wiring
{
    public static ICaptureBackend CreateBackend(WayScrawlPaths paths, WayScrawlConfig config) => config.Backend switch
    {
        "gpu-screen-recorder" => new GpuScreenRecorderBackend(),
        "portal" => new WayScrawl.Portal.PortalCaptureBackend(Path.Combine(paths.State, "portal-restore-token")),
        _ => throw new InvalidDataException($"Unknown backend '{config.Backend}'. Use gpu-screen-recorder or portal.")
    };

    public static SessionCoordinator CreateCoordinator(WayScrawlPaths paths, WayScrawlConfig config, Action<string> log, int configProblems = 0) =>
        new(paths, config, CreateBackend(paths, config),
            overlayFactory: () => new WaylandOverlay(new OverlayOptions(config.DrawColor, config.DrawWidth, InkFadeSeconds: config.InkFadeSeconds)),
            telemetryFactory: source => new HyprlandTelemetry(
                source.Geometry.Source.StartsWith("monitor:", StringComparison.Ordinal) ? source.Geometry : null,
                bridge: config.Telemetry == "bridge",
                windowTitles: config.WindowTitles),
            log, configProblems: configProblems, holdFactory: () => new HyprlandHoldControl(config.DrawHoldKey));
}
