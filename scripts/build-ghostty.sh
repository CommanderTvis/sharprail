#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
[ "$(uname -s)" = Darwin ] || exit 0
root="$PWD"
ghostty_commit=6d2dd585a5d87fa745d48188dd096ca6e63014d0
zig_version=0.14.1
arch="$(uname -m)"
case "$arch" in arm64) zig_arch=aarch64; rid_arch=arm64 ;; x86_64) zig_arch=x86_64; rid_arch=x64 ;; *) exit 1 ;; esac
case "${1:-}" in
  ''|"osx-$rid_arch") ;;
  *) echo 'The Ghostty bridge must be built on its target macOS architecture.' >&2; exit 1 ;;
esac
mkdir -p .tools/ghostty
# MSBuild may invoke this from multiple referencing projects.
while ! mkdir .tools/ghostty/build.lock 2>/dev/null; do sleep 1; done
trap 'rmdir "$root/.tools/ghostty/build.lock"' EXIT HUP INT TERM
fingerprint="$arch $(cat native/ghostty/SharpRailGhostty.h native/ghostty/SharpRailGhostty.m native/ghostty/config-colors.zig scripts/build-ghostty.sh | shasum -a 256)"
if [ -f .tools/ghostty/build.fingerprint ] && [ -f .tools/ghostty/libSharpRailGhostty.dylib ] &&
   [ "$(cat .tools/ghostty/build.fingerprint)" = "$fingerprint" ]; then exit 0; fi
# Download into scratch locations so an interrupted run never leaves a partial tool behind.
if [ "$(git -C .tools/ghostty/source rev-parse HEAD 2>/dev/null)" != "$ghostty_commit" ]; then
  rm -rf .tools/ghostty/source .tools/ghostty/source.partial
  git clone --depth 1 --branch v1.2.3 https://github.com/ghostty-org/ghostty.git .tools/ghostty/source.partial
  [ "$(git -C .tools/ghostty/source.partial rev-parse HEAD)" = "$ghostty_commit" ] || { echo 'Unexpected Ghostty source revision' >&2; exit 1; }
  mv .tools/ghostty/source.partial .tools/ghostty/source
fi
git -C .tools/ghostty/source show "$ghostty_commit:src/config/CApi.zig" > .tools/ghostty/source/src/config/CApi.zig
cat native/ghostty/config-colors.zig >> .tools/ghostty/source/src/config/CApi.zig
zig_dir=".tools/zig-$zig_arch-macos-$zig_version"
if ! "$zig_dir/zig" version >/dev/null 2>&1; then
  rm -rf "$zig_dir" .tools/ghostty/zig.partial
  mkdir .tools/ghostty/zig.partial
  curl -fL --retry 3 "https://ziglang.org/download/$zig_version/zig-$zig_arch-macos-$zig_version.tar.xz" -o .tools/ghostty/zig.partial/zig.tar.xz
  tar -xf .tools/ghostty/zig.partial/zig.tar.xz -C .tools/ghostty/zig.partial
  mv ".tools/ghostty/zig.partial/zig-$zig_arch-macos-$zig_version" "$zig_dir"
  rm -rf .tools/ghostty/zig.partial .tools/ghostty/zig.tar.xz
fi
# Zig 0.14 cannot resolve the arm64e-only stubs in newer Apple SDKs.
# Adjust a disposable SDK copy, leaving the installed SDK untouched.
# Newer Xcode reports a versioned symlink as the SDK path; copy its target.
sdk="$(cd "$(xcrun --sdk macosx --show-sdk-path)" && pwd -P)"
if [ "$arch" = arm64 ] && grep -q 'arm64e-macos' "$sdk/usr/lib/libSystem.tbd"; then
  if [ "$(cat .tools/ghostty/sdk.source 2>/dev/null)" != "$sdk" ] || [ ! -f .tools/ghostty/sdk/usr/lib/libSystem.tbd ]; then
    rm -rf .tools/ghostty/sdk .tools/ghostty/sdk.partial .tools/ghostty/sdk.source
    cp -cR "$sdk/" .tools/ghostty/sdk.partial
    find .tools/ghostty/sdk.partial -name '*.tbd' -type f -exec sed -i '' 's/arm64e/arm64/g' {} +
    mv .tools/ghostty/sdk.partial .tools/ghostty/sdk
    printf '%s\n' "$sdk" > .tools/ghostty/sdk.source
  fi
  mkdir -p .tools/ghostty/bin
  cat > .tools/ghostty/bin/xcrun <<'WRAPPER'
#!/bin/sh
case "$*" in
  '--sdk macosx --show-sdk-path') printf '%s\n' "$SHARPRAIL_GHOSTTY_SDK" ;;
  *) exec /usr/bin/xcrun "$@" ;;
esac
WRAPPER
  chmod +x .tools/ghostty/bin/xcrun
  export SHARPRAIL_GHOSTTY_SDK="$root/.tools/ghostty/sdk"
  export PATH="$root/.tools/ghostty/bin:$PATH"
fi
mkdir -p .tools/ghostty/bin
# Recent Apple libtool drops Zig objects whose sizes are not aligned to 8 bytes.
# LLVM's Darwin archive writer preserves them and writes a usable symbol index.
cat > .tools/ghostty/bin/libtool <<'ARCHIVER'
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
chmod +x .tools/ghostty/bin/libtool
export PATH="$root/.tools/ghostty/bin:$PATH"
cd .tools/ghostty/source
"$root/$zig_dir/zig" build --cache-dir "$root/.tools/ghostty/build-cache" \
  -Doptimize=ReleaseFast -Drenderer=metal -Dapp-runtime=none \
  -Demit-macos-app=false -Demit-docs=false -Demit-themes=false -Di18n=false -Dxcframework-target=native
cd "$root"
clang -dynamiclib -fobjc-arc -mmacosx-version-min=13.0 -O2 -Wall -Wextra -Werror -Wno-unused-parameter \
  -I .tools/ghostty/source/include native/ghostty/SharpRailGhostty.m \
  .tools/ghostty/source/macos/GhosttyKit.xcframework/macos-$arch/libghostty-fat.a \
  -framework AppKit -framework Metal -framework QuartzCore -framework IOSurface \
  -framework CoreText -framework CoreGraphics -framework Carbon -framework Foundation -framework CoreVideo \
  -lc++ -install_name @rpath/libSharpRailGhostty.dylib -o .tools/ghostty/libSharpRailGhostty.dylib
mkdir -p .tools/ghostty/resources/ghostty
cp -R .tools/ghostty/source/zig-out/share/ghostty/. .tools/ghostty/resources/ghostty/
cp -R .tools/ghostty/source/zig-out/share/terminfo .tools/ghostty/resources/
printf '%s\n' "$fingerprint" > .tools/ghostty/build.fingerprint
