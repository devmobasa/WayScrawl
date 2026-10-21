# WayScrawl

Show a coding agent what is wrong instead of describing it. Record your screen, draw on it,
say what you see, and WayScrawl turns the recording into a small evidence bundle an agent can
read: keyframes with your ink, close-up crops, a contact sheet, a transcript of your narration
and a timeline of what happened.

WayScrawl is for Arch Linux with Hyprland. It is written in C# on .NET 11 and runs entirely on
your machine.

## How it works

1. Press a hotkey to start recording. Talk through the problem.
2. Press another hotkey to draw: circle the bug, drop numbered pins, mark important moments.
   Ink fades on its own a moment after you lift the pen.
3. Stop the recording. WayScrawl picks the keyframes, transcribes your narration with Whisper,
   crops around your ink and the window you used, and publishes a bundle.
4. The clipboard now holds a short summary: one line per keyframe with what you said, plus the
   bundle path. Paste it into Claude Code, Codex or any agent that can read files and images.

Agents read text and images, not video. The bundle gives them the screenshots that matter and
your words, and the original recording stays available for anything they want to look at more
closely until retention retires it, after seven days by default.

## Shortcuts

All shortcuts use the prefix **SUPER + ALT + SHIFT**.

| Key | Action |
| --- | --- |
| R | start or stop recording |
| D | draw on or off (Esc or right-click also leaves drawing) |
| M | mark this moment; it is always kept as a keyframe (nearby moments within 400 ms can share one) |
| X | discard the current recording |
| C | clear all ink |
| Z | undo the last stroke or pin |
| V | copy the summary of the latest capture again |

While drawing: drag to draw, click to drop a numbered pin, `Ctrl+Z` undo, `Delete` clear,
`M` marker, `1`–`6` colours.

## Install

Requirements: Hyprland (0.55 or later for the Lua config), the .NET 11 RC2 SDK
`11.0.100-rc.2.26504.105`, `gpu-screen-recorder`, `ffmpeg`, `wl-clipboard` and `libnotify`.
For the optional screen-share portal backend: `xdg-desktop-portal-hyprland` and GStreamer with
`pipewiresrc` and an H.264 encoder. An NVIDIA GPU with CUDA speeds up transcription but is not
required.

Clone the repository:

~~~bash
git clone https://github.com/devmobasa/wayscrawl.git
cd wayscrawl
~~~

Make sure `~/.local/bin` is on your `PATH`; the installer writes its launcher there.

