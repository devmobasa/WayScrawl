# Third-party notices

WayScrawl is an independent C# implementation. It contains no code, binaries, icons, fonts or
model weights from Scrawl.

## Vendored and adapted code

- `tools/Wayland.SourceGenerator`: Wayland.SourceGenerator 0.0.2 by affederaffe, MIT License
  (see `tools/Wayland.SourceGenerator/LICENSE`), with SharpMarker's documented changes, copied
  from the sharpmarker-next repository at commit 740b079b2610e57c1cc3febdabae187c2f1794e7.
  Keep its LICENSE with the code.
- Adapted from the same SharpMarker repository, by the same author as WayScrawl:
  `src/WayScrawl.Wayland/Interop` (display, registry, child-proxy and POSIX helpers),
  `src/WayScrawl.Portal/PortalRequest.cs` (portal request handling) and the nested-compositor
  test pattern in `tests/WayScrawl.Checks/NestedHyprland.cs`.
- `protocols/`: Wayland protocol XML (wayland, xdg-shell, viewporter, fractional-scale,
  cursor-shape, tablet-v2, wlr-layer-shell, wlr-virtual-pointer). Each file carries its own
  copyright and permission notice in its header.

## Packages

These packages are installed through NuGet when you build; they are not stored in this
repository. All are MIT licensed. Versions are pinned in `Directory.Packages.props`.

- SkiaSharp and SkiaSharp.NativeAssets.Linux: drawing ink, frames and contact sheets
- Whisper.net, Whisper.net.Runtime and Whisper.net.Runtime.Cuda.Linux: speech transcription
  (they include whisper.cpp, also MIT)
- Tmds.DBus.Protocol: the screen-share portal client
- Avalonia, Avalonia.Desktop and Avalonia.Themes.Fluent: the review window
- Microsoft.CodeAnalysis.CSharp and Microsoft.CodeAnalysis.Analyzers: used only at build time by
  the Wayland source generator

Whisper model weights are not included. Download a ggml model separately and point
`whisper.model` in the config at it, or link it under the default name (see the README).

The SDK bootstrap script downloads Microsoft's .NET SDK on your machine; the SDK keeps its own
license files.

## Inspiration

WayScrawl's workflow is inspired by Scrawl (https://github.com/kitlangton/scrawl), MIT License,
copyright 2026 Kit Langton. No Scrawl code was copied or translated.
