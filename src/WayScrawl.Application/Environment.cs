using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using WayScrawl.Core;

namespace WayScrawl.Application;

// XDG roles stay distinct: runtime for the socket and lock, state for raw sessions,
// data for published bundles and models, config for settings.
public sealed record WayScrawlPaths(string Runtime, string State, string Data, string Config)
{
    public string Socket => Path.Combine(Runtime, "control.sock");
    public string DaemonLock => Path.Combine(Runtime, "daemon.lock");
    public string Sessions => Path.Combine(State, "sessions");
    public string DaemonLog => Path.Combine(State, "daemon.log");
    public string Bundles => Path.Combine(Data, "bundles");
    public string ConfigFile => Path.Combine(Config, "config.json");

    public static WayScrawlPaths FromEnvironment()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Xdg(string name, string fallback) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value && Path.IsPathRooted(value)
                ? value : Path.Combine(home, fallback);
        string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } r && Path.IsPathRooted(r)
            ? r : Path.Combine(Path.GetTempPath(), "wayscrawl-" + Environment.UserName);
        return new(Path.Combine(runtime, "wayscrawl"), Path.Combine(Xdg("XDG_STATE_HOME", ".local/state"), "wayscrawl"),
            Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "wayscrawl"), Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "wayscrawl"));
    }

    public static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}

public sealed record RetentionSettings(int MediaDays = 7, int BundleDays = 30, bool Purge = false, int OrphanDays = 30);

public sealed record WhisperSettings(string? Model = null, string Language = "auto", string Runtime = "auto",
    int TimeoutSeconds = 900);

