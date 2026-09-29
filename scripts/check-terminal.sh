#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
sh scripts/build-ghostty.sh
mkdir -p .bench
clang -fobjc-arc -Wall -Wextra -Werror -Wno-unused-parameter \
  native/ghostty/terminal-check.m -L .tools/ghostty -lSharpRailGhostty \
  -framework AppKit -Wl,-rpath,"$PWD/.tools/ghostty" -o .bench/terminal-check
GHOSTTY_RESOURCES_DIR="$PWD/.tools/ghostty/resources/ghostty" .bench/terminal-check "$PWD"
