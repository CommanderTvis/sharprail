#!/bin/sh
set -eu
# Builds the Skia renderer's native library for Android into the cache build-native.sh uses:
#   android/<abi>/libGhosttyAvaloniaVt.so  libghostty-vt terminal state behind the Skia renderer's C ABI
# It shares that script's pinned libghostty-vt source and Zig toolchain. The NDK is ANDROID_NDK_HOME,
# or the one scripts/android-ndk.sh installs into the checkout.
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
vt_commit="$(sed -n 's/^vt_commit=//p' build-native.sh)"
vt_zig="$(sed -n 's/^vt_zig=//p' build-native.sh)"
case "$(uname -s)" in Darwin) zig_os=macos; ndk_host=darwin-x86_64 ;; Linux) zig_os=linux; ndk_host=linux-x86_64 ;; *) echo 'The Android build needs macOS or Linux.' >&2; exit 1 ;; esac
case "$(uname -m)" in arm64|aarch64) zig_arch=aarch64 ;; x86_64) zig_arch=x86_64 ;; *) exit 1 ;; esac
ndk="${ANDROID_NDK_HOME:-$(sh "$project/../../scripts/android-ndk.sh")}"
# MSBuild may invoke this for several ABIs, alongside the desktop build.
while ! mkdir "$cache/build.lock" 2>/dev/null; do sleep 1; done
trap 'rmdir "$cache/build.lock"' EXIT HUP INT TERM

out="$cache/android/$abi"
fingerprint="$abi $api $(cat "$ndk/source.properties" Native/GhosttyVt.c build-native.sh build-android.sh | shasum -a 256)"
if [ -f "$out/libGhosttyAvaloniaVt.so" ] && [ "$(cat "$out/vt.fingerprint" 2>/dev/null)" = "$fingerprint" ]; then exit 0; fi

# Download into scratch locations so an interrupted run never leaves a partial tool behind.
zig="$cache/zig-$zig_arch-$zig_os-$vt_zig"
if ! "$zig/zig" version >/dev/null 2>&1; then
  rm -rf "$zig" "$cache/zig.partial"
  mkdir "$cache/zig.partial"
  curl -fL --retry 3 "https://ziglang.org/download/$vt_zig/zig-$zig_arch-$zig_os-$vt_zig.tar.xz" -o "$cache/zig.partial/zig.tar.xz"
  tar -xf "$cache/zig.partial/zig.tar.xz" -C "$cache/zig.partial"
  mv "$cache/zig.partial/zig-$zig_arch-$zig_os-$vt_zig" "$zig"
  rm -rf "$cache/zig.partial"
fi
source="$cache/vt-source"
if [ "$(git -C "$source" rev-parse HEAD 2>/dev/null)" != "$vt_commit" ]; then
  rm -rf "$source" "$source.partial"
  git init -q "$source.partial"
  git -C "$source.partial" fetch -q --depth 1 https://github.com/ghostty-org/ghostty.git "$vt_commit"
  git -C "$source.partial" checkout -q FETCH_HEAD
  [ "$(git -C "$source.partial" rev-parse HEAD)" = "$vt_commit" ] || { echo 'Unexpected Ghostty source revision' >&2; exit 1; }
  mv "$source.partial" "$source"
fi

mkdir -p "$out"
# Ghostty's build takes bionic's headers and startup objects from the NDK and aligns segments to 16 KB pages.
(cd "$source" && ANDROID_NDK_HOME="$ndk" "$zig/zig" build --cache-dir "$cache/vt-cache" --prefix "$out/vt-out" \
  -Demit-lib-vt -Doptimize=ReleaseFast "-Dtarget=$triple.$api")
"$ndk/toolchains/llvm/prebuilt/$ndk_host/bin/$triple$api-clang" -shared -fPIC -O2 -Wall -Wextra -Werror -DGHOSTTY_STATIC \
  -I "$out/vt-out/include" Native/GhosttyVt.c "$out/vt-out/lib/libghostty-vt.a" \
  -Wl,-z,max-page-size=16384 -Wl,--no-undefined -Wl,--exclude-libs,ALL -Wl,-soname,libGhosttyAvaloniaVt.so -o "$out/libGhosttyAvaloniaVt.so.partial"
"$ndk/toolchains/llvm/prebuilt/$ndk_host/bin/llvm-strip" --strip-unneeded "$out/libGhosttyAvaloniaVt.so.partial"
mv "$out/libGhosttyAvaloniaVt.so.partial" "$out/libGhosttyAvaloniaVt.so"
printf '%s\n' "$fingerprint" > "$out/vt.fingerprint"
