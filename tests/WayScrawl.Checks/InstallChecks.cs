using WayScrawl.Platform;

namespace WayScrawl.Checks;

internal static class InstallChecks
{
    public static async Task RunAsync(Harness h)
    {
        h.Section = "install";
        foreach (var (mode, runtime, replace) in new[]
                 {
                     ("running", "", true), ("running", "relative", true), ("stopped", "", true),
                     ("shutdown-fails", "", false), ("timeout", "", false), ("status-fails", "", false)
                 })
        {
            string work = h.TempDirectory("install");
            string scripts = Path.Combine(work, "repo/scripts");
            string tools = Path.Combine(work, "tools");
            string prefix = Path.Combine(work, "prefix");
            string target = Path.Combine(prefix, "lib/wayscrawl");
            foreach (string directory in new[] { scripts, tools, target, Path.Combine(work, "tmp") })
                Directory.CreateDirectory(directory);
            File.Copy(Path.Combine(OverlayChecks.RepositoryRoot(), "scripts/install.sh"), Path.Combine(scripts, "install.sh"));
            async Task Executable(string path, string script)
            {
                await File.WriteAllTextAsync(path, script);
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            await Executable(Path.Combine(tools, "dotnet"), """
                #!/bin/bash
                set -eu
                [ "$1" = publish ]
                while [ "$1" != -o ]; do shift; done
                mkdir -p "$2/runtimes"
                cp "$FIXTURE/new-client" "$2/wayscrawl"
                """);
            await Executable(Path.Combine(tools, "sleep"), "#!/bin/sh\nexit 0\n");
            await Executable(Path.Combine(work, "new-client"), "#!/bin/sh\necho 'replacement client'\n");
            await Executable(Path.Combine(target, "wayscrawl"), """
                #!/bin/bash
                set -eu
                echo "$1" >> "$FIXTURE/calls"
                case "$1" in
                    status)
                        [ "$FAKE_DAEMON" != status-fails ] || exit 1
                        if [ "$FAKE_DAEMON" = stopped ] || [ -f "$FIXTURE/stopped" ]; then
                            echo 'Daemon: not running'
                        elif [ -f "$FIXTURE/shutdown" ] && [ "$FAKE_DAEMON" != timeout ]; then
                            if [ -f "$FIXTURE/polled" ]; then
                                touch "$FIXTURE/stopped"
                                echo 'Daemon: not running'
                            else
                                touch "$FIXTURE/polled"
                                echo 'Daemon is shutting down'
                            fi
                        else
                            echo 'Daemon is running'
                        fi ;;
                    shutdown)
                        [ "$FAKE_DAEMON" != shutdown-fails ] || exit 1
                        touch "$FIXTURE/shutdown" ;;
                    *) exit 1 ;;
                esac
                """);
            string original = await File.ReadAllTextAsync(Path.Combine(target, "wayscrawl"));
            var environment = new Dictionary<string, string>
            {
                ["HOME"] = Path.Combine(work, "home"), ["PREFIX"] = prefix,
                ["XDG_CONFIG_HOME"] = Path.Combine(work, "config"), ["XDG_STATE_HOME"] = Path.Combine(work, "state"),
                ["XDG_DATA_HOME"] = Path.Combine(work, "data"), ["TMPDIR"] = Path.Combine(work, "tmp"),
                ["PATH"] = tools + ":" + Environment.GetEnvironmentVariable("PATH"),
                ["FIXTURE"] = work, ["FAKE_DAEMON"] = mode, ["TEST_RUNTIME"] = runtime
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await ProcessRunner.RunAsync("bash",
                ["-c", "unset XDG_RUNTIME_DIR; if [ -n \"$TEST_RUNTIME\" ]; then export XDG_RUNTIME_DIR=\"$TEST_RUNTIME\"; fi; exec bash \"$1\"",
                    "install-check", Path.Combine(scripts, "install.sh")], timeout.Token, environment: environment);
            string[] calls = File.Exists(Path.Combine(work, "calls")) ? await File.ReadAllLinesAsync(Path.Combine(work, "calls")) : [];
            bool safe = replace
                ? result.ExitCode == 0 && await File.ReadAllTextAsync(Path.Combine(target, "wayscrawl")) != original &&
                    (mode == "stopped" ? calls.SequenceEqual(["status"]) :
                        calls.SequenceEqual(["status", "shutdown", "status", "status"]) && File.Exists(Path.Combine(work, "stopped")))
                : result.ExitCode != 0 && await File.ReadAllTextAsync(Path.Combine(target, "wayscrawl")) == original &&
                    !File.Exists(Path.Combine(prefix, "bin/wayscrawl"));
            h.Check(safe, $"Installer queries the installed client and preserves shutdown safety ({mode}, runtime '{runtime}'): " +
                string.Join(", ", calls) + " " + result.Error.Trim());
        }
    }
}
