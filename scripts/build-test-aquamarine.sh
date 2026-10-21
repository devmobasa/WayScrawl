#!/usr/bin/env bash
# Builds a test-only Aquamarine for the isolated overlay checks (tests/WayScrawl.Checks).
#
# The checks run a private Hyprland nested inside headless Weston. Packaged Aquamarine binds
# wl_compositor at version 6 while Weston offers 5, so the nested Hyprland aborts. This build
# caps each Wayland-backend bind at the advertised version. It is never used by WayScrawl
# itself and is not installed anywhere: output goes to .cache/aquamarine in this repository.
#
# Requires: git, cmake, ninja, a C++ compiler, pugixml, and Hyprland's build dependencies.
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
work="$root/.cache/aquamarine-build"
version=$(pacman -Q aquamarine 2>/dev/null | awk '{print $2}' | cut -d- -f1)
version=${version:-0.15.1}
mkdir -p "$work"
cd "$work"
[ -d hyprwayland-scanner ] || git clone -q --depth 1 --branch v0.4.6 https://github.com/hyprwm/hyprwayland-scanner
[ -d aquamarine ] || git clone -q --depth 1 --branch "v$version" https://github.com/hyprwm/aquamarine
sed -i -E 's/(wl_registry_bind\(\(wl_registry\*\)waylandState\.registry->resource\(\), id, &[a-z0-9_]+_interface, )([0-9]+)\)/\1std::min<uint32_t>(version, \2))/' \
    aquamarine/src/backend/Wayland.cpp
cmake -S hyprwayland-scanner -B hyprwayland-scanner/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_INSTALL_PREFIX="$work/prefix" >/dev/null
cmake --build hyprwayland-scanner/build >/dev/null
cmake --install hyprwayland-scanner/build >/dev/null
PATH="$work/prefix/bin:$PATH" cmake -S aquamarine -B aquamarine/build -G Ninja -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_PREFIX_PATH="$work/prefix" >/dev/null
PATH="$work/prefix/bin:$PATH" cmake --build aquamarine/build >/dev/null
mkdir -p "$root/.cache/aquamarine"
cp -a aquamarine/build/libaquamarine.so* "$root/.cache/aquamarine/"
echo "Test Aquamarine $version: $root/.cache/aquamarine"
