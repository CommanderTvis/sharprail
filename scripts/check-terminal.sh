#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
cache="$PWD/.tools/ghostty-avalonia"
sh src/Ghostty.Avalonia/build-native.sh '' "$cache"
mkdir -p .bench
clang -fobjc-arc -Wall -Wextra -Werror -Wno-unused-parameter \
  src/Ghostty.Avalonia/Native/terminal-check.m -L "$cache" -lGhosttyAvaloniaView \
  -framework AppKit -Wl,-rpath,"$cache" -o .bench/terminal-check
GHOSTTY_RESOURCES_DIR="$cache/resources/ghostty" .bench/terminal-check "$PWD"
