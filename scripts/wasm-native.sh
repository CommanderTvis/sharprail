#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
root="$PWD"
cache="$root/.tools/wasm-native"
mkdir -p "$cache"
case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) runtime=osx-arm64; zig_host=aarch64-macos;;
    Darwin-x86_64) runtime=osx-x64; zig_host=x86_64-macos;;
    Linux-x86_64) runtime=linux-x64; zig_host=x86_64-linux;;
    Linux-aarch64) runtime=linux-arm64; zig_host=aarch64-linux;;
    *) echo 'Unsupported WASM build host.' >&2; exit 1;;
esac
packs="$root/.tools/dotnet/packs"
for sdk in "$packs"/Microsoft.NET.Runtime.Emscripten.*.Sdk."$runtime"/*/tools; do [ ! -d "$sdk" ] || break; done
for python in "$packs"/Microsoft.NET.Runtime.Emscripten.*.Python."$runtime"/*/tools/bin/python3; do [ ! -f "$python" ] || break; done
for node in "$packs"/Microsoft.NET.Runtime.Emscripten.*.Node."$runtime"/*/tools/bin/node; do [ ! -f "$node" ] || break; done
for emcache in "$packs"/Microsoft.NET.Runtime.Emscripten.*.Cache."$runtime"/*/tools/emscripten/cache; do [ ! -d "$emcache" ] || break; done
[ -d "$sdk" ] && [ -f "$python" ] || { echo 'Install the checkout-local wasm-tools workload first.' >&2; exit 1; }
export DOTNET_EMSCRIPTEN_LLVM_ROOT="$sdk/bin"
export DOTNET_EMSCRIPTEN_BINARYEN_ROOT="$sdk"
export DOTNET_EMSCRIPTEN_NODE_JS="$node"
export EM_CACHE="$emcache"
export FROZEN_CACHE=False
emcc() { uv run --no-project --python "$python" "$sdk/emscripten/emcc.py" "$@"; }
emar() { uv run --no-project --python "$python" "$sdk/emscripten/emar.py" "$@"; }

fetch() {
    archive="$cache/$2"
    if [ ! -f "$archive" ]; then curl -fL --retry 3 "$1" -o "$archive.partial"; mv "$archive.partial" "$archive"; fi
    printf '%s  %s\n' "$3" "$archive" | shasum -a 256 -c - >/dev/null
    if [ ! -d "$cache/$4" ]; then
        mkdir -p "$cache/$4.partial"
        tar -xzf "$archive" -C "$cache/$4.partial" --strip-components=1
        mv "$cache/$4.partial" "$cache/$4"
    fi
}

vt_commit="$(sed -n 's/^vt_commit=//p' src/Ghostty.Avalonia/build-native.sh)"
vt_zig="$(sed -n 's/^vt_zig=//p' src/Ghostty.Avalonia/build-native.sh)"
zig="$root/.tools/ghostty-avalonia/zig-$zig_host-$vt_zig"
if [ ! -f "$zig/zig" ]; then
    mkdir -p "$cache/zig.partial"
    curl -fL --retry 3 "https://ziglang.org/download/$vt_zig/zig-$zig_host-$vt_zig.tar.xz" -o "$cache/zig.partial/zig.tar.xz"
    tar -xf "$cache/zig.partial/zig.tar.xz" -C "$cache/zig.partial"
    mkdir -p "$(dirname "$zig")"
    mv "$cache/zig.partial/zig-$zig_host-$vt_zig" "$zig"
fi
patch_hash="$(shasum -a 256 src/Ghostty.Avalonia/Native/Wasm.patch | cut -c1-16)"
source="$cache/ghostty-$vt_commit-$patch_hash"
if [ ! -d "$source" ]; then
    git init -q "$source.partial"
    git -C "$source.partial" fetch -q --depth 1 https://github.com/ghostty-org/ghostty.git "$vt_commit"
    git -C "$source.partial" checkout -q FETCH_HEAD
    patch -s -p1 -d "$source.partial" < src/Ghostty.Avalonia/Native/Wasm.patch
    mv "$source.partial" "$source"
