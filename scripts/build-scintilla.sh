#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
[ "$(uname -s)" = Darwin ] || { echo 'The Scintilla bridge currently supports macOS only.' >&2; exit 1; }
arch="${1:-$(uname -m)}"
case "$arch" in arm64|x86_64) ;; *) echo "Unsupported macOS architecture: $arch" >&2; exit 1;; esac
version=567
archive=.tools/scintilla$version.tgz
source=.tools/scintilla
mkdir -p .tools
if [ ! -f "$archive" ]; then
    curl -fL "https://www.scintilla.org/scintilla$version.tgz" -o "$archive.tmp"
    mv "$archive.tmp" "$archive"
fi
printf '%s  %s\n' 19e4476706750056f86ca70974810e4a5b08e73477e13f7d9fae78243606f6a7 "$archive" | shasum -a 256 -c -
if [ ! -f "$source/version.txt" ]; then
    mkdir -p "$source"
    tar -xzf "$archive" -C "$source" --strip-components=1
fi
out=".tools/scintilla-build/$arch"
mkdir -p "$out"
for file in "$source"/src/*.cxx native/SharpRail.Scintilla/*.cxx; do
    case "$file" in */AutoComplete.cxx|*/CallTip.cxx|*/ScintillaBase.cxx) continue;; esac
    name="$(basename "$file" .cxx)"
    case "$file" in native/*) name="Bridge$name";; esac
    object="$out/$name.o"
    if [ ! -f "$object" ] || [ "$file" -nt "$object" ] || [ native/SharpRail.Scintilla/Bridge.h -nt "$object" ] || [ native/SharpRail.Scintilla/Includes.h -nt "$object" ] || [ scripts/build-scintilla.sh -nt "$object" ]; then
        xcrun clang++ -std=c++17 -O2 -DNDEBUG -fPIC -fvisibility=hidden -arch "$arch" -mmacosx-version-min=12.0 \
            -I "$source/include" -I "$source/src" -c "$file" -o "$object"
    fi
done
xcrun clang++ -dynamiclib -arch "$arch" -mmacosx-version-min=12.0 "$out"/*.o \
    -Wl,-install_name,@rpath/libSharpRail.Scintilla.dylib -o "$out/libSharpRail.Scintilla.dylib"
