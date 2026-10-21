# Usage

## Commands

Recording commands talk to the WayScrawl daemon. Only `start` and `toggle` start it when
needed; other daemon commands need it running. `daemon` runs it in the foreground.

~~~text
toggle                    start or stop a recording
start | stop | cancel     explicit control; cancel discards the recording
draw [on|off|toggle]      drawing vs pass-through (Esc also leaves drawing)
clear | undo              remove ink, in drawing or pass-through
marker [TEXT]             mark this moment; always kept as a keyframe (moments within 400 ms can share one)
status [--waybar [--follow]]   daemon and session state, or a Waybar JSON stream
daemon                    run the daemon in the foreground
shutdown                  stop the daemon after finishing any recording
~~~

Evidence commands work on bundles. `BUNDLE` can be a bundle folder or `latest` (the newest
bundle by creation time). `review` is the exception: it takes a folder, and opens the newest
capture when given none.

~~~text
paste [BUNDLE]            copy the agent summary to the clipboard (default: latest)
send BUNDLE [--clipboard] print (or copy) the same summary
inspect BUNDLE [--json]   validate and print a bundle
zoom BUNDLE --at TIME|PHRASE [--span 2] [--frames 12] [--full]
frame BUNDLE SECONDS [OUT.png]   extract one more frame from the recording
export BUNDLE [--out PATH] [--crop auto|full|x,y,w,h] [--size 1920]
review [BUNDLE]           open the review window
sessions                  list recorded sessions and their bundles
process SESSION_DIR       build a bundle from a recorded session
clean [--dry-run]         run retention now, or preview it
pin BUNDLE [--off]        protect a capture from retention, or remove the protection
skill [--install claude|codex [--force]]   print or install the agent guide
demo NEW_DIRECTORY        write a synthetic bundle
doctor [--connect]        check the environment
~~~

A second record toggle within 0.4 seconds of starting is ignored, so a double press does not
produce an empty recording. Explicit `stop` and `cancel` always work.

## Drawing

`draw toggle` (SUPER+ALT+SHIFT+D) switches the overlay between pass-through, where your clicks
and typing reach your applications, and drawing, where the overlay takes the mouse and keyboard.
Esc or a right-click returns to pass-through.

- Drag to draw. A click that moves less than 12 logical pixels and is released within one
  second drops a numbered pin instead.
- Ink fades 1.5 seconds after you last lift the pen. Starting a new stroke before then keeps
  all current ink on screen, so a circle and an arrow fade together. Set `inkFadeSeconds` to `0`
  to keep ink until you clear or undo it.
- `Ctrl+Z` undoes the last stroke or pin, `Delete` clears everything, `M` adds a marker and
  `1`–`6` choose a colour. Shortcuts follow your keyboard layout.
- `wayscrawl clear` and `wayscrawl undo` work without entering drawing mode.

With gpu-screen-recorder the overlay is captured with the screen, so your ink is part of the
recording. When ink is missing from the captured pixels, WayScrawl detects that and draws the ink onto the frames instead.

### Hold to draw

Set `"drawTrigger": "hold"` to draw only while you hold a key. `drawHoldKey` chooses the key
(default `Shift_L`); left or right Shift, Control, Alt and Super are supported.

- While the key is held, the overlay takes the mouse but not the keyboard, so typing still
  reaches your application. Shift-click selection in applications is unavailable while
  recording with Shift as the hold key.
- Move the pointer a little after pressing the key, before the first click: Hyprland does not
  send the pointer to the overlay until it moves.
- Releasing the key stops drawing at once. If a mouse button is still held, the pointer
  returns to your application when that button is released.
- Each hold ends after 10 seconds, even if the key is still down, so a missed key release can
  never leave the overlay holding the mouse. Press the key again to keep drawing.
- The draw hotkey still selects normal drawing mode, and hold presses do not change it.
- Key presses that produce no ink are not recorded.

The first recording with an overlay adds bindings for the chosen key to the running Hyprland.
They stay until Hyprland reloads its configuration, and a per-recording token keeps them
inactive outside a recording. `wayscrawl draw hold` and `draw release` give explicit control too.

## The agent summary

When a bundle is published, its summary is copied to the clipboard. It starts with the
recording length, monitor and bundle path, followed by one line per keyframe: the `#N` number
from the contact sheet, the time, why the moment was chosen and what you said, with notable
events (window switches, clicks, markers, pins) indented below. It never includes screen
coordinates, and includes window titles only when `windowTitles` is on. It is capped at about
4,000 characters; longer captures point to `summary.md`.

`wayscrawl paste` copies it again. Nothing is ever typed into another application for you.
Set `clipboard` to `path` to copy only the path of `summary.md` when a bundle is published, or
`off` to copy nothing automatically; `wayscrawl paste` always copies the full summary.

## Looking closer: zoom and frame

`wayscrawl zoom latest --at 0:21` builds a contact sheet of frames around one moment: by default
12 frames over 2 seconds, a quarter of them before the moment. `--at "the width changes"` finds
the moment by what you said. `--full` shows whole frames instead of the close-up crop. The sheet
is written to the bundle's `zoom/` folder, and the command prints its path, each frame's time and
the narration nearby. Sample numbers in a zoom sheet are not keyframe numbers.

`wayscrawl frame BUNDLE 21.5 /tmp/frame.png` extracts a single frame. It refuses to overwrite an
existing file or write through a symbolic link at the destination.