fi
[ "$(git -C "$source" rev-parse HEAD)" = "$vt_commit" ] || { echo 'Unexpected Ghostty source revision.' >&2; exit 1; }
fingerprint="$(cat src/Ghostty.Avalonia/Native/GhosttyVt.c src/Ghostty.Avalonia/Native/Wasm.patch src/SharpRail.Scintilla/Native/* scripts/wasm-native.sh | shasum -a 256) $sdk $vt_commit"
if [ "$(cat "$cache/fingerprint" 2>/dev/null)" = "$fingerprint" ] && [ -f "$cache/GhosttyAvaloniaVt.a" ] && [ -f "$cache/SharpRail.Scintilla.a" ]; then exit 0; fi
(cd "$source" && "$zig/zig" build --cache-dir "$cache/zig-cache" --prefix "$cache/ghostty" -Demit-lib-vt -Dtarget=wasm32-freestanding -Dcpu=mvp+simd128+mutable_globals+sign_ext+nontrapping_fptoint+atomics+bulk_memory -Doptimize=ReleaseFast -Dstrip=true)
emcc -pthread -O2 -DGHOSTTY_STATIC -I "$source/include" -c src/Ghostty.Avalonia/Native/GhosttyVt.c -o "$cache/GhosttyVt.o"
cp "$cache/ghostty/lib/libghostty-vt.a" "$cache/GhosttyAvaloniaVt.a"
emar r "$cache/GhosttyAvaloniaVt.a" "$cache/GhosttyVt.o"

fetch https://www.scintilla.org/scintilla567.tgz scintilla567.tgz \
    19e4476706750056f86ca70974810e4a5b08e73477e13f7d9fae78243606f6a7 scintilla-5.6.7
fetch https://github.com/Tehreer/SheenBidi/archive/refs/tags/v3.0.0.tar.gz sheenbidi-3.0.0.tgz \
    86c56014034739ba39a24c23eb00323b0bf6f737354f665786015fca842af786 sheenbidi-3.0.0
patched="$cache/scintilla-patched"
patch_hash="$(shasum -a 256 src/SharpRail.Scintilla/Native/EditView.patch)"
if [ "$(cat "$patched/.patch" 2>/dev/null)" != "$patch_hash" ]; then
    # Each patch revision has its own source directory; existing build inputs stay intact.
    patched="$cache/scintilla-$(printf '%s' "$patch_hash" | cut -c1-16)"
    if [ ! -d "$patched" ]; then
        cp -R "$cache/scintilla-5.6.7" "$patched"
        patch -s -p1 -d "$patched" < src/SharpRail.Scintilla/Native/EditView.patch
        printf '%s\n' "$patch_hash" > "$patched/.patch"
    fi
fi
bidi="$cache/sheenbidi-3.0.0"
mkdir -p "$cache/editor"
emcc -pthread -O2 -DSB_CONFIG_UNITY -I "$bidi/Headers" -I "$bidi/Source" -c "$bidi/Source/SheenBidi.c" -o "$cache/editor/SheenBidi.o"
for file in "$patched"/src/*.cxx "$root"/src/SharpRail.Scintilla/Native/*.cxx; do
    case "$file" in */AutoComplete.cxx|*/CallTip.cxx|*/ScintillaBase.cxx) continue;; esac
    name="$(basename "$file" .cxx)"
    case "$file" in */Native/*) name="Bridge$name";; esac
    emcc -pthread -DPTRDIFF_DOESNT_ALIAS_INT -x c++ -std=c++17 -O2 -DNDEBUG -fwasm-exceptions -I "$patched/include" -I "$patched/src" -I "$bidi/Headers" -c "$file" -o "$cache/editor/$name.o"
done
emar rcs "$cache/SharpRail.Scintilla.a" "$cache/editor"/*.o
printf '%s\n' "$fingerprint" > "$cache/fingerprint"
