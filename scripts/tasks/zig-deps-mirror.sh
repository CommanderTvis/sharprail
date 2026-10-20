#!/bin/sh
# Rebuilds scripts/zig-deps.lock and the mirror archive for the pinned Ghostty revisions (run it after changing a Ghostty
# pin in mise.toml): scripts/mise run zig-deps:mirror. It builds the Ghostty libraries on this macOS host into a fresh Zig
# cache, so the package list is measured, then archives every package fetched from a host other than github.com and deps.files.ghostty.org (Codeberg today). Publishing is a
# separate, explicit step (printed at the end); this script uploads nothing.
set -eu
cd "$(dirname "$0")/../.."
root="$PWD"
[ "$(uname -s)" = Darwin ] || { echo 'Run on macOS: only it builds both Ghostty libraries.' >&2; exit 1; }
work="$root/.bench/zig-deps"
rm -rf "$work"; mkdir -p "$work/gc" "$work/out"
# build-native.sh builds the view (Zig 0.14, package directories) and libghostty-vt (Zig 0.16, p/<hash>.tar.gz) into one
# fresh cache; every other target (Linux, Windows, WASM) fetches a subset of the VT packages.
ZIG_GLOBAL_CACHE_DIR="$work/gc" ZIG_DEPS_NO_RESTORE=1 sh "$root/src/Ghostty.Avalonia/build-native.sh" "osx-$(uname -m | sed 's/x86_64/x64/')" "$work/native"
uv run --no-project python -I scripts/zig-deps-pack.py "$work/out" "$work/gc" "$work/gc" "$work/native/view-source" "$work/native/vt-source" "$work/gc/p"
cp "$work/out/zig-deps.lock" scripts/zig-deps.lock
tag="$(awk '$1 == "tag" { print $2 }' scripts/zig-deps.lock)"
asset="$(awk '$1 == "asset" { print $2 }' scripts/zig-deps.lock)"
echo "Publish with: gh release create $tag --repo $(awk '$1 == "repo" { print $2 }' scripts/zig-deps.lock) --title $tag --notes 'Zig dependency mirror (scripts/zig-deps.sh)' $work/out/$asset"
echo "Then commit scripts/zig-deps.lock and remove $work."