## Agent guide

`wayscrawl skill` prints a short guide that teaches coding agents to read bundles.
`skill --install claude` writes it to `~/.claude/skills/wayscrawl/SKILL.md`, and
`--install codex` to `~/.agents/skills/wayscrawl/SKILL.md`. An existing different guide is kept;
`--force` replaces it and keeps a backup.

## Window crops

Frames get a close-up crop around fresh ink. Frames without ink get a crop around the window
you were using, when that window covers less than about 60 % of the screen. Full frames are
always kept. Window positions come from Hyprland events and a poll every two seconds while
recording. Portal window shares get no window crop.

## Export

`wayscrawl export BUNDLE` writes an H.264/AAC MP4, by default to `WayScrawl/` in your Videos
folder. `--crop auto` (the default) uses the area covered by the close-up crops, `full` the whole
frame, or `x,y,w,h` in pixels of the recording. The long edge is capped at `--size` (1920) and video
is never upscaled. NVENC is used when it works, with libx264 as the fallback. An existing output
file is never overwritten. Ink is included when it was captured with the screen
(gpu-screen-recorder); otherwise the export says so.

## Waybar

`wayscrawl status --waybar --follow` prints a JSON line whenever the state changes, and
reconnects when the daemon restarts. Merge `config/waybar.jsonc.example` into your bar to show
recording, drawing and processing state. Left-click starts or stops a recording; right-click
toggles drawing. The bar is on screen, so it appears in recordings of its monitor.

## Retention

Recordings are large, so old captures are cleaned up automatically.

- After `mediaDays` (default 7): the bundle's `media/` moves to the Trash, and WayScrawl's
  internal copy of the session is deleted. Keyframes, crops, the contact sheet and the summary
  stay. `frame`, `zoom` and `export` then report that the video is gone, and `process` can no
  longer rebuild the bundle.
- After `bundleDays` (default 30): the whole bundle moves to the Trash.
- A recording that never produced a bundle, for example because processing failed, is kept for
  `orphanDays` (default 30) and then moved to the Trash whole. Restore it and run
  `wayscrawl process DIR` to rebuild it. The daemon notifies you when this happens.

WayScrawl deletes its internal copy of a session only after verifying that the recording
survives in the bundle or in the Trash; everything else goes to the Trash. With
`"purge": true`, expired files are deleted instead, and a recording without a bundle can be
deleted after `mediaDays`. A recording whose bundle WayScrawl cannot identify is never deleted
permanently, though it can move to the Trash after `orphanDays`. Pinned captures
(`wayscrawl pin`), anything changed in the last day, captures in use and anything restored from
the Trash are left alone.

`wayscrawl clean --dry-run` shows what a pass would do. The daemon runs a pass a minute after it
starts and then daily, at low priority. Set `"retention": null` to keep everything. Emptying the
Trash is what frees the space.

## Configuration

`~/.config/wayscrawl/config.json`. Every key is optional.

The daemon reads the config when it starts. After editing it, run `wayscrawl shutdown`; the
next `wayscrawl start` or `wayscrawl toggle` loads the new settings.

| Key | Default | Meaning |
| --- | --- | --- |
| `backend` | `gpu-screen-recorder` | or `portal` (desktop chooser, any monitor or window) |
| `output` | `focused` | monitor connector name, for gpu-screen-recorder |
| `microphone` | `default_input` | source name, or `null` for no audio |
| `framesPerSecond` | `30` | capture rate |
| `overlay` | `true` | show the drawing overlay while recording a monitor |
| `drawColor`, `drawWidth` | `#ff3b30`, `6` | ink colour and width |
| `inkFadeSeconds` | `1.5` | seconds after the pen lifts; `0` keeps ink |
| `drawTrigger`, `drawHoldKey` | `toggle`, `Shift_L` | `hold` enables hold to draw |
| `telemetry` | `socket2` | `off`, `socket2` (window focus and workspaces) or `bridge` (adds click positions) |
| `windowTitles` | `true` | record window titles |
| `clipboard` | `paste` | `paste` (summary), `path` or `off` |
| `notifications` | `true` | desktop notifications |
| `maxFrames`, `frameLongEdge`, `cropLongEdge` | `12`, `1920`, `1600` | evidence size; `maxFrames` is a soft budget that protected marker and annotation moments can exceed |
| `whisper.model`, `.language`, `.runtime` | data folder model, `auto`, `auto` | `runtime`: `auto`, `cuda` or `cpu` |
| `bundleDirectory` | `~/.local/share/wayscrawl/bundles` | where bundles are published |
| `retention` | `{"mediaDays": 7, "bundleDays": 30, "orphanDays": 30, "purge": false}` | `null` keeps everything |

An invalid value falls back for that key only, and `wayscrawl doctor`, the daemon log and
`status` report it. Privacy settings fall back to their most private value, and invalid
retention settings turn automatic cleanup off. A file that is not valid JSON at all leaves the
microphone, window titles, telemetry, clipboard copying and retention off until it is fixed.
The older `copyPathToClipboard: false` still turns clipboard copying off.

## Telemetry

With the default `socket2`, WayScrawl records window focus, workspace changes and window
positions while recording. `bridge` also records mouse clicks and their positions through
non-consuming key bindings added to the running Hyprland; they never change your config files
and do nothing outside a recording. See [hyprland-setup.md](hyprland-setup.md#interaction-telemetry).
