using System.Diagnostics;
using System.Text.Json;
using WayScrawl.Platform;
using WayScrawl.Platform.Hyprland;

namespace WayScrawl.Checks;

// A private Hyprland nested in headless Weston, with its own HOME, XDG directories and
// session bus. Nothing connects to the owner's compositor; injected input stays inside.
// Pattern adapted from SharpMarker's NestedCompositor.
internal sealed class NestedHyprland : IAsyncDisposable
{
    private readonly List<Process> processes = [];
    private readonly Dictionary<string, string> environment;

    private NestedHyprland(string root, Dictionary<string, string> environment)
    {
        Root = root;
        this.environment = environment;
    }

    public string Root { get; }
    public string Runtime => environment["XDG_RUNTIME_DIR"];
    public string Display { get; private set; } = "";
    public IReadOnlyDictionary<string, string> Environment => environment;

    // Null when the prerequisites (weston, Hyprland, a test Aquamarine) are missing.
    public static string? MissingPrerequisite(string aquamarine)
    {
        foreach (string tool in new[] { "weston", "Hyprland", "dbus-daemon", "hyprctl", "grim", "wtype" })
            if (!File.Exists("/usr/bin/" + tool)) return tool + " is not installed";
        return File.Exists(Path.Combine(aquamarine, "libaquamarine.so")) ? null
            : $"no test Aquamarine in {aquamarine} (run scripts/build-test-aquamarine.sh)";
    }

