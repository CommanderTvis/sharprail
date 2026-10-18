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

# The Scintilla and SheenBidi sources are pinned and checksummed in mise.toml and linked by `scripts/mise run bootstrap`.
scintilla=../../.tools/scintilla; bidi=../../.tools/sheenbidi
[ -d "$scintilla/src" ] && [ -d "$bidi/Source" ] || { echo 'Run scripts/mise run bootstrap first.' >&2; exit 1; }

# The patched tree is disposable: a changed patch rebuilds it and every object from scratch.
source="$cache/scintilla-patched"
out="$cache/$arch"
fingerprint="$(shasum -a 256 Native/EditView.patch | cut -d' ' -f1)"
if [ "$(cat "$source/.patch" 2>/dev/null)" != "$fingerprint" ]; then
    rm -rf "$source" "$source.partial" "$out"
    cp -R "$scintilla/." "$source.partial"
    chmod -R u+w "$source.partial"
    patch -s -p1 -d "$source.partial" < Native/EditView.patch
    echo "$fingerprint" > "$source.partial/.patch"
    mv "$source.partial" "$source"
fi

mkdir -p "$out"
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
