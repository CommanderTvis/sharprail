#!/bin/sh
set -eu
# Builds this project's native libraries into an output directory (MSBuild's GhosttyNativeCache, under the checkout's obj):
#   libGhosttyAvaloniaView.dylib  embedded libghostty 1.2.3: AppKit view with Ghostty's Metal renderer
#   libGhosttyAvaloniaVt.dylib    libghostty-vt terminal state behind the Skia renderer's C ABI
#   resources/                    Ghostty's shell integration and terminfo, used by the embedded view
# The pinned sources, Zig caches and finished builds live in the machine-wide cache ($SHARPRAIL_CACHE_DIR/ghostty-avalonia,
# shared with build-linux.sh and build-android.sh): each finished build is kept under results/<key>, the key hashing
# everything the build reads, so checkouts on different branches never overwrite each other's libraries and a new worktree
# copies an existing result instead of rebuilding. One lock serializes the builds of every checkout.
# Usage: build-native.sh [osx-<arch>] [output-directory]
cd "$(dirname "$0")"
[ "$(uname -s)" = Darwin ] || exit 0
project="$PWD"
cache="${2:-$project/obj/native}"
mkdir -p "$cache"
cache="$(cd "$cache" && pwd -P)"
# The pins live in mise.toml's [env]; run this through `scripts/mise x --` (the MSBuild targets do). libghostty-vt's C API
# for terminal and render state is unreleased, so its commit is a main commit rather than a release.
view_commit="${GHOSTTY_VIEW_COMMIT:?Run through scripts/mise x -- (see mise.toml).}"
view_zig="$GHOSTTY_VIEW_ZIG"
vt_commit="$GHOSTTY_VT_COMMIT"
vt_zig="$GHOSTTY_VT_ZIG"
arch="$(uname -m)"
case "$arch" in arm64) rid_arch=arm64 ;; x86_64) rid_arch=x64 ;; *) exit 1 ;; esac
case "${1:-}" in
  ''|"osx-$rid_arch") ;;
  *) echo 'The Ghostty libraries must be built on their target macOS architecture.' >&2; exit 1 ;;
esac
. "$project/../../scripts/lock.sh"
. "$project/../../scripts/native-cache.sh"
work="$(sharprail_cache_dir)/ghostty-avalonia"
mkdir -p "$work/results"
work="$(cd "$work" && pwd -P)"

# The Zig toolchains are the pins of mise.toml; scripts/tool prints (and installs, when missing) one.
zig_path() {
  zig_dir="$("$project/../../scripts/tool" "zig-$1")"
  [ -x "$zig_dir/zig" ] || { echo "No Zig $1 in $zig_dir." >&2; exit 1; }
  printf '%s\n' "$zig_dir/zig"
}
checkout() {
  dir="$work/$1"
  if [ "$(git -C "$dir" rev-parse HEAD 2>/dev/null)" != "$2" ]; then
    rm -rf "$dir" "$dir.partial"
    sh "$project/../../scripts/fetch-git.sh" "$dir.partial" https://github.com/ghostty-org/ghostty.git "$2"
    mv "$dir.partial" "$dir"
  fi
}
fresh() { [ -f "$cache/$1.fingerprint" ] && [ -f "$cache/$2" ] && [ "$(cat "$cache/$1.fingerprint")" = "$3" ]; }

view_fingerprint="$arch $view_commit $view_zig $(cat Native/GhosttyView.h Native/GhosttyView.m Native/GhosttyTexture.m Native/MetalTexture.patch Native/ScrollbackMemory.patch Native/ReverseColors.patch Native/LinkUnderlines.patch Native/ConfigFile.patch Native/ExternalIo.patch Native/keyboard-mode.zig Native/file-links.zig Native/context-link.zig Native/prompt-surface.zig Native/prompts.zig Native/FileLinks.patch Native/CommandEnd.patch build-native.sh | shasum -a 256)"
vt_fingerprint="$arch $vt_commit $vt_zig $(cat Native/GhosttyVt.c Native/GhosttyClipboard.m build-native.sh | shasum -a 256)"
if fresh view libGhosttyAvaloniaView.dylib "$view_fingerprint" && fresh vt libGhosttyAvaloniaVt.dylib "$vt_fingerprint"; then
  touch "$cache/libGhosttyAvaloniaView.dylib" "$cache/libGhosttyAvaloniaVt.dylib"
  exit 0
