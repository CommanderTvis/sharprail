#!/bin/sh
set -eu
# Builds the Skia renderer's native library for Linux into an output directory (MSBuild's GhosttyNativeCache):
#   linux-<arch>/libGhosttyAvaloniaVt.so  libghostty-vt terminal state behind the Skia renderer's C ABI
# It shares build-native.sh's pinned libghostty-vt source, Zig toolchain (mise.toml) and machine-wide cache, where each
# finished build is kept under results/<key> (see scripts/native-cache.sh) and cross-compiles with Zig from any
# macOS or Linux host, against glibc 2.31 (the baseline AppImages are built for) so the library runs on every distribution
# at least that old. Zig links only libc, libm and libdl; nothing else is needed at run time.
# Usage: build-linux.sh <linux-x64|linux-arm64> [output-directory]
cd "$(dirname "$0")"
project="$PWD"
rid="${1:-}"
case "$rid" in
  linux-x64) target=x86_64-linux-gnu.2.31 ;;
  linux-arm64) target=aarch64-linux-gnu.2.31 ;;
  *) echo 'Usage: build-linux.sh <linux-x64|linux-arm64> [output-directory]' >&2; exit 1 ;;
esac
cache="${2:-$project/obj/native}"
mkdir -p "$cache"
cache="$(cd "$cache" && pwd -P)"
vt_commit="${GHOSTTY_VT_COMMIT:?Run through scripts/mise x -- (see mise.toml).}"
vt_zig="$GHOSTTY_VT_ZIG"
. "$project/../../scripts/lock.sh"
. "$project/../../scripts/native-cache.sh"
work="$(sharprail_cache_dir)/ghostty-avalonia"
mkdir -p "$work/results"
work="$(cd "$work" && pwd -P)"

out="$cache/$rid"
zig="$("$project/../../scripts/tool" "zig-$vt_zig")"
[ -x "$zig/zig" ] || { echo "No Zig $vt_zig in $zig." >&2; exit 1; }
fingerprint="$vt_commit $target $("$zig/zig" version) $(cat Native/GhosttyVt.c build-linux.sh | shasum -a 256)"
if [ -f "$out/libGhosttyAvaloniaVt.so" ] && [ "$(cat "$out/vt.fingerprint" 2>/dev/null)" = "$fingerprint" ]; then exit 0; fi
# MSBuild may invoke this alongside other native builds, and other checkouts share the cache.
acquire_lock "$work/build.lock"
result="$work/results/$rid-vt-$(cache_key "$fingerprint")"

if [ ! -f "$result/libGhosttyAvaloniaVt.so" ]; then
  source="$work/vt-source"
  if [ "$(git -C "$source" rev-parse HEAD 2>/dev/null)" != "$vt_commit" ]; then
    rm -rf "$source" "$source.partial"
    sh "$project/../../scripts/fetch-git.sh" "$source.partial" https://github.com/ghostty-org/ghostty.git "$vt_commit"
    mv "$source.partial" "$source"
  fi

  rm -rf "$result.partial"
  mkdir -p "$result.partial"
  sh "$project/../../scripts/zig-deps.sh" tgz
  (cd "$source" && "$zig/zig" build --cache-dir "$work/vt-cache" --prefix "$work/$rid-vt-out" -Demit-lib-vt -Doptimize=ReleaseFast "-Dtarget=$target")
  # The version script exports the bridge alone, so Ghostty's own symbols stay private to the library.
  echo '{ global: gav_*; local: *; };' > "$work/$rid-exports.map"
  "$zig/zig" cc -target "$target" -shared -fPIC -s -O2 -Wall -Wextra -Werror -DGHOSTTY_STATIC \
    -I "$work/$rid-vt-out/include" Native/GhosttyVt.c "$work/$rid-vt-out/lib/libghostty-vt.a" \
    -Wl,--no-undefined -Wl,--gc-sections -Wl,--version-script,"$work/$rid-exports.map" -Wl,-soname,libGhosttyAvaloniaVt.so \
    -o "$result.partial/libGhosttyAvaloniaVt.so"
  publish_result "$result"
fi
install_output "$result" "$out" libGhosttyAvaloniaVt.so
printf '%s\n' "$fingerprint" > "$out/vt.fingerprint"
