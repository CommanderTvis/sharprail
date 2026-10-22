#!/bin/sh
# Links Merman's static xcframework (pinned and checksummed in mise.toml; `scripts/tool merman` prints where it is) into the dylib the UI loads. Usage: build-merman.sh <output dylib>
set -eu
cd "$(dirname "$0")/.."
[ "$(uname -s)" = Darwin ] || exit 0
out="$1"
library="$(scripts/tool merman)/macos-arm64_x86_64/libmerman_ffi.a"
[ -f "$library" ] || { echo "No Merman library at $library." >&2; exit 1; }
arch="$(uname -m)"
mkdir -p "$(dirname "$out")"
clang -dynamiclib -arch "$arch" -mmacosx-version-min=13.0 \
  "-Wl,-force_load,$library" \
  '-Wl,-exported_symbol,_merman_*' -framework CoreFoundation \
  -install_name @rpath/libSharpRailMermaid.dylib -o "$out.partial"
mv "$out.partial" "$out"
