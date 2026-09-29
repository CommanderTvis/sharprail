#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
[ "$(uname -s)" = Darwin ] || exit 0
root="$PWD"
version=0.7.0
archive_sha256=29a46ea5da99bad60888581073e88345ce370366a4855b4e2996355a7d0e880b
arch="$(uname -m)"
mkdir -p .tools/merman
# MSBuild may invoke this from multiple referencing projects.
while ! mkdir .tools/merman/build.lock 2>/dev/null; do sleep 1; done
trap 'rmdir "$root/.tools/merman/build.lock"' EXIT HUP INT TERM
fingerprint="$arch $version $(shasum -a 256 scripts/build-merman.sh)"
if [ -f .tools/merman/build.fingerprint ] && [ -f .tools/merman/libSharpRailMermaid.dylib ] &&
   [ "$(cat .tools/merman/build.fingerprint)" = "$fingerprint" ]; then exit 0; fi
# Merman's release publishes its C ABI for Apple platforms as a static xcframework.
archive=".tools/merman/Merman.xcframework-v$version.zip"
if [ "$(shasum -a 256 "$archive" 2>/dev/null | cut -d' ' -f1)" != "$archive_sha256" ]; then
  rm -f "$archive.partial"
  curl -fL --retry 3 "https://github.com/Latias94/merman/releases/download/v$version/Merman.xcframework-v$version.zip" -o "$archive.partial"
  [ "$(shasum -a 256 "$archive.partial" | cut -d' ' -f1)" = "$archive_sha256" ] || { echo 'Unexpected Merman archive checksum' >&2; exit 1; }
  mv "$archive.partial" "$archive"
fi
rm -rf .tools/merman/xcframework
unzip -q "$archive" -d .tools/merman/xcframework
clang -dynamiclib -arch "$arch" -mmacosx-version-min=13.0 \
  -Wl,-force_load,.tools/merman/xcframework/Merman.xcframework/macos-arm64_x86_64/libmerman_ffi.a \
  '-Wl,-exported_symbol,_merman_*' -framework CoreFoundation \
  -install_name @rpath/libSharpRailMermaid.dylib -o .tools/merman/libSharpRailMermaid.dylib
printf '%s\n' "$fingerprint" > .tools/merman/build.fingerprint
