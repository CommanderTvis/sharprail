#!/bin/sh
set -eu
# Builds the Skia renderer's native library for Android into an output directory (MSBuild's GhosttyNativeCache):
#   android/<abi>/libGhosttyAvaloniaVt.so  libghostty-vt terminal state behind the Skia renderer's C ABI
# It shares build-native.sh's pinned libghostty-vt source, Zig toolchain (mise.toml) and machine-wide cache, where each
# finished build is kept under results/<key> (see scripts/native-cache.sh). The NDK is ANDROID_NDK_HOME,
# or the one `scripts/tool android-ndk` prints.
# Usage: build-android.sh <arm64-v8a|x86_64> [output-directory]
cd "$(dirname "$0")"
project="$PWD"
abi="${1:-}"
case "$abi" in
  arm64-v8a) triple=aarch64-linux-android ;;
  x86_64) triple=x86_64-linux-android ;;
  *) echo 'Usage: build-android.sh <arm64-v8a|x86_64> [output-directory]' >&2; exit 1 ;;
esac
api=24
cache="${2:-$project/obj/native}"
mkdir -p "$cache"
cache="$(cd "$cache" && pwd -P)"
vt_commit="${GHOSTTY_VT_COMMIT:?Run through scripts/mise x -- (see mise.toml).}"
vt_zig="$GHOSTTY_VT_ZIG"
case "$(uname -s)" in Darwin) ndk_host=darwin-x86_64 ;; Linux) ndk_host=linux-x86_64 ;; *) echo 'The Android build needs macOS or Linux.' >&2; exit 1 ;; esac
ndk="${ANDROID_NDK_HOME:-$("$project/../../scripts/tool" android-ndk)}"
[ -f "$ndk/source.properties" ] || { echo "No Android NDK at $ndk; run scripts/mise install with MISE_ENV=android." >&2; exit 1; }
. "$project/../../scripts/lock.sh"
. "$project/../../scripts/native-cache.sh"
work="$(sharprail_cache_dir)/ghostty-avalonia"
mkdir -p "$work/results"
work="$(cd "$work" && pwd -P)"

out="$cache/android/$abi"
fingerprint="$abi $api $vt_commit $vt_zig $(cat "$ndk/source.properties" Native/GhosttyVt.c build-android.sh | shasum -a 256)"
if [ -f "$out/libGhosttyAvaloniaVt.so" ] && [ "$(cat "$out/vt.fingerprint" 2>/dev/null)" = "$fingerprint" ]; then exit 0; fi
# MSBuild may invoke this for several ABIs, alongside the desktop build, and other checkouts share the cache.
acquire_lock "$work/build.lock"
result="$work/results/android-$abi-vt-$(cache_key "$fingerprint")"

if [ ! -f "$result/libGhosttyAvaloniaVt.so" ]; then
  zig="$("$project/../../scripts/tool" "zig-$vt_zig")"
  [ -x "$zig/zig" ] || { echo "No Zig $vt_zig in $zig." >&2; exit 1; }
  source="$work/vt-source"
  if [ "$(git -C "$source" rev-parse HEAD 2>/dev/null)" != "$vt_commit" ]; then
    rm -rf "$source" "$source.partial"
    sh "$project/../../scripts/fetch-git.sh" "$source.partial" https://github.com/ghostty-org/ghostty.git "$vt_commit"
    mv "$source.partial" "$source"
  fi

  rm -rf "$result.partial"
  mkdir -p "$result.partial"
  sh "$project/../../scripts/zig-deps.sh" tgz
  # Ghostty's build takes bionic's headers and startup objects from the NDK and aligns segments to 16 KB pages.
  (cd "$source" && ANDROID_NDK_HOME="$ndk" "$zig/zig" build --cache-dir "$work/vt-cache" --prefix "$work/android-$abi-vt-out" \
    -Demit-lib-vt -Doptimize=ReleaseFast "-Dtarget=$triple.$api")
  "$ndk/toolchains/llvm/prebuilt/$ndk_host/bin/$triple$api-clang" -shared -fPIC -O2 -Wall -Wextra -Werror -DGHOSTTY_STATIC \
    -I "$work/android-$abi-vt-out/include" Native/GhosttyVt.c "$work/android-$abi-vt-out/lib/libghostty-vt.a" \
    -Wl,-z,max-page-size=16384 -Wl,--no-undefined -Wl,--exclude-libs,ALL -Wl,-soname,libGhosttyAvaloniaVt.so -o "$result.partial/libGhosttyAvaloniaVt.so"
  "$ndk/toolchains/llvm/prebuilt/$ndk_host/bin/llvm-strip" --strip-unneeded "$result.partial/libGhosttyAvaloniaVt.so"
  publish_result "$result"
fi
install_output "$result" "$out" libGhosttyAvaloniaVt.so
printf '%s\n' "$fingerprint" > "$out/vt.fingerprint"