    public static async Task<NestedHyprland> StartAsync(string aquamarine, int width = 1920, int height = 1080, double scale = 1.5)
    {
        SweepStaleRoots();
        // Short root: Hyprland's socket paths must fit the 108-byte sockaddr_un limit.
        string root = Directory.CreateTempSubdirectory("wsn-").FullName;
        await File.WriteAllTextAsync(Path.Combine(root, "owner"), System.Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var env = new Dictionary<string, string>
        {
            ["PATH"] = "/usr/bin:/bin", ["LANG"] = "C.UTF-8", ["USER"] = System.Environment.UserName,
            ["XDG_SESSION_TYPE"] = "wayland", ["XDG_CURRENT_DESKTOP"] = "Hyprland"
        };
        foreach (var (key, name) in new[] { ("HOME", "home"), ("XDG_RUNTIME_DIR", "rt"), ("XDG_CONFIG_HOME", "config"),
                     ("XDG_DATA_HOME", "data"), ("XDG_STATE_HOME", "state"), ("XDG_CACHE_HOME", "cache"), ("TMPDIR", "tmp") })
        {
            string path = Path.Combine(root, name);
            Directory.CreateDirectory(path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            env[key] = path;
        }
        env["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=" + Path.Combine(env["XDG_RUNTIME_DIR"], "bus");
        env["GIO_USE_VFS"] = "local";
        var nested = new NestedHyprland(root, env);
        try
        {
            string runtime = env["XDG_RUNTIME_DIR"];
            // A bus without service directories cannot activate anything (gvfs, portals, notifiers).
            string busConfig = Path.Combine(root, "bus.conf");
            await File.WriteAllTextAsync(busConfig, $$"""
                <!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN"
                 "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
                <busconfig>
                  <type>session</type>
                  <listen>{{env["DBUS_SESSION_BUS_ADDRESS"]}}</listen>
                  <auth>EXTERNAL</auth>
                  <policy context="default"><allow send_destination="*" eavesdrop="true"/><allow eavesdrop="true"/><allow own="*"/></policy>
                </busconfig>
                """);
            nested.Spawn("dbus-daemon", ["--config-file=" + busConfig, "--nofork"], new Dictionary<string, string>());
            await WaitAsync(() => File.Exists(Path.Combine(runtime, "bus")), "session bus");
            // Mesa EGL keeps Weston and the nested Hyprland on one Mesa-driven GPU.
            const string mesa = "/usr/share/glvnd/egl_vendor.d/50_mesa.json";
            var gl = new Dictionary<string, string>();
            if (File.Exists(mesa)) gl["__EGL_VENDOR_LIBRARY_FILENAMES"] = mesa;
            nested.Spawn("weston", ["--backend=headless", "--renderer=gl", "--socket=parent", "--width=1920", "--height=1080"], gl);
            await WaitAsync(() => File.Exists(Path.Combine(runtime, "parent")), "Weston");
            string config = Path.Combine(root, "hypr.lua");
            await File.WriteAllTextAsync(config,
                "hl.config({xwayland={enabled=false},general={gaps_in=0,gaps_out=0,border_size=0},misc={disable_hyprland_logo=true," +
                "disable_splash_rendering=true,disable_watchdog_warning=true},animations={enabled=false},input={kb_layout=\"us\"}})\n" +
                FormattableString.Invariant($"hl.monitor({{output=\"TEST\",mode=\"{width}x{height}@60\",position=\"0x0\",scale={scale}}})\n"));
            var hypr = new Dictionary<string, string>(gl)
            {
                ["WAYLAND_DISPLAY"] = "parent", ["HYPRLAND_NO_SD_VARS"] = "1", ["HYPRLAND_NO_SD_NOTIFY"] = "1",
                ["LD_LIBRARY_PATH"] = aquamarine
            };
            // Render on a Mesa-driven GPU; NVIDIA nodes have no Mesa driver.
            foreach (string card in Directory.Exists("/sys/class/drm") ? Directory.EnumerateDirectories("/sys/class/drm", "card?") : [])
                if (File.Exists(Path.Combine(card, "device/vendor")) && File.ReadAllText(Path.Combine(card, "device/vendor")).Trim() != "0x10de")
                {
                    hypr["AQ_DRM_DEVICES"] = "/dev/dri/" + Path.GetFileName(card);
                    break;
                }
            nested.Spawn("Hyprland", ["--config", config], hypr);
            string hyprDirectory = Path.Combine(runtime, "hypr");
            await WaitAsync(() => Directory.Exists(hyprDirectory) &&
                Directory.EnumerateFiles(hyprDirectory, ".socket.sock", SearchOption.AllDirectories).Any() &&
                Directory.EnumerateFiles(runtime, "wayland-*").Any(p => !p.EndsWith(".lock", StringComparison.Ordinal)), "Hyprland");
            string socket = Directory.EnumerateFiles(hyprDirectory, ".socket.sock", SearchOption.AllDirectories).Single();
            env["HYPRLAND_INSTANCE_SIGNATURE"] = Path.GetFileName(Path.GetDirectoryName(socket))!;
            nested.Display = Directory.EnumerateFiles(runtime, "wayland-*").Single(p => !p.EndsWith(".lock", StringComparison.Ordinal));
            env["WAYLAND_DISPLAY"] = Path.GetFileName(nested.Display);
            await nested.HyprctlAsync("output", "create", "headless", "TEST");
            await WaitAsync(() => nested.MonitorsAsync().Result.Any(m => m.Name == "TEST"), "TEST output");
            return nested;
        }
        catch
        {
            await nested.DisposeAsync();
            throw;
        }
    }

    // A harness that crashed never disposed its compositor. Before starting another, stop any
    // process still running from a root whose owner is gone, unmount it and delete it.
    private static void SweepStaleRoots()
    {
        foreach (string root in Directory.EnumerateDirectories(Path.GetTempPath(), "wsn-*"))
        {
            string ownerFile = Path.Combine(root, "owner");
            if (File.Exists(ownerFile) && int.TryParse(File.ReadAllText(ownerFile), out int owner) &&
                Directory.Exists("/proc/" + owner))
                continue;
            foreach (string proc in Directory.EnumerateDirectories("/proc").Where(d => int.TryParse(Path.GetFileName(d), out _)))
            {
                try
                {
                    // Only processes this harness started carry the root's private runtime directory.
                    string environ = File.ReadAllText(Path.Combine(proc, "environ"));
                    if (environ.Split('\0').Contains("XDG_RUNTIME_DIR=" + Path.Combine(root, "rt")))
                        WayScrawl.Platform.Native.Libc.Kill(int.Parse(Path.GetFileName(proc)), 9);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            foreach (string line in File.ReadAllLines("/proc/self/mounts"))
                if (line.Split(' ') is [_, var target, ..] && target.StartsWith(root, StringComparison.Ordinal))
                    WayScrawl.Platform.Spawn.Start(new ProcessStartInfo("fusermount3", ["-u", "-z", target]) { UseShellExecute = false }).WaitForExit(5000);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public string BusAddress => environment["DBUS_SESSION_BUS_ADDRESS"];

    // Runs xdg-desktop-portal and its Hyprland backend inside the private session. The share
    // picker is a script that selects TEST, so no person is asked. PipeWire is the owner's
    // daemon: screencast streams need one, and a node exists only while a test records.
    public async Task StartPortalAsync()
    {
        string picker = Path.Combine(Root, "picker.sh");
        await File.WriteAllTextAsync(picker, "#!/bin/sh\necho '[SELECTION]r/screen:TEST'\n");
        File.SetUnixFileMode(picker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string hypr = Path.Combine(environment["XDG_CONFIG_HOME"], "hypr");
        Directory.CreateDirectory(hypr);
        await File.WriteAllTextAsync(Path.Combine(hypr, "xdph.conf"),
            $"screencopy {{\n    custom_picker_binary = {picker}\n    allow_token_by_default = true\n}}\n");
        string owner = System.Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/run/user/" + WayScrawl.Platform.Native.Libc.GetUid();
        var portal = new Dictionary<string, string> { ["PIPEWIRE_RUNTIME_DIR"] = owner };
        Spawn("/usr/lib/xdg-desktop-portal-hyprland", [], portal);
        await Task.Delay(500);
        Spawn("/usr/lib/xdg-desktop-portal", ["-r"], portal);
        await WaitAsync(() =>
        {
            try { return WayScrawl.Portal.ScreenCastSession.ProbeAsync(BusAddress, CancellationToken.None).Result.Version > 0; }
            catch (AggregateException) { return false; }
        }, "xdg-desktop-portal");
    }

    // Stops one of the private session's processes by its executable name (for failure tests).
    public void Stop(string tool)
    {
        foreach (var process in processes.Where(p => !p.HasExited && Path.GetFileName(p.StartInfo.FileName) == tool))
            process.Kill();
    }

    public async Task<string> HyprctlAsync(params string[] arguments) => (await RunAsync("hyprctl", arguments)).Text;

    public async Task<HyprMonitor[]> MonitorsAsync()
    {
        string text = await HyprctlAsync("-j", "monitors");
        return HyprlandIpc.ParseMonitors(text[text.IndexOf('[')..]);
    }

    public async Task<JsonDocument> LayersAsync() => JsonDocument.Parse(await HyprctlAsync("-j", "layers"));

    // grim against the nested display: the composed output exactly as a recorder would see it.
    public async Task<byte[]> ScreenshotAsync(string output = "TEST") =>
        (await RunAsync("grim", ["-o", output, "-t", "png", "-"])).Output;

    public Task KeysAsync(params string[] arguments) => RunAsync("wtype", arguments);

    // A long-lived virtual keyboard keeps the seat's keyboard capability stable; each short
    // wtype call would otherwise add and remove a keyboard around its own key events.
    public void HoldKeyboard() => Spawn("wtype", ["-s", "600000", "-k", "Shift_L"], new Dictionary<string, string>());

    public async Task<ProcessResult> RunAsync(string tool, string[] arguments)
    {
        var info = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        info.Environment.Clear();
        foreach (var (key, value) in environment) info.Environment[key] = value;
        using var process = WayScrawl.Platform.Spawn.Start(info);
        using var output = new MemoryStream();
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.BaseStream.CopyToAsync(output);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException($"{tool} failed: {await error} {ProcessRunner.LastLine(System.Text.Encoding.UTF8.GetString(output.ToArray()))}");
        return new ProcessResult(process.ExitCode, output.ToArray(), await error);
    }

    public void Spawn(string tool, string[] arguments, IReadOnlyDictionary<string, string> extra)
    {
        var info = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        info.Environment.Clear();
        foreach (var (key, value) in environment) info.Environment[key] = value;
        foreach (var (key, value) in extra) info.Environment[key] = value;
        var process = WayScrawl.Platform.Spawn.Start(info);
        // Each compositor process logs to ROOT/<tool>.log; set WAYSCRAWL_TEST_KEEP=1 to keep ROOT.
        var log = new FileStream(Path.Combine(Root, Path.GetFileName(tool) + ".log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _ = Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(log), process.StandardError.BaseStream.CopyToAsync(log))
            .ContinueWith(_ => log.Dispose(), TaskScheduler.Default);
        processes.Add(process);
    }

    private static async Task WaitAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            try { if (condition()) return; } catch (Exception) when (DateTime.UtcNow < deadline) { }
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what + " did not start.");
            await Task.Delay(100);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var process in Enumerable.Reverse(processes))
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            process.Dispose();
        }
        if (System.Environment.GetEnvironmentVariable("WAYSCRAWL_TEST_KEEP") == "1")
        {
            Console.WriteLine("Kept nested compositor files: " + Root);
            return;
        }
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
