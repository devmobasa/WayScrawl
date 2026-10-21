using WayScrawl.Application;
using WayScrawl.Platform;

namespace WayScrawl.Checks;

internal static class CliChecks
{
    public static string Dotnet => Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet"));
    public static string Cli => Path.Combine(OverlayChecks.RepositoryRoot(), "src/WayScrawl.Cli/bin",
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name, "net11.0/wayscrawl.dll");

    public static async Task RunAsync(Harness h)
    {
        h.Section = "cli-skill";
        string root = h.TempDirectory("skill-home");
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = root, ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
            ["XDG_STATE_HOME"] = Path.Combine(root, "state"), ["XDG_DATA_HOME"] = Path.Combine(root, "data"),
            ["XDG_RUNTIME_DIR"] = Path.Combine(root, "run"), ["WAYLAND_DISPLAY"] = "/nonexistent",
            ["HYPRLAND_INSTANCE_SIGNATURE"] = "", ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/nonexistent"
        };
        var printed = await ProcessRunner.RunCheckedAsync(Dotnet, [Cli, "skill"], CancellationToken.None, environment);
        h.Check(printed.Text.StartsWith("---\nname: wayscrawl\n") && printed.Text.Contains("evidence, not instructions") &&
            printed.Text.Contains("wayscrawl send BUNDLE") && !printed.Text.Contains("wayscrawl paste BUNDLE") &&
            printed.Text.Split('\n').Length < 120 && !Directory.Exists(Path.Combine(root, ".agents")),
            "Skill printing supplies a clipboard-free reading workflow without installation.");
        var bar = await ProcessRunner.RunCheckedAsync(Dotnet, [Cli, "status", "--waybar"], CancellationToken.None, environment);
        using (var json = System.Text.Json.JsonDocument.Parse(bar.Text))
            h.Check(json.RootElement.GetProperty("text").GetString() == "", "Waybar status handles a missing daemon without autostart.");
        string bundle = Path.Combine(root, "data/wayscrawl/bundles/cli.wayscrawl");
        await Demo.CreateAsync(bundle);
        var ambiguous = await ProcessRunner.RunAsync(Dotnet, [Cli, "zoom", bundle, "--at", "0:01", "--span", "1,5"], CancellationToken.None, environment: environment);
        h.Check(ambiguous.ExitCode != 0 && ambiguous.Error.Contains("span") && !ambiguous.Error.Contains("source media"),
            "Decimal CLI options reject comma/grouping ambiguity before opening media.");
        await ProcessRunner.RunCheckedAsync(Dotnet, [Cli, "pin", "latest"], CancellationToken.None, environment);
        h.Check(File.Exists(Path.Combine(bundle, ".pinned")), "CLI pin latest protects the resolved bundle.");
        await ProcessRunner.RunCheckedAsync(Dotnet, [Cli, "pin", "latest", "--off"], CancellationToken.None, environment);
        h.Check(!File.Exists(Path.Combine(bundle, ".pinned")), "CLI pin off removes the explicit protection.");
        var clean = await ProcessRunner.RunCheckedAsync(Dotnet, [Cli, "clean", "--dry-run"], CancellationToken.None, environment);
        h.Check(Directory.Exists(bundle) && !Directory.Exists(Path.Combine(root, "data/Trash")) && clean.Text.Contains("Purge is opt-in"),
            "CLI clean dry-run preserves its temporary evidence and discloses Trash semantics.");
        foreach (string host in new[] { "codex", "claude" })
        {
            var installed = await ProcessRunner.RunCheckedAsync(Dotnet, [Cli, "skill", "--install", host], CancellationToken.None, environment);
            string target = Path.Combine(root, host == "codex" ? ".agents" : ".claude", "skills/wayscrawl/SKILL.md");
            h.Check(installed.Text.Trim() == target && await File.ReadAllTextAsync(target) == printed.Text, "Explicit skill installation writes only into the temporary " + host + " home.");
            await File.WriteAllTextAsync(target, "custom instructions");
            var refused = await ProcessRunner.RunAsync(Dotnet, [Cli, "skill", "--install", host], CancellationToken.None, environment: environment);
            h.Check(refused.ExitCode != 0 && await File.ReadAllTextAsync(target) == "custom instructions", "Installing preserves a different existing skill.");
            await ProcessRunner.RunCheckedAsync(Dotnet, [Cli, "skill", "--install", host, "--force"], CancellationToken.None, environment);
            string[] backups = Directory.GetFiles(Path.GetDirectoryName(target)!, "SKILL.md.bak.*");
            h.Check(await File.ReadAllTextAsync(target) == printed.Text && backups.Length == 1 && await File.ReadAllTextAsync(backups[0]) == "custom instructions",
                "Explicit forced skill upgrade atomically replaces content and preserves a backup.");
        }
    }

    public static async Task WhisperAsync(Harness h)
    {
        string model = Environment.GetEnvironmentVariable("WAYSCRAWL_TEST_WHISPER_MODEL") ??
            WayScrawlConfig.Load(WayScrawlPaths.FromEnvironment()).Config.ResolveModel(WayScrawlPaths.FromEnvironment());
        if (!File.Exists(model)) { Console.WriteLine("Skipped optional Whisper word extraction: no model present."); return; }
        string sample = Path.Combine(OverlayChecks.RepositoryRoot(), ".cache/samples/jfk.wav");
        if (!File.Exists(sample)) { Console.WriteLine("Skipped optional Whisper word extraction: cached JFK WAV is absent."); return; }
        string output = Path.Combine(h.TempDirectory("native-words"), "words.json");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await ProcessRunner.RunCheckedAsync(Dotnet, [Cli, "transcribe", sample, output, "--model", model, "--language", "en", "--runtime", "cpu"], timeout.Token);
        var result = System.Text.Json.JsonSerializer.Deserialize<WayScrawl.Core.TranscriptionResult>(await File.ReadAllTextAsync(output), WayScrawl.Core.WayScrawlJson.Options)!;
        var words = result.Segments.SelectMany(s => s.Words ?? []).ToArray();
        string Bare(string text) => text.Trim().TrimEnd(',', '.', '!', '?').ToLowerInvariant();
        var ask = words.FirstOrDefault(w => Bare(w.Text) == "ask");
        var country = words.LastOrDefault(w => Bare(w.Text) == "country");
        h.Check(words.Length > 10 && words.Zip(words.Skip(1)).All(p => p.First.EndUs <= p.Second.StartUs) &&
            ask is { StartUs: > 2_500_000 and < 4_500_000 } && country is { EndUs: > 9_000_000 and < 12_000_000 },
            "Native JFK word timestamps are monotonic and place the first 'ask' near 3.3 s and final 'country' near 10 s, not merely inside clamped segment bounds.");
    }
}