public sealed record WayScrawlConfig(
    string Output = "focused",
    string? Microphone = "default_input",
    int FramesPerSecond = 30,
    string Backend = "gpu-screen-recorder",
    string? BundleDirectory = null,
    int MaxFrames = 12,
    int FrameLongEdge = 1920,
    int CropLongEdge = 1600,
    string DrawColor = "#ff3b30",
    double DrawWidth = 6,
    bool Overlay = true,
    // off | socket2 (window focus and workspaces) | bridge (socket2 plus the Lua click bridge)
    string Telemetry = "socket2",
    bool WindowTitles = true,
    bool Notifications = true,
    bool CopyPathToClipboard = true,
    WhisperSettings? Whisper = null,
    string Clipboard = "paste",
    double InkFadeSeconds = 1.5,
    string DrawTrigger = "toggle", string DrawHoldKey = "Shift_L")
{
    public RetentionSettings? Retention { get; init; } = new();
    public WhisperSettings WhisperOrDefault => Whisper ?? new();

    public string ClipboardMode => CopyPathToClipboard? Clipboard : "off";

    public static (WayScrawlConfig Config, string[] Problems) Load(WayScrawlPaths paths)
    {
        if (!File.Exists(paths.ConfigFile)) return (new(), []);
        var problems = new List<string>();
        try
        {
            if (JsonNode.Parse(File.ReadAllText(paths.ConfigFile)) is not JsonObject root)
                throw new JsonException("Expected a JSON object.");
            Clean(root, typeof(WayScrawlConfig), "", problems);
            return (root.Deserialize<WayScrawlConfig>(WayScrawlJson.Options) ?? new(), problems.ToArray());
        }
        catch (Exception error) when (error is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return (new(Microphone: null, Telemetry: "off", WindowTitles: false, Clipboard: "off") { Retention = null },
                [$"config: {error.Message}; using privacy-safe defaults (microphone, telemetry, titles and clipboard off)"]);
        }
    }

    private static void Clean(JsonObject node, Type type, string prefix, List<string> problems)
    {
        var properties = type.GetProperties().Where(p => p.SetMethod is not null)
            .ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), StringComparer.Ordinal);
        foreach (var (key, value) in node.ToArray())
        {
            string? problem = null;
            if (!properties.TryGetValue(key, out var property)) problem = "unknown key, ignored";
            else
            {
                try
                {
                    if (key == "whisper" && value is JsonObject nested)
                        Clean(nested, typeof(WhisperSettings), "whisper.", problems);
                    if (key == "retention" && value is JsonObject settings)
                    {
                        int before = problems.Count;
                        Clean(settings, typeof(RetentionSettings), "retention.", problems);
                        if (problems.Count > before) problem = "invalid retention settings";
                    }
                    object? parsed = value?.Deserialize(property.PropertyType, WayScrawlJson.Options);
                    if (parsed is null && key is not ("microphone" or "bundleDirectory" or "model" or "whisper" or "retention"))
                        problem = "null is not allowed";
                    else problem ??= ValidateValue(prefix + key, parsed);
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
                { problem = "invalid value: " + error.Message; }
            }
            if (problem is null) continue;
            node.Remove(key);
            bool privacy = prefix.Length == 0 && key is "microphone" or "windowTitles" or "telemetry" or "clipboard" or
                                                        "copyPathToClipboard" or "retention";
            if (privacy)
                node[key] = key switch
                {
                    "windowTitles" or "copyPathToClipboard" => JsonValue.Create(false),
                    "telemetry" or "clipboard" => JsonValue.Create("off"),
                    _ => null
                };
            problems.Add(prefix + key + ": " + problem +
                         (prefix == "retention." ? "; retention disabled"
                             : privacy           ? "; using the most private value"
                                                 : "; using the default"));
        }
    }

    private static string? ValidateValue(string key, object? value) => key switch {
        "bundleDirectory" when value is string directory && !Path.IsPathRooted(directory) => "must be an absolute directory",
        "retention" when value is RetentionSettings r &&
            (r.MediaDays < 1 || r.BundleDays < r.MediaDays || r.BundleDays > 36500 || r.OrphanDays < 1 || r.OrphanDays > 36500) =>
            "days must be positive, bundleDays at least mediaDays, at most 36500",
        "retention.mediaDays" or "retention.bundleDays" or "retention.orphanDays" when value is int days &&
            (days < 1 || days > 36500) => "days must be 1–36500",
        "drawTrigger" when value is not ("toggle" or "hold") => "must be toggle or hold",
        "drawHoldKey" when value is not (
            "Shift_L" or "Shift_R" or "Control_L" or "Control_R" or "Alt_L" or "Alt_R" or "Super_L" or "Super_R") =>
            "must be a left/right Shift, Control, Alt or Super modifier keysym",
        "inkFadeSeconds" when value is double n && (!double.IsFinite(n) || n < 0 || n > 3600) => "must be 0–3600",
        "framesPerSecond" when value is int n && (n < 1 || n > 240) => "must be 1–240",
        "maxFrames" when value is int n && (n < 3 || n > 24) => "must be 3–24",
        "frameLongEdge" or "cropLongEdge" when value is int n && (n < 320 || n > 7680) => "must be 320–7680",
        "drawWidth" when value is double n && (!double.IsFinite(n) || n < 1 || n > 64) => "must be 1–64",
        "drawColor" when value is string color && (color.Length != 7 || color[0] != '#' || color.Skip(1).Any(c => !Uri.IsHexDigit(c))) =>
            "must be #rrggbb",
        "backend" when value is not ("gpu-screen-recorder" or "portal") => "must be gpu-screen-recorder or portal",
        "telemetry" when value is not ("off" or "socket2" or "bridge") => "must be off, socket2 or bridge",
        "clipboard" when value is not ("paste" or "path" or "off") => "must be paste, path or off",
        "whisper.runtime" when value is not ("auto" or "cuda" or "cpu") => "must be auto, cuda or cpu",
        "whisper.timeoutSeconds" when value is int n && n <= 0 => "must be positive",
        _ => null
    };

    public string ResolveModel(WayScrawlPaths paths) =>
        WhisperOrDefault.Model is { Length: > 0 } model
            ? model.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), model[2..]) : model
            : Path.Combine(paths.Data, "models", "ggml-large-v3-turbo.bin");
}

// Starts this same executable with a worker verb, whether it runs as an apphost or via dotnet.
public static class SelfCommand
{
    public static (string File, string[] Prefix) Resolve()
    {
        string process = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown process path.");
        if (Path.GetFileNameWithoutExtension(process) == "dotnet")
        {
            string entry = System.Reflection.Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("Unknown entry assembly.");
            return (process, [entry]);
        }
        return (process, []);
    }

    // Installed builds keep wayscrawl and wayscrawl-review in one directory. A development build
    // (src/<project>/bin/<configuration>/<framework>/) falls back to the other project's output
    // for the same configuration and framework.
    public static string? FindCompanion(string executable, string project)
    {
        string directory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string sibling = Path.Combine(directory, executable);
        if (File.Exists(sibling)) return sibling;
        string framework = Path.GetFileName(directory);
        string configuration = Path.GetFileName(Path.GetDirectoryName(directory)) ?? "";
        if (framework.Length == 0 || configuration.Length == 0) return null;
        string development = Path.GetFullPath(Path.Combine(directory, "../../../..", project, "bin",
            configuration, framework, executable));
        return File.Exists(development) ? development : null;
    }
}
