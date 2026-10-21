using WayScrawl.Application;

namespace WayScrawl.Cli;

internal static partial class Commands
{
    public static async Task<int> ZoomAsync(WayScrawlPaths paths, string[] arguments, CancellationToken cancellationToken)
    {
        var options = new CliArguments(arguments[1..], ["--at", "--span", "--frames"], ["--full"]);
        string directory = await ResolveBundleAsync(paths, arguments[0], cancellationToken);
        var result = await ZoomBuilder.BuildAsync(directory, options.Require("--at"), options.Number("--span", 2),
            options.Integer("--frames", 12), options.Has("--full"), cancellationToken, paths.Runtime);
        Console.WriteLine(result.Path);
        Console.WriteLine("Session times: " + string.Join(", ", result.TimesUs.Select(t => (t / 1_000_000d).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture))));
        if (result.Matched is not null) Console.WriteLine("Matched: " + result.Matched);
        foreach (string text in result.Narration) Console.WriteLine(text);
        return 0;
    }

    public static async Task<int> SkillAsync(string[] arguments, CancellationToken cancellationToken)
    {
        await using var resource = typeof(Commands).Assembly.GetManifestResourceStream("WayScrawl.Skill")
            ?? throw new InvalidOperationException("The bundled agent skill is missing.");
        using var reader = new StreamReader(resource);
        string content = await reader.ReadToEndAsync(cancellationToken);
        if (arguments.Length == 0) { Console.Write(content); return 0; }
        bool force = arguments is ["--install", "claude" or "codex", "--force"];
        if (!force && arguments is not ["--install", "claude" or "codex"])
            throw new ArgumentException("skill accepts --install claude or --install codex, optionally with --force to back up and replace an existing skill.");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Codex CLI 0.163.0: user skills are in ~/.agents/skills (official Build skills docs).
        string directory = Path.Combine(home, arguments[1] == "codex" ? ".agents" : ".claude", "skills", "wayscrawl");
        string path = Path.Combine(directory, "SKILL.md");
        if (File.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Skill path must not be a symbolic link: " + path);
            if (await File.ReadAllTextAsync(path, cancellationToken) == content) { Console.WriteLine(path); return 0; }
            if (!force) throw new IOException("A different skill already exists at " + path + "; use --force to replace it with a backup.");
            File.Copy(path, path + ".bak." + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture));
        }
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".SKILL-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(file))
                await writer.WriteAsync(content.AsMemory(), cancellationToken);
            File.Move(temporary, path, overwrite: force);
        }
        finally { File.Delete(temporary); }
        Console.WriteLine(path);
        return 0;
    }
}
