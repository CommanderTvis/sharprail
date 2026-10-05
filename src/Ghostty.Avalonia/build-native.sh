#!/bin/sh
set -eu
# Builds this project's native libraries into a cache directory:
#   libGhosttyAvaloniaView.dylib  embedded libghostty 1.2.3: AppKit view with Ghostty's Metal renderer
#   libGhosttyAvaloniaVt.dylib    libghostty-vt terminal state behind the Skia renderer's C ABI
#   resources/                    Ghostty's shell integration and terminfo, used by the embedded view
# Usage: build-native.sh [osx-<arch>] [cache-directory]
cd "$(dirname "$0")"
[ "$(uname -s)" = Darwin ] || exit 0
project="$PWD"
cache="${2:-$project/obj/native}"
mkdir -p "$cache"
cache="$(cd "$cache" && pwd -P)"
view_commit=6d2dd585a5d87fa745d48188dd096ca6e63014d0 # v1.2.3
view_zig=0.14.1
# libghostty-vt's C API for terminal and render state is unreleased; pin the main commit it was built against.
vt_commit=59c2dc032aba42aa5064bf206cc27286add3e9d8
vt_zig=0.16.0
arch="$(uname -m)"
case "$arch" in arm64) zig_arch=aarch64; rid_arch=arm64 ;; x86_64) zig_arch=x86_64; rid_arch=x64 ;; *) exit 1 ;; esac
case "${1:-}" in
  ''|"osx-$rid_arch") ;;
  *) echo 'The Ghostty libraries must be built on their target macOS architecture.' >&2; exit 1 ;;
esac
# MSBuild may invoke this from multiple referencing projects.
while ! mkdir "$cache/build.lock" 2>/dev/null; do sleep 1; done
trap 'rmdir "$cache/build.lock"' EXIT HUP INT TERM

# Download into scratch locations so an interrupted run never leaves a partial tool behind.
zig_path() {
  dir="$cache/zig-$zig_arch-macos-$1"
  if ! "$dir/zig" version >/dev/null 2>&1; then
    rm -rf "$dir" "$cache/zig.partial"
    mkdir "$cache/zig.partial"
    curl -fL --retry 3 "https://ziglang.org/download/$1/zig-$zig_arch-macos-$1.tar.xz" -o "$cache/zig.partial/zig.tar.xz"
    tar -xf "$cache/zig.partial/zig.tar.xz" -C "$cache/zig.partial"
    mv "$cache/zig.partial/zig-$zig_arch-macos-$1" "$dir"
    rm -rf "$cache/zig.partial"
  fi
  printf '%s\n' "$dir/zig"
}
checkout() {
  dir="$cache/$1"
  if [ "$(git -C "$dir" rev-parse HEAD 2>/dev/null)" != "$2" ]; then
    rm -rf "$dir" "$dir.partial"
    git init -q "$dir.partial"
    git -C "$dir.partial" fetch -q --depth 1 https://github.com/ghostty-org/ghostty.git "$2"
    git -C "$dir.partial" checkout -q FETCH_HEAD
    [ "$(git -C "$dir.partial" rev-parse HEAD)" = "$2" ] || { echo 'Unexpected Ghostty source revision' >&2; exit 1; }
    mv "$dir.partial" "$dir"
  fi
}
fresh() { [ -f "$cache/$1.fingerprint" ] && [ -f "$cache/$2" ] && [ "$(cat "$cache/$1.fingerprint")" = "$3" ]; }

view_fingerprint="$arch $(cat Native/GhosttyView.h Native/GhosttyView.m Native/GhosttyTexture.m Native/MetalTexture.patch Native/ScrollbackMemory.patch Native/ReverseColors.patch Native/ConfigFile.patch Native/ExternalIo.patch build-native.sh | shasum -a 256)"
if ! fresh view libGhosttyAvaloniaView.dylib "$view_fingerprint"; then
  checkout view-source "$view_commit"
  git -C "$cache/view-source" show "$view_commit:src/config/CApi.zig" > "$cache/view-source/src/config/CApi.zig"
  git -C "$cache/view-source" show "$view_commit:include/ghostty.h" > "$cache/view-source/include/ghostty.h"
  patch -s -d "$cache/view-source" -p1 < Native/ConfigFile.patch
  git -C "$cache/view-source" show "$view_commit:src/renderer/Metal.zig" > "$cache/view-source/src/renderer/Metal.zig"
  git -C "$cache/view-source" show "$view_commit:src/renderer/metal/Frame.zig" > "$cache/view-source/src/renderer/metal/Frame.zig"
  git -C "$cache/view-source" show "$view_commit:src/renderer/metal/Target.zig" > "$cache/view-source/src/renderer/metal/Target.zig"
  patch -s -d "$cache/view-source" -p1 < Native/MetalTexture.patch
  git -C "$cache/view-source" show "$view_commit:src/terminal/PageList.zig" > "$cache/view-source/src/terminal/PageList.zig"
  patch -s -d "$cache/view-source" -p1 < Native/ScrollbackMemory.patch
  git -C "$cache/view-source" show "$view_commit:src/renderer/generic.zig" > "$cache/view-source/src/renderer/generic.zig"
  patch -s -d "$cache/view-source" -p1 < Native/ReverseColors.patch
  for source in src/Surface.zig src/apprt/embedded.zig src/termio/backend.zig; do
    git -C "$cache/view-source" show "$view_commit:$source" > "$cache/view-source/$source"
  done
  patch -s -d "$cache/view-source" -p1 < Native/ExternalIo.patch
  view_zig_path="$(zig_path "$view_zig")"
  (
    # Zig 0.14 cannot resolve the arm64e-only stubs in newer Apple SDKs.
    # Adjust a disposable SDK copy, leaving the installed SDK untouched.
    # Newer Xcode reports a versioned symlink as the SDK path; copy its target.
    sdk="$(cd "$(xcrun --sdk macosx --show-sdk-path)" && pwd -P)"
    mkdir -p "$cache/bin"
    if [ "$arch" = arm64 ] && grep -q 'arm64e-macos' "$sdk/usr/lib/libSystem.tbd"; then
      if [ "$(cat "$cache/sdk.source" 2>/dev/null)" != "$sdk" ] || [ ! -f "$cache/sdk/usr/lib/libSystem.tbd" ]; then
        rm -rf "$cache/sdk" "$cache/sdk.partial" "$cache/sdk.source"
        cp -cR "$sdk/" "$cache/sdk.partial"
        find "$cache/sdk.partial" -name '*.tbd' -type f -exec sed -i '' 's/arm64e/arm64/g' {} +
        mv "$cache/sdk.partial" "$cache/sdk"
        printf '%s\n' "$sdk" > "$cache/sdk.source"
      fi
      cat > "$cache/bin/xcrun" <<'WRAPPER'
#!/bin/sh
case "$*" in
  '--sdk macosx --show-sdk-path') printf '%s\n' "$GHOSTTY_AVALONIA_SDK" ;;
  *) exec /usr/bin/xcrun "$@" ;;