fi
# MSBuild may invoke this from multiple referencing projects, and other checkouts share the cache.
acquire_lock "$work/build.lock"

view_result="$work/results/macos-view-$(cache_key "$view_fingerprint")"
if ! fresh view libGhosttyAvaloniaView.dylib "$view_fingerprint" && [ ! -f "$view_result/libGhosttyAvaloniaView.dylib" ]; then
  rm -rf "$view_result.partial"
  mkdir -p "$view_result.partial"
  checkout view-source "$view_commit"
  git -C "$work/view-source" show "$view_commit:src/config/CApi.zig" > "$work/view-source/src/config/CApi.zig"
  git -C "$work/view-source" show "$view_commit:include/ghostty.h" > "$work/view-source/include/ghostty.h"
  patch -s -d "$work/view-source" -p1 < Native/ConfigFile.patch
  git -C "$work/view-source" show "$view_commit:src/renderer/Metal.zig" > "$work/view-source/src/renderer/Metal.zig"
  git -C "$work/view-source" show "$view_commit:src/renderer/metal/Frame.zig" > "$work/view-source/src/renderer/metal/Frame.zig"
  git -C "$work/view-source" show "$view_commit:src/renderer/metal/Target.zig" > "$work/view-source/src/renderer/metal/Target.zig"
  patch -s -d "$work/view-source" -p1 < Native/MetalTexture.patch
  git -C "$work/view-source" show "$view_commit:src/terminal/PageList.zig" > "$work/view-source/src/terminal/PageList.zig"
  patch -s -d "$work/view-source" -p1 < Native/ScrollbackMemory.patch
  git -C "$work/view-source" show "$view_commit:src/renderer/generic.zig" > "$work/view-source/src/renderer/generic.zig"
  patch -s -d "$work/view-source" -p1 < Native/ReverseColors.patch
  patch -s -d "$work/view-source" -p1 < Native/LinkUnderlines.patch
  for source in src/Surface.zig src/apprt/embedded.zig src/termio/backend.zig; do
    git -C "$work/view-source" show "$view_commit:$source" > "$work/view-source/$source"
  done
  patch -s -d "$work/view-source" -p1 < Native/ExternalIo.patch
  git -C "$work/view-source" show "$view_commit:src/config/url.zig" > "$work/view-source/src/config/url.zig"
  for source in src/terminal/page.zig src/termio/stream_handler.zig; do
    git -C "$work/view-source" show "$view_commit:$source" > "$work/view-source/$source"
  done
  patch -s -d "$work/view-source" -p1 < Native/CommandEnd.patch
  patch -s -d "$work/view-source" -p1 < Native/FileLinks.patch
  cat Native/context-link.zig Native/prompt-surface.zig >> "$work/view-source/src/Surface.zig"
  cat Native/keyboard-mode.zig Native/file-links.zig Native/prompts.zig >> "$work/view-source/src/apprt/embedded.zig"
  view_zig_path="$(zig_path "$view_zig")"
  (
    # Zig 0.14 cannot resolve the arm64e-only stubs in newer Apple SDKs.
    # Adjust a disposable SDK copy, leaving the installed SDK untouched.
    # Newer Xcode reports a versioned symlink as the SDK path; copy its target.
    sdk="$(cd "$(xcrun --sdk macosx --show-sdk-path)" && pwd -P)"
    mkdir -p "$work/bin"
    if [ "$arch" = arm64 ] && grep -q 'arm64e-macos' "$sdk/usr/lib/libSystem.tbd"; then
      if [ "$(cat "$work/sdk.source" 2>/dev/null)" != "$sdk" ] || [ ! -f "$work/sdk/usr/lib/libSystem.tbd" ]; then
        rm -rf "$work/sdk" "$work/sdk.partial" "$work/sdk.source"
        cp -cR "$sdk/" "$work/sdk.partial"
        find "$work/sdk.partial" -name '*.tbd' -type f -exec sed -i '' 's/arm64e/arm64/g' {} +
        mv "$work/sdk.partial" "$work/sdk"
        printf '%s\n' "$sdk" > "$work/sdk.source"
      fi
      cat > "$work/bin/xcrun" <<'WRAPPER'
#!/bin/sh
case "$*" in
  '--sdk macosx --show-sdk-path') printf '%s\n' "$GHOSTTY_AVALONIA_SDK" ;;
  *) exec /usr/bin/xcrun "$@" ;;
