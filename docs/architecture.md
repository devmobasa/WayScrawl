# Architecture

This page explains how WayScrawl is put together, for people and agents who change the code.

## Processes

WayScrawl is one executable, `wayscrawl`, that plays several roles, plus a separate review app.

| Process | Started by | Job |
| --- | --- | --- |
| client | you, a hotkey or Waybar | sends one command to the daemon and prints the reply |
| daemon (`wayscrawl daemon`) | the first client that needs it, detached with `setsid` | owns recordings, the overlay, telemetry and retention |
| processing worker (`wayscrawl process`) | the daemon, after a recording stops | builds the bundle in its own process |
| transcription worker (`wayscrawl transcribe`) | the processing worker | runs Whisper, so a crash or timeout cannot take the builder down |
| recorder | the daemon | `gpu-screen-recorder`, or GStreamer for the portal backend |
| review app (`wayscrawl-review`) | `wayscrawl review` | an Avalonia window; another client of the daemon |

Clients reach the daemon through a Unix socket at `$XDG_RUNTIME_DIR/wayscrawl/control.sock`, one
JSON object per line. Each request has an ID, and the daemon remembers the last 256, so a
retried request is not executed twice. A file lock next to the socket keeps a single daemon per
user.

## Recording flow

1. `toggle` reaches the daemon's `SessionCoordinator`, which prepares the capture backend, starts
   one session clock and starts the recorder. The recorder's first-frame timestamp maps video
   time onto that clock.
2. On a monitor recording, the coordinator shows the overlay: a wlr-layer-shell surface drawn with
   SkiaSharp on its own Wayland dispatch thread. In pass-through it has an empty input region; in
   drawing mode it takes the pointer and keyboard.
3. Overlay events (strokes, pins, removals, markers, mode changes) and Hyprland telemetry are
   written to a crash-safe journal (`session.json` and `journal.jsonl`) under
   `~/.local/state/wayscrawl/sessions/<id>/`.
4. `stop` finalises the recorder, hides the overlay and starts the processing worker. If the
   recorder was killed, the partial file is remuxed so it stays readable.
5. The worker (`EvidenceBuilder`) samples the video, transcribes the narration, plans keyframes
   (`KeyframePlanner`), extracts frames, plans close-up crops (`CropPlanner`), draws the contact
   sheet and publishes the bundle atomically (`BundleStore`).
6. The daemon copies the agent summary to the clipboard and sends a notification.

The coordinator serialises commands through a short "lane" and keeps at most one stop or cancel
in flight, so overlapping hotkeys cannot start two recordings or stop the wrong one.

## Projects

| Project | Responsibility |
| --- | --- |
| `src/WayScrawl.Core` | bundle model, schema rules and migration, session state machine, clock, ports (interfaces), daemon protocol, keyframe and crop planning, summary text |
| `src/WayScrawl.Platform` | bundle store, process supervision and the spawn gate, gpu-screen-recorder, ffmpeg, Hyprland IPC, telemetry and hold control, Trash and path safety |
| `src/WayScrawl.Rendering` | SkiaSharp ink and pin renderer shared by the overlay, the evidence builder and zoom; image scaling, contact sheets |
| `src/WayScrawl.Wayland` | generated Wayland bindings and the native overlay |
| `src/WayScrawl.Portal` | ScreenCast portal client (D-Bus) and the GStreamer recorder |
| `src/WayScrawl.Transcription` | whisper.cpp through Whisper.net; used only inside the transcription worker |
| `src/WayScrawl.Application` | session coordinator, daemon and client, evidence builder, zoom, export and retention |
| `src/WayScrawl.Cli` | the `wayscrawl` command, its worker verbs and the embedded agent guide |
| `src/WayScrawl.Review` | the Avalonia review app |
| `tools/Wayland.SourceGenerator` | vendored source generator that turns `protocols/*.xml` into C# bindings |
| `tests/WayScrawl.Checks` | the console check harness |

Core has no platform dependencies. Platform-specific code sits behind the ports in
`src/WayScrawl.Core/Ports.cs` (`ICaptureBackend`, `IOverlayHost`, `ITranscriber`,
`IInteractionSource`, `IAgentSink`), and `src/WayScrawl.Cli/Wiring.cs` connects the real
implementations.

## Rules the code relies on

- **One clock.** Every event is stamped in microseconds of the session clock
  (`CLOCK_MONOTONIC`). Media streams carry a `ClockMap` from their own timestamps to it.
- **Ink in source pixels.** Overlay coordinates are converted to the recorded image's pixels as
  they are captured, so the planner, renderer and exporter never deal with logical coordinates.
- **Composition is checked, not assumed.** The builder samples an annotation frame to decide
  whether ink is already in the captured pixels (`CapturedInk`) or missing (`CleanSource`), and
  draws vector ink only onto `CleanSource` frames. The check is a heuristic.
- **Nothing synthetic by accident.** A missing backend fails visibly; only `demo` writes synthetic
  data, and it says so in the manifest.
- **The user's compositor is not ours.** Code that installs Lua into the running Hyprland adds
  bindings once and never removes them, because Hyprland 0.56 aborts on removal. A token and a
  deadline keep them inactive outside a recording.
- **Retention fails safe.** Cleanup moves data to the Trash by default. Copies into the Trash
  are verified and flushed to disk, and each source file's identity and contents are checked
  again just before it is unlinked. WayScrawl's internal copy of a session is deleted only once
  the recording is verified to survive elsewhere, and a session whose bundle cannot be
  identified is never deleted permanently.

## Tests

`tests/WayScrawl.Checks` is a console program (`dotnet run --project tests/WayScrawl.Checks`) with
suites selected by `--only=NAME`: core, platform, pipeline, coordinator, overlay, telemetry,
portal, review and live.

- **core, platform, pipeline, coordinator** run headless. The pipeline suite records synthetic
  media with ffmpeg and also covers zoom, export and retention against temporary directories.
- **overlay, telemetry, portal** start a private nested Hyprland inside a headless Weston, with
  their own home, runtime directory and session bus, and drive it with a virtual pointer and
  keyboard. Nothing reaches your desktop. They need `scripts/build-test-aquamarine.sh`.
- **review** runs the review app in its headless smoke mode.
- **live** records your real screen and runs only with `--live` or `--only=live`.

A suite whose recognised prerequisites are missing is skipped with a note, and the harness still
exits successfully, so read the output when you need the integration suites to have run. Other
missing tools, such as gcc or alacritty for the hold checks, make the run fail.
