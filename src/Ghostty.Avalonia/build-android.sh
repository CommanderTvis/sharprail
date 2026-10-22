#!/bin/sh
set -eu
# Builds the Skia renderer's native library for Android into the cache build-native.sh uses:
#   android/<abi>/libGhosttyAvaloniaVt.so  libghostty-vt terminal state behind the Skia renderer's C ABI
# It shares that script's pinned libghostty-vt source and Zig toolchain (mise.toml). The NDK is ANDROID_NDK_HOME,
# or the one `scripts/tool android-ndk` prints.
# Usage: build-android.sh <arm64-v8a|x86_64> [cache-directory]
cd "$(dirname "$0")"
project="$PWD"
abi="${1:-}"
case "$abi" in
  arm64-v8a) triple=aarch64-linux-android ;;
  x86_64) triple=x86_64-linux-android ;;
  *) echo 'Usage: build-android.sh <arm64-v8a|x86_64> [cache-directory]' >&2; exit 1 ;;
esac
api=24
cache="${2:-$project/../../.tools/ghostty-avalonia}"
mkdir -p "$cache"
cache="$(cd "$cache" && pwd -P)"
vt_commit="${GHOSTTY_VT_COMMIT:?Run through scripts/mise x -- (see mise.toml).}"
vt_zig="$GHOSTTY_VT_ZIG"
case "$(uname -s)" in Darwin) ndk_host=darwin-x86_64 ;; Linux) ndk_host=linux-x86_64 ;; *) echo 'The Android build needs macOS or Linux.' >&2; exit 1 ;; esac
ndk="${ANDROID_NDK_HOME:-$("$project/../../scripts/tool" android-ndk)}"
[ -f "$ndk/source.properties" ] || { echo "No Android NDK at $ndk; run scripts/mise install with MISE_ENV=android." >&2; exit 1; }
# MSBuild may invoke this for several ABIs, alongside the desktop build.
. "$project/../../scripts/lock.sh"
acquire_lock "$cache/build.lock"

out="$cache/android/$abi"
fingerprint="$abi $api $(cat "$ndk/source.properties" Native/GhosttyVt.c build-android.sh | shasum -a 256)"
if [ -f "$out/libGhosttyAvaloniaVt.so" ] && [ "$(cat "$out/vt.fingerprint" 2>/dev/null)" = "$fingerprint" ]; then exit 0; fi

zig="$("$project/../../scripts/tool" "zig-$vt_zig")"
[ -x "$zig/zig" ] || { echo "No Zig $vt_zig in $zig." >&2; exit 1; }
source="$cache/vt-source"
if [ "$(git -C "$source" rev-parse HEAD 2>/dev/null)" != "$vt_commit" ]; then
  rm -rf "$source" "$source.partial"
  sh "$project/../../scripts/fetch-git.sh" "$source.partial" https://github.com/ghostty-org/ghostty.git "$vt_commit"
  mv "$source.partial" "$source"
fi

mkdir -p "$out"
sh "$project/../../scripts/zig-deps.sh" tgz
# Ghostty's build takes bionic's headers and startup objects from the NDK and aligns segments to 16 KB pages.
(cd "$source" && ANDROID_NDK_HOME="$ndk" "$zig/zig" build --cache-dir "$cache/vt-cache" --prefix "$out/vt-out" \
  -Demit-lib-vt -Doptimize=ReleaseFast "-Dtarget=$triple.$api")
"$ndk/toolchains/llvm/prebuilt/$ndk_host/bin/$triple$api-clang" -shared -fPIC -O2 -Wall -Wextra -Werror -DGHOSTTY_STATIC \
  -I "$out/vt-out/include" Native/GhosttyVt.c "$out/vt-out/lib/libghostty-vt.a" \
  -Wl,-z,max-page-size=16384 -Wl,--no-undefined -Wl,--exclude-libs,ALL -Wl,-soname,libGhosttyAvaloniaVt.so -o "$out/libGhosttyAvaloniaVt.so.partial"
"$ndk/toolchains/llvm/prebuilt/$ndk_host/bin/llvm-strip" --strip-unneeded "$out/libGhosttyAvaloniaVt.so.partial"
mv "$out/libGhosttyAvaloniaVt.so.partial" "$out/libGhosttyAvaloniaVt.so"
printf '%s\n' "$fingerprint" > "$out/vt.fingerprint"
