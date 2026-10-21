# Hyprland setup

## Toolchain

WayScrawl builds with the exact SDK pinned in `global.json`: .NET 11 RC2
(`11.0.100-rc.2.26504.105`, target `net11.0`). Arch's `dotnet-sdk` package may lag behind
this pin, so a package install alone may not be enough. The bootstrap script installs a
project-local copy:

~~~bash
python3 scripts/bootstrap-dotnet.py
python3 scripts/check.py
~~~

The bootstrap needs Python 3.12+ on Linux x86_64. It checks Microsoft's release metadata and the
SDK's SHA-512 before extracting, retries a truncated download, refuses archive entries that leave
the SDK folder, and never replaces an existing `.dotnet` folder. If an interrupted run left its
lock behind, make sure the old process has exited before removing it. If the SDK fails to start,
look at the actual loader error (often a missing ICU, OpenSSL or C++ library) rather than
changing the pinned version.

## Desktop packages

~~~bash
sudo pacman -S --needed gpu-screen-recorder ffmpeg wl-clipboard libnotify pipewire wireplumber
# optional: the screen-share portal backend
sudo pacman -S --needed xdg-desktop-portal xdg-desktop-portal-hyprland gstreamer gst-plugins-base gst-plugin-pipewire gst-plugins-good gst-plugins-bad
~~~

Package names can differ between repositories; check them on your machine. On a PipeWire desktop,
keep your existing audio setup.

## Diagnosis

`wayscrawl doctor` reports session variables, tool paths, monitors, the Whisper model, the
capture backend (gpu-screen-recorder options, or the portal version and GStreamer encoder) and
whether the daemon is running. It never starts a recording or opens the microphone; `--connect`
adds one Wayland connection attempt.

More checks, all read-only:

~~~bash
hyprctl version
hyprctl -j monitors
wpctl status
systemctl --user --no-pager status pipewire wireplumber xdg-desktop-portal xdg-desktop-portal-hyprland
journalctl --user -b -u xdg-desktop-portal -u xdg-desktop-portal-hyprland --no-pager -n 80
~~~

Run graphical diagnostics from your desktop session: a service without the session environment
cannot reach a healthy compositor.

## Shortcuts

Hyprland 0.55 and later use a Lua configuration. `config/hyprland.lua.example` binds the
WayScrawl commands to SUPER+ALT+SHIFT with R, D, M, X, C, Z and V;
`config/hyprland-legacy.conf.example` does the same for the older format. The binds use
`hl.dsp.exec_cmd`, so the work runs outside the compositor.

Add them after your other bindings. On Omarchy, M and X are already used (Music TUI and X Post).
Remove those first with `hl.unbind`, written with the same modifier order Omarchy uses, or the
unbind does not match:

~~~lua
hl.unbind("SUPER + SHIFT + ALT + M")
hl.bind("SUPER + ALT + SHIFT + M", hl.dsp.exec_cmd("wayscrawl marker"))
~~~

Check the result with `hyprctl reload`, `hyprctl configerrors` and `hyprctl binds -j`.

## Interaction telemetry

`"telemetry": "socket2"` (the default) keeps one connection to Hyprland's event socket while
recording. It records window focus, workspace and window-open events, timestamped on arrival,
and retries the same socket if the connection drops. If Hyprland restarts as a new instance,
restart the daemon with `wayscrawl shutdown`.

`"telemetry": "bridge"` also records mouse clicks and wheel events with the cursor position.
At the start of a recording it installs a small Lua script with `hyprctl eval`. The script adds
non-consuming bindings for the mouse buttons and the wheel, so applications still receive every
click. Each binding emits a custom event that WayScrawl maps to the recorded monitor.

- Hyprland 0.56 aborts if a Lua binding is removed, so the bindings are installed once per
  compositor run and never removed. A per-recording token and deadline keep them silent outside
  a recording; the daemon renews the deadline every 5 seconds, and an unrenewed bridge goes
  quiet after 15 seconds.
- A config reload drops the bindings; WayScrawl reinstalls them when Hyprland reports the reload.
- Keystrokes are never stored in bundles. Bridge events describe bindings the compositor saw,
  not input an application received.

Hold to draw (`"drawTrigger": "hold"`) uses its own connection and its own bindings for the
chosen key only, independent of the telemetry setting.

## Screen-capture permissions

Hyprland's permission system can mediate direct screen capture, while the portal backend uses
its own consent dialog. A permission applies to the process that captures (the recorder), not
to the WayScrawl daemon that starts it. A black recording usually means capture was denied, not
a decoding error.