esac
WRAPPER
      chmod +x "$work/bin/xcrun"
      export GHOSTTY_AVALONIA_SDK="$work/sdk"
    fi
    # Recent Apple libtool drops Zig objects whose sizes are not aligned to 8 bytes.
    # LLVM's Darwin archive writer preserves them and writes a usable symbol index.
    cat > "$work/bin/libtool" <<'ARCHIVER'
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
    chmod +x "$work/bin/libtool"
    export PATH="$work/bin:$PATH"
    cd "$work/view-source"
    sh "$project/../../scripts/zig-deps.sh" dir
    "$view_zig_path" build --cache-dir "$work/view-cache" \
      -Doptimize=ReleaseFast -Drenderer=metal -Dapp-runtime=none \
      -Demit-macos-app=false -Demit-docs=false -Demit-themes=false -Di18n=false -Dxcframework-target=native
  )
  clang -dynamiclib -fobjc-arc -mmacosx-version-min=13.0 -O2 -Wall -Wextra -Werror -Wno-unused-parameter \
    -I "$work/view-source/include" Native/GhosttyView.m Native/GhosttyTexture.m \
    "$work/view-source/macos/GhosttyKit.xcframework/macos-$arch/libghostty-fat.a" \
    -framework AppKit -framework Metal -framework QuartzCore -framework IOSurface \
    -framework CoreText -framework CoreGraphics -framework Carbon -framework Foundation -framework CoreVideo \
    -lc++ -install_name @rpath/libGhosttyAvaloniaView.dylib -o "$view_result.partial/libGhosttyAvaloniaView.dylib"
  mkdir -p "$view_result.partial/resources/ghostty"
  cp -R "$work/view-source/zig-out/share/ghostty/." "$view_result.partial/resources/ghostty/"
  cp -R "$work/view-source/zig-out/share/terminfo" "$view_result.partial/resources/"
  publish_result "$view_result"
fi
if ! fresh view libGhosttyAvaloniaView.dylib "$view_fingerprint"; then
  install_output "$view_result" "$cache" libGhosttyAvaloniaView.dylib resources
  printf '%s\n' "$view_fingerprint" > "$cache/view.fingerprint"
fi

vt_result="$work/results/macos-vt-$(cache_key "$vt_fingerprint")"
if ! fresh vt libGhosttyAvaloniaVt.dylib "$vt_fingerprint" && [ ! -f "$vt_result/libGhosttyAvaloniaVt.dylib" ]; then
  rm -rf "$vt_result.partial"
  mkdir -p "$vt_result.partial"
  checkout vt-source "$vt_commit"
  vt_zig_path="$(zig_path "$vt_zig")"
  sh "$project/../../scripts/zig-deps.sh" tgz
  (cd "$work/vt-source" && "$vt_zig_path" build --cache-dir "$work/vt-cache" --prefix "$work/vt-out" -Demit-lib-vt -Doptimize=ReleaseFast)
  clang -dynamiclib -fobjc-arc -mmacosx-version-min=13.0 -O2 -Wall -Wextra -Werror -DGHOSTTY_STATIC \
    -I "$work/vt-out/include" Native/GhosttyVt.c Native/GhosttyClipboard.m "$work/vt-out/lib/libghostty-vt.a" -framework AppKit \
    -install_name @rpath/libGhosttyAvaloniaVt.dylib -o "$vt_result.partial/libGhosttyAvaloniaVt.dylib"
  publish_result "$vt_result"
fi
if ! fresh vt libGhosttyAvaloniaVt.dylib "$vt_fingerprint"; then
  install_output "$vt_result" "$cache" libGhosttyAvaloniaVt.dylib
  printf '%s\n' "$vt_fingerprint" > "$cache/vt.fingerprint"
fi
# The scripts come from the same pinned commit as the VT library, whose OSC 133 handling they are written for.
if [ "$(cat "$cache/shell.commit" 2>/dev/null)" != "$vt_commit" ] || [ ! -d "$cache/shell/shell-integration" ]; then
  checkout vt-source "$vt_commit"
  rm -rf "$cache/shell"
  mkdir -p "$cache/shell"
  cp -R "$cache/vt-source/src/shell-integration" "$cache/shell/"
  rm -f "$cache/shell/shell-integration/README.md"
  printf '%s\n' "$vt_commit" > "$cache/shell.commit"
fi
# Whichever library was already current is not rebuilt; touching both tells MSBuild this run is done.
touch "$cache/libGhosttyAvaloniaView.dylib" "$cache/libGhosttyAvaloniaVt.dylib"
