#!/bin/sh
# Builds libSharpRail.Scintilla.so for Android from the same pinned sources as build-native.sh,
# with the NDK that `scripts/mise run bootstrap:android` links as .tools/android-ndk. Usage: build-android.sh <arm64-v8a|x86_64> [cache directory]
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
bin="${ANDROID_NDK_HOME:-../../.tools/android-ndk}/toolchains/llvm/prebuilt/$host/bin"
api=24

# The Scintilla and SheenBidi sources are pinned and checksummed in mise.toml and linked by `scripts/mise run bootstrap`.
scintilla=../../.tools/scintilla; bidi=../../.tools/sheenbidi
[ -d "$scintilla/src" ] && [ -d "$bidi/Source" ] || { echo 'Run scripts/mise run bootstrap first.' >&2; exit 1; }

# The patched tree is shared with build-native.sh, which owns the desktop objects; a changed patch
# rebuilds it, and the fingerprint kept beside the Android objects rebuilds those.
source="$cache/scintilla-patched"
out="$cache/android/$abi"
fingerprint="$(shasum -a 256 Native/EditView.patch | cut -d' ' -f1)"
if [ "$(cat "$source/.patch" 2>/dev/null)" != "$fingerprint" ]; then
    rm -rf "$source" "$source.partial"
    cp -R "$scintilla/." "$source.partial"
    chmod -R u+w "$source.partial"
    patch -s -p1 -d "$source.partial" < Native/EditView.patch
    echo "$fingerprint" > "$source.partial/.patch"
    mv "$source.partial" "$source"
fi
if [ "$(cat "$out/.patch" 2>/dev/null)" != "$fingerprint" ]; then
    rm -rf "$out"; mkdir -p "$out"
    echo "$fingerprint" > "$out/.patch"
fi

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
