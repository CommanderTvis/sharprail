#!/bin/sh
# Builds libSharpRail.Scintilla.so for Linux from the same pinned sources as build-native.sh, cross-compiling with the
# Zig that mise.toml pins from any macOS or Linux host against glibc 2.31 (the baseline AppImages are built for).
# libc++ is linked statically and kept private, so the library needs only libc, libm and libdl.
# Usage: build-linux.sh <linux-x64|linux-arm64> [cache directory]
set -eu
cd "$(dirname "$0")"
rid="${1:-}"
case "$rid" in
    linux-x64) target=x86_64-linux-gnu.2.31;;
    linux-arm64) target=aarch64-linux-gnu.2.31;;
    *) echo "Unsupported Linux runtime: $rid (linux-x64, linux-arm64)" >&2; exit 1;;
esac
cache="${2:-obj/native}"
mkdir -p "$cache"
zig="../../.tools/zig-${GHOSTTY_VT_ZIG:?Run through scripts/mise x -- (see mise.toml).}/zig"
[ -x "$zig" ] || { echo 'Run scripts/mise run bootstrap first.' >&2; exit 1; }

# The Scintilla and SheenBidi sources are pinned and checksummed in mise.toml and linked by `scripts/mise run bootstrap`.
scintilla=../../.tools/scintilla; bidi=../../.tools/sheenbidi
[ -d "$scintilla/src" ] && [ -d "$bidi/Source" ] || { echo 'Run scripts/mise run bootstrap first.' >&2; exit 1; }

# The patched tree is shared with the other desktop builds; a changed patch rebuilds it, and the fingerprint kept
# beside the Linux objects rebuilds those.
source="$cache/scintilla-patched"
out="$cache/$rid"
fingerprint="$(shasum -a 256 Native/EditView.patch | cut -d' ' -f1)"
if [ "$(cat "$source/.patch" 2>/dev/null)" != "$fingerprint" ]; then
    rm -rf "$source" "$source.partial"
    cp -R "$scintilla/." "$source.partial"
    chmod -R u+w "$source.partial"
    patch -s -p1 -d "$source.partial" < Native/EditView.patch
    echo "$fingerprint" > "$source.partial/.patch"
    mv "$source.partial" "$source"
fi
objects="$fingerprint $target $("$zig" version)"
if [ "$(cat "$out/.objects" 2>/dev/null)" != "$objects" ]; then
    rm -rf "$out"; mkdir -p "$out"
    echo "$objects" > "$out/.objects"
fi

flags="-target $target -Wno-nullability-completeness -O2 -DNDEBUG -fPIC -fvisibility=hidden"
[ -f "$out/SheenBidi.o" ] ||
    "$zig" cc -std=c99 $flags -DSB_CONFIG_UNITY -I "$bidi/Headers" -I "$bidi/Source" -c "$bidi/Source/SheenBidi.c" -o "$out/SheenBidi.o"
for file in "$source"/src/*.cxx Native/*.cxx; do
    case "$file" in */AutoComplete.cxx|*/CallTip.cxx|*/ScintillaBase.cxx) continue;; esac
    name="$(basename "$file" .cxx)"
    case "$file" in Native/*) name="Bridge$name";; esac
    object="$out/$name.o"
    stale=0
    for dependency in "$file" Native/*.h build-linux.sh; do [ "$dependency" -nt "$object" ] && stale=1; done
    if [ ! -f "$object" ] || [ $stale = 1 ]; then
        "$zig" c++ -std=c++17 $flags -fvisibility-inlines-hidden -I "$source/include" -I "$source/src" -I "$bidi/Headers" -c "$file" -o "$object"
    fi
done
# The version script exports the bridge alone: libc++'s headers give some type information default visibility.
echo '{ global: sr_*; local: *; };' > "$out/exports.map"
"$zig" c++ -target "$target" -shared -s "$out"/*.o \
    -Wl,-soname,libSharpRail.Scintilla.so -Wl,--no-undefined -Wl,--gc-sections -Wl,--version-script,"$out/exports.map" \
    -o "$out/libSharpRail.Scintilla.so.partial"
mv "$out/libSharpRail.Scintilla.so.partial" "$out/libSharpRail.Scintilla.so"
