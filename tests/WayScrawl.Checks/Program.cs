using WayScrawl.Checks;

if (args is ["--trash-mount-fixture", var mountRoot]) return await RetentionChecks.MountFixtureAsync(mountRoot);

// Dependency-free console harness. Default checks are headless (they still need ffmpeg);
// --live adds checks that capture this desktop and therefore need an attended session.
bool live = args.Contains("--live");
// --only=NAME runs one group: core, platform, pipeline, coordinator, overlay, telemetry, portal, review or live.
string? only = args.FirstOrDefault(a => a.StartsWith("--only=", StringComparison.Ordinal))?["--only=".Length..];
bool Run(string name) => only is null ? name != "live" || live : only == name;
var h = new Harness();
try
{
    if (Run("core")) await CoreChecks.RunAsync(h);
    if (Run("platform")) await PlatformChecks.RunAsync(h);
    if (Run("pipeline")) await PipelineChecks.RunAsync(h);
    if (Run("coordinator")) await CoordinatorChecks.RunAsync(h);
    if (Run("overlay")) await OverlayChecks.RunAsync(h);
    if (Run("telemetry")) await TelemetryChecks.RunAsync(h);
    if (Run("portal")) await PortalChecks.RunAsync(h);
    if (Run("review")) await ReviewChecks.RunAsync(h);
    if (Run("live")) await LiveChecks.RunAsync(h);
}
catch (CheckFailed failure)
{
    Console.Error.WriteLine("FAILED " + failure.Message);
    return 1;
}
finally
{
    h.Cleanup();
}
Console.WriteLine($"Passed {h.Count} {(only ?? (live ? "headless and live" : "headless"))} checks.");
return 0;
