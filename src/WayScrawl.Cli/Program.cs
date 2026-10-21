using System.Runtime.InteropServices;
using System.Text.Json;
using WayScrawl.Application;
using WayScrawl.Cli;
using WayScrawl.Core;
using WayScrawl.Platform;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var paths = WayScrawlPaths.FromEnvironment();
try
{
    string command = args.Length > 0 ? args[0] : "";
    string[] rest = args.Skip(1).ToArray();
    switch (command)
    {
        case DaemonCommands.Start or DaemonCommands.Stop or DaemonCommands.Toggle or DaemonCommands.Cancel
            or DaemonCommands.Clear or DaemonCommands.Undo or DaemonCommands.Status or DaemonCommands.Shutdown or DaemonCommands.Marker or DaemonCommands.Draw:
            return await Commands.ClientAsync(paths, command, rest, cancellation.Token);
        case "draw-toggle":
            return await Commands.ClientAsync(paths, DaemonCommands.Draw, ["toggle"], cancellation.Token);
        case "daemon":
            return await Commands.DaemonAsync(paths, rest.Contains("--detached"), cancellation.Token);
        case "process" when rest.Length == 1:
            return await Commands.ProcessAsync(paths, rest[0], cancellation.Token);
        case "transcribe" when rest.Length >= 2:
            return await Commands.TranscribeAsync(rest, cancellation.Token);
        case "sessions":
            return Commands.Sessions(paths);
        case "frame" when rest.Length is 2 or 3:
            return await Commands.FrameAsync(rest, cancellation.Token);
        case "clean":
            return await Commands.CleanAsync(paths, rest, cancellation.Token);
        case "pin" when rest.Length >= 1:
            return await Commands.PinAsync(paths, rest, cancellation.Token);
        case "export" when rest.Length >= 1:
            return await Commands.ExportAsync(paths, rest, cancellation.Token);
        case "skill":
            return await Commands.SkillAsync(rest, cancellation.Token);
        case "zoom" when rest.Length >= 3:
            return await Commands.ZoomAsync(paths, rest, cancellation.Token);
        case "paste" when rest.Length <= 1:
            return await Commands.SendAsync(rest.FirstOrDefault() ?? "latest", true, cancellation.Token);
        case "send" when rest.Length is 1 or 2:
            return await Commands.SendAsync(rest[0], rest.Contains("--clipboard"), cancellation.Token);
        case "review":
            return Commands.Review(rest);
        case "demo" when rest.Length == 1:
            await Demo.CreateAsync(rest[0], cancellation.Token);
            Console.WriteLine("Synthetic bundle: " + Path.GetFullPath(rest[0]));
            return 0;
        case "inspect" when rest.Length == 1 || rest.Length == 2 && rest[1] == "--json":
            string directory = await Commands.ResolveBundleAsync(paths, rest[0], cancellation.Token);
            Bundle bundle = await BundleStore.ReadAsync(directory, cancellation.Token);
            Console.WriteLine(rest.Length == 2
                ? JsonSerializer.Serialize(bundle, BundleStore.Json)
                : BundleStore.Summary(bundle, directory));
            return 0;
        case "overlay-probe":
            return await Commands.OverlayProbeAsync(rest, cancellation.Token);
        case "doctor" when rest.Length == 0 || rest is ["--connect"]:
            return await Commands.DoctorAsync(paths, rest.Length == 1, cancellation.Token);
    }
    Console.WriteLine($"""
        WayScrawl — record, annotate and narrate screen evidence for coding agents ({RuntimeInformation.FrameworkDescription})

        Recording (talks to the daemon and starts it when needed):
          toggle                    start or stop a recording
          start | stop | cancel     explicit control; cancel discards the recording
          draw [on|off|toggle]      overlay drawing vs pass-through (Esc also leaves drawing)
          clear | undo              remove ink in drawing or pass-through
          marker [TEXT]             mark this moment; markers always become keyframes
          status [--waybar [--follow]]   daemon/session state or a Waybar JSON stream
          daemon                    run the daemon in the foreground
          shutdown                  stop the daemon after finishing any recording

        Evidence:
          sessions                  list recorded sessions and their bundles
          process SESSION_DIR       (re)build a bundle from a recorded session
          frame BUNDLE SECONDS [OUT.png]   extract another frame from a bundle's video
          export BUNDLE [--out PATH] [--crop auto|full|x,y,w,h] [--size 1920]
          clean [--dry-run]         move eligible captures to Trash
          pin BUNDLE [--off]        protect/unprotect a capture from retention
          skill [--install claude|codex [--force]]   print/install; force backs up and replaces
          zoom BUNDLE --at TIME|PHRASE [--span 2] [--frames 12] [--full]
          paste [BUNDLE]            copy evidence text (default: latest)
          send BUNDLE [--clipboard] print (or copy) a prompt pointing an agent at a bundle
          review [BUNDLE]           open the review window
          inspect BUNDLE [--json]
          demo NEW_DIRECTORY        write a synthetic bundle
          doctor [--connect]

        Config: {paths.ConfigFile}   Bundles: {paths.Bundles}
        """);
    return command is "" or "--help" or "-h" or "help" ? 0 : 64;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (DaemonUnavailableException error)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
    or ArgumentException or NotSupportedException or InvalidOperationException or InvalidDataException
    or FormatException or OverflowException or System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
