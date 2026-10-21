# Vendored Wayland source generator

Vendored into WayScrawl from SharpMarker (sharpmarker-next commit 740b079).

This directory contains `Wayland.SourceGenerator` 0.0.2 from upstream commit
`e3b0200ca532ec8a41aec49082e6634762b10bb7`, under its MIT license.

SharpMarker carries these local deviations from the pinned upstream source:

- `WaylandSourceGenerator.Events.cs` treats an absent XML argument array as
  empty. Zero-argument events such as `wl_buffer.release` therefore generate
  their event method and dispatcher; the upstream package skips those events.
- `WaylandSourceGenerator.Classes.cs` makes the native dispatcher an
  `UnmanagedCallersOnly` entry point. This records the native-to-managed
  transition so GC can walk callback frames. Handler exceptions are captured
  per thread and rethrown by `CompleteDispatch` after native dispatch returns,
  rather than unwinding across libwayland. Generated display dispatch and
  roundtrip methods, and SharpMarker's display wrapper, complete this bridge.
- The generated `wl_display_connect` declaration marshals its optional display
  name as UTF-8 (`LPUTF8Str`), including absolute Unix socket paths.

WayScrawl adds one further deviation:

- `LibWayland` keys its proxy table by a process-unique token, installed as the
  proxy's dispatcher data and recovered with `wl_proxy_get_user_data`, instead of
  `wl_proxy_get_id`. Protocol object ids repeat across connections, so with two
  displays in one process (the overlay plus a test's virtual pointer, or two
  overlays) events were dispatched to the other connection's proxy.

In the SharpMarker repository, the zero-argument generated-binding test, fake-compositor
callback tests in `WaylandDispatchBridgeTests`, and socket/output integration tests protect
these contracts. In WayScrawl, the overlay suite runs the overlay and a virtual pointer on two
connections in one process. The separate unmanaged-entry-point assertion protects the GC
ABI contract. Preserve the upstream MIT license when changing or rebasing the
vendored code; recheck each deviation against the new pinned source.