No model weights ship with WayScrawl. Download `ggml-large-v3-turbo.bin` following the
[whisper.cpp model instructions](https://github.com/ggml-org/whisper.cpp/blob/master/models/README.md),
then replace `/path/to/ggml-large-v3-turbo.bin` below with its real path. Any whisper.cpp ggml
model works: link it under the default name below, or set `whisper.model` in the config to its
path. Without a model, recordings have no transcript and `wayscrawl doctor` reports the model
as `MISSING`.

~~~bash
python3 scripts/bootstrap-dotnet.py    # optional: project-local copy of the pinned SDK
scripts/install.sh                     # installs into ~/.local/lib/wayscrawl, launcher at ~/.local/bin/wayscrawl
mkdir -p ~/.local/share/wayscrawl/models
ln -s /path/to/ggml-large-v3-turbo.bin ~/.local/share/wayscrawl/models/   # default model name
wayscrawl doctor                       # checks tools, monitors, the model and the daemon
~~~

If you use `scripts/bootstrap-dotnet.py`, keep the repository's `.dotnet` folder. The installed
launcher uses that runtime through `DOTNET_ROOT`.

Then add the shortcuts from `config/hyprland.lua.example` (or
`config/hyprland-legacy.conf.example`) to your Hyprland configuration. On Omarchy, two of the
keys replace Omarchy defaults; the examples show the `unbind` lines to add. See
[docs/hyprland-setup.md](docs/hyprland-setup.md).

### First recording

Reload Hyprland, then press **SUPER + ALT + SHIFT + R** to start. Narrate the problem. Press
**SUPER + ALT + SHIFT + D** to draw; press it again or Esc to stop drawing. Press
**SUPER + ALT + SHIFT + R** to stop recording. Wait for the "Evidence ready" notification, then
paste the clipboard into your agent.

## What an agent receives

A bundle is a folder under `~/.local/share/wayscrawl/bundles/<id>.wayscrawl/`:

| File | Content |
| --- | --- |
| `summary.md` | keyframes with narration and events, file paths and capture notes |
| `contact-sheet.png` | all keyframes in one image, numbered `#1`, `#2`, … |
| `frames/` | each keyframe as a screenshot, plus close-up crops |
| `manifest.json` | machine-readable data: times, ink, pins, events, transcript |
| `media/recording.mkv` | the original recording with sound |

Agents can look closer with `wayscrawl zoom BUNDLE --at 0:21` (or `--at "words you said"`),
which builds a sheet of frames around one moment. `wayscrawl skill --install claude` (or
`codex`) installs a short guide that teaches the agent how to read bundles. The format is
described in [docs/bundle-format.md](docs/bundle-format.md).

## Privacy

- Everything stays on your machine. Nothing is uploaded.
- A bundle contains whatever was on the recorded screen and microphone. Review it before you
  share it.
- Keystrokes are never stored in bundles. Window titles are recorded unless `windowTitles` is
  `false`.
- Text inside screenshots and narration is presented to agents as evidence, not instructions.
- Old recordings are cleaned up automatically by moving them to the Trash; see
  [Retention](docs/usage.md#retention).

## Documentation

- [Usage](docs/usage.md): commands, drawing, configuration, retention, export and Waybar.
- [Hyprland setup](docs/hyprland-setup.md): shortcuts, telemetry and diagnostics.
- [Bundle format](docs/bundle-format.md): what a bundle contains, for agents and tools.
- [Architecture](docs/architecture.md): how the code is organised, for contributors.

## Development

~~~bash
dotnet build WayScrawl.slnx
dotnet run --project tests/WayScrawl.Checks                 # all suites except live
dotnet run --project tests/WayScrawl.Checks -- --only=core  # one suite
dotnet run --project tests/WayScrawl.Checks -- --live       # also records this desktop
~~~

The checks are a console harness, not `dotnet test`. Suites: core, platform, pipeline,
coordinator, overlay, telemetry, portal, review and live. The overlay, telemetry and portal
suites run inside a private nested Hyprland (a headless Weston parent with its own home,
runtime directory and session bus), so injected input never reaches your desktop. They need
`weston`, `wtype`, `grim` and a test-only Aquamarine build from
`scripts/build-test-aquamarine.sh`; without it they are skipped with a note. The hold-to-draw
checks also need gcc, the libwayland-client and libxkbcommon development files, and alacritty;
those checks fail rather than skip when the tools are missing. `--live` (or `--only=live`)
records your real screen and is meant for attended runs. If you installed the SDK with the
bootstrap script, use `.dotnet/dotnet` instead of `dotnet`, or run `python3 scripts/check.py`.

The optional native Whisper timestamp check needs a model and `.cache/samples/jfk.wav`.
`WAYSCRAWL_TEST_WHISPER_MODEL` overrides the configured model. The check skips when either the
model or the sample is missing.

## Known limits

- One upright monitor per recording; rotated outputs and geometry changes during a recording
  are not handled.
- A portal window share records without the drawing overlay, because the window's position on
  screen is unknown.
- Video timing is measured from gpu-screen-recorder's first-frame timestamp and estimated for
  portal recordings. Audio alignment is estimated.
- Word timing from Whisper is approximate.
- The optional click bridge sees Hyprland input bindings, not what applications receive, and
  depends on Hyprland's Lua API (tested on 0.56.2).

## Credits

WayScrawl is inspired by [Scrawl](https://github.com/kitlangton/scrawl) for macOS. It is an
independent implementation and its bundle format is its own; see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## License

MIT; see [LICENSE](LICENSE). Vendored and adapted code keeps its own notices; see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
