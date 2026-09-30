#!/bin/sh
# Builds libSharpRail.Scintilla.dylib from Scintilla, the SheenBidi bidi engine and the
# Skia platform bridge. Usage: build-native.sh <arm64|x86_64> [cache directory]
set -eu
cd "$(dirname "$0")"
[ "$(uname -s)" = Darwin ] || { echo 'The Scintilla bridge currently supports macOS only.' >&2; exit 1; }
arch="${1:-$(uname -m)}"
case "$arch" in arm64|x86_64) ;; *) echo "Unsupported macOS architecture: $arch" >&2; exit 1;; esac
cache="${2:-obj/native}"
mkdir -p "$cache"

# Downloads a pinned archive and unpacks it once.
fetch() {
    archive="$cache/$2"; target="$cache/$4"
    if [ ! -f "$archive" ]; then
        curl -fL --retry 3 "$1" -o "$archive.tmp"
        mv "$archive.tmp" "$archive"
    fi
    printf '%s  %s\n' "$3" "$archive" | shasum -a 256 -c - >/dev/null
    if [ ! -d "$target" ]; then
        rm -rf "$target.partial"; mkdir -p "$target.partial"
        tar -xzf "$archive" -C "$target.partial" --strip-components=1
        mv "$target.partial" "$target"
    fi
}
fetch https://www.scintilla.org/scintilla567.tgz scintilla567.tgz \
    19e4476706750056f86ca70974810e4a5b08e73477e13f7d9fae78243606f6a7 scintilla-5.6.7
fetch https://github.com/Tehreer/SheenBidi/archive/refs/tags/v3.0.0.tar.gz sheenbidi-3.0.0.tgz \
    86c56014034739ba39a24c23eb00323b0bf6f737354f665786015fca842af786 sheenbidi-3.0.0

# The patched tree is disposable: a changed patch rebuilds it and every object from scratch.
source="$cache/scintilla-patched"
out="$cache/$arch"
fingerprint="$(shasum -a 256 Native/EditView.patch | cut -d' ' -f1)"
if [ "$(cat "$source/.patch" 2>/dev/null)" != "$fingerprint" ]; then
    rm -rf "$source" "$source.partial" "$out"
    cp -R "$cache/scintilla-5.6.7" "$source.partial"
    patch -s -p1 -d "$source.partial" < Native/EditView.patch
    echo "$fingerprint" > "$source.partial/.patch"
    mv "$source.partial" "$source"
fi

mkdir -p "$out"
bidi="$cache/sheenbidi-3.0.0"
flags="-O2 -DNDEBUG -fPIC -fvisibility=hidden -arch $arch -mmacosx-version-min=12.0"
[ -f "$out/SheenBidi.o" ] ||
    xcrun clang -std=c99 $flags -DSB_CONFIG_UNITY -I "$bidi/Headers" -I "$bidi/Source" -c "$bidi/Source/SheenBidi.c" -o "$out/SheenBidi.o"
for file in "$source"/src/*.cxx Native/*.cxx; do
    case "$file" in */AutoComplete.cxx|*/CallTip.cxx|*/ScintillaBase.cxx) continue;; esac
    name="$(basename "$file" .cxx)"
    case "$file" in Native/*) name="Bridge$name";; esac
    object="$out/$name.o"
    stale=0
    for dependency in "$file" Native/*.h build-native.sh; do [ "$dependency" -nt "$object" ] && stale=1; done
    if [ ! -f "$object" ] || [ $stale = 1 ]; then
        xcrun clang++ -std=c++17 $flags -I "$source/include" -I "$source/src" -I "$bidi/Headers" -c "$file" -o "$object"
    fi
done
xcrun clang++ -dynamiclib -arch "$arch" -mmacosx-version-min=12.0 "$out"/*.o \
    -Wl,-install_name,@rpath/libSharpRail.Scintilla.dylib -o "$out/libSharpRail.Scintilla.dylib"
