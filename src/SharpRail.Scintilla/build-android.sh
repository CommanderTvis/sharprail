#!/bin/sh
# Builds libSharpRail.Scintilla.so for Android from the same pinned sources as build-native.sh,
# with the NDK that scripts/android-ndk.sh installs. Usage: build-android.sh <arm64-v8a|x86_64> [cache directory]
set -eu
cd "$(dirname "$0")"
abi="${1:-}"
case "$abi" in
    arm64-v8a) triple=aarch64-linux-android;;
    x86_64) triple=x86_64-linux-android;;
    *) echo "Unsupported Android ABI: $abi (arm64-v8a or x86_64)" >&2; exit 1;;
esac
cache="${2:-obj/native}"
mkdir -p "$cache"
case "$(uname -s)" in Darwin) host=darwin-x86_64;; Linux) host=linux-x86_64;; *) echo 'The Android NDK needs macOS or Linux.' >&2; exit 1;; esac
bin="$(sh ../../scripts/android-ndk.sh)/toolchains/llvm/prebuilt/$host/bin"
api=24

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

# The patched tree is shared with build-native.sh, which owns the desktop objects; a changed patch
# rebuilds it, and the fingerprint kept beside the Android objects rebuilds those.
source="$cache/scintilla-patched"
out="$cache/android/$abi"
fingerprint="$(shasum -a 256 Native/EditView.patch | cut -d' ' -f1)"
if [ "$(cat "$source/.patch" 2>/dev/null)" != "$fingerprint" ]; then
    rm -rf "$source" "$source.partial"
    cp -R "$cache/scintilla-5.6.7" "$source.partial"
    patch -s -p1 -d "$source.partial" < Native/EditView.patch
    echo "$fingerprint" > "$source.partial/.patch"
    mv "$source.partial" "$source"
fi
if [ "$(cat "$out/.patch" 2>/dev/null)" != "$fingerprint" ]; then
    rm -rf "$out"; mkdir -p "$out"
    echo "$fingerprint" > "$out/.patch"
fi

bidi="$cache/sheenbidi-3.0.0"
flags="-O2 -DNDEBUG -fPIC -fvisibility=hidden"
[ -f "$out/SheenBidi.o" ] ||
    "$bin/$triple$api-clang" -std=c99 $flags -DSB_CONFIG_UNITY -I "$bidi/Headers" -I "$bidi/Source" -c "$bidi/Source/SheenBidi.c" -o "$out/SheenBidi.o"
for file in "$source"/src/*.cxx Native/*.cxx; do
    case "$file" in */AutoComplete.cxx|*/CallTip.cxx|*/ScintillaBase.cxx) continue;; esac
    name="$(basename "$file" .cxx)"
    case "$file" in Native/*) name="Bridge$name";; esac
    object="$out/$name.o"
    stale=0
    for dependency in "$file" Native/*.h build-android.sh; do [ "$dependency" -nt "$object" ] && stale=1; done
    if [ ! -f "$object" ] || [ $stale = 1 ]; then
        "$bin/$triple$api-clang++" -std=c++17 $flags -fvisibility-inlines-hidden -I "$source/include" -I "$source/src" -I "$bidi/Headers" -c "$file" -o "$object"
    fi
done
# libc++ is linked statically and kept private, so the library needs only the system's libc, libm and libdl.
# Its headers give some type information default visibility; the version script exports the bridge alone.
echo '{ global: sr_*; local: *; };' > "$out/exports.map"
"$bin/$triple$api-clang++" -shared -static-libstdc++ "$out"/*.o \
    -Wl,-soname,libSharpRail.Scintilla.so -Wl,-z,max-page-size=16384 -Wl,--exclude-libs,ALL -Wl,--no-undefined -Wl,--gc-sections \
    -Wl,--version-script,"$out/exports.map" -o "$out/libSharpRail.Scintilla.so"
"$bin/llvm-strip" --strip-unneeded "$out/libSharpRail.Scintilla.so"