esac
WRAPPER
      chmod +x "$cache/bin/xcrun"
      export GHOSTTY_AVALONIA_SDK="$cache/sdk"
    fi
    # Recent Apple libtool drops Zig objects whose sizes are not aligned to 8 bytes.
    # LLVM's Darwin archive writer preserves them and writes a usable symbol index.
    cat > "$cache/bin/libtool" <<'ARCHIVER'
#!/bin/sh
set -eu
[ "$1" = -static ] && [ "$2" = -o ]
output="$3"
shift 3
scratch="$(mktemp -d "$output.XXXXXX")"
trap 'rmdir "$scratch"' EXIT
/opt/homebrew/opt/llvm/bin/llvm-ar --format=darwin qcLs "$scratch/library.a" "$@"
mv "$scratch/library.a" "$output"
ARCHIVER
    chmod +x "$cache/bin/libtool"
    export PATH="$cache/bin:$PATH"
    cd "$cache/view-source"
    "$view_zig_path" build --cache-dir "$cache/view-cache" \
      -Doptimize=ReleaseFast -Drenderer=metal -Dapp-runtime=none \
      -Demit-macos-app=false -Demit-docs=false -Demit-themes=false -Di18n=false -Dxcframework-target=native
  )
  clang -dynamiclib -fobjc-arc -mmacosx-version-min=13.0 -O2 -Wall -Wextra -Werror -Wno-unused-parameter \
    -I "$cache/view-source/include" Native/GhosttyView.m Native/GhosttyTexture.m \
    "$cache/view-source/macos/GhosttyKit.xcframework/macos-$arch/libghostty-fat.a" \
    -framework AppKit -framework Metal -framework QuartzCore -framework IOSurface \
    -framework CoreText -framework CoreGraphics -framework Carbon -framework Foundation -framework CoreVideo \
    -lc++ -install_name @rpath/libGhosttyAvaloniaView.dylib -o "$cache/libGhosttyAvaloniaView.dylib"
  rm -rf "$cache/resources"
  mkdir -p "$cache/resources/ghostty"
  cp -R "$cache/view-source/zig-out/share/ghostty/." "$cache/resources/ghostty/"
  cp -R "$cache/view-source/zig-out/share/terminfo" "$cache/resources/"
  printf '%s\n' "$view_fingerprint" > "$cache/view.fingerprint"
fi

vt_fingerprint="$arch $(cat Native/GhosttyVt.c Native/GhosttyClipboard.m build-native.sh | shasum -a 256)"
if ! fresh vt libGhosttyAvaloniaVt.dylib "$vt_fingerprint"; then
  checkout vt-source "$vt_commit"
  vt_zig_path="$(zig_path "$vt_zig")"
  (cd "$cache/vt-source" && "$vt_zig_path" build --cache-dir "$cache/vt-cache" --prefix "$cache/vt-out" -Demit-lib-vt -Doptimize=ReleaseFast)
  clang -dynamiclib -fobjc-arc -mmacosx-version-min=13.0 -O2 -Wall -Wextra -Werror -DGHOSTTY_STATIC \
    -I "$cache/vt-out/include" Native/GhosttyVt.c Native/GhosttyClipboard.m "$cache/vt-out/lib/libghostty-vt.a" -framework AppKit \
    -install_name @rpath/libGhosttyAvaloniaVt.dylib -o "$cache/libGhosttyAvaloniaVt.dylib"
  printf '%s\n' "$vt_fingerprint" > "$cache/vt.fingerprint"
fi
