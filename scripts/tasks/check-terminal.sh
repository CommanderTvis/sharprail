#!/bin/sh
set -eu
# `scripts/mise run check:terminal`: builds the Ghostty libraries and runs the native shell/Metal probe.
cd "$(dirname "$0")/../.."
cache="$PWD/src/Ghostty.Avalonia/obj/native"
sh src/Ghostty.Avalonia/build-native.sh '' "$cache"
mkdir -p .bench
clang -fobjc-arc -Wall -Wextra -Werror -Wno-unused-parameter \
  src/Ghostty.Avalonia/Native/terminal-check.m -L "$cache" -lGhosttyAvaloniaView \
  -framework AppKit -Wl,-rpath,"$cache" -o .bench/terminal-check
GHOSTTY_RESOURCES_DIR="$cache/resources/ghostty" .bench/terminal-check "$PWD"
