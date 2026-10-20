#!/bin/sh
# Builds the WASM static libraries (libghostty-vt and the editor bridge). The browser project's BuildBrowserNative target
# decides when it runs: when a source, this script or the lockfile is newer than the libraries.
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

vt_commit="${GHOSTTY_VT_COMMIT:?Run through scripts/mise x -- (see mise.toml).}"
zig="$root/.tools/zig-$GHOSTTY_VT_ZIG"
[ -x "$zig/zig" ] || { echo "No Zig $GHOSTTY_VT_ZIG in .tools; run scripts/mise run bootstrap." >&2; exit 1; }
patch_hash="$(shasum -a 256 src/Ghostty.Avalonia/Native/Wasm.patch | cut -c1-16)"
source="$cache/ghostty-$vt_commit-$patch_hash"
if [ ! -d "$source" ]; then
    sh scripts/fetch-git.sh "$source.partial" https://github.com/ghostty-org/ghostty.git "$vt_commit"
    patch -s -p1 -d "$source.partial" < src/Ghostty.Avalonia/Native/Wasm.patch
    mv "$source.partial" "$source"
fi
[ "$(git -C "$source" rev-parse HEAD)" = "$vt_commit" ] || { echo 'Unexpected Ghostty source revision.' >&2; exit 1; }
sh scripts/zig-deps.sh tgz
(cd "$source" && "$zig/zig" build --cache-dir "$cache/zig-cache" --prefix "$cache/ghostty" -Demit-lib-vt -Dtarget=wasm32-freestanding -Dcpu=mvp+simd128+mutable_globals+sign_ext+nontrapping_fptoint+atomics+bulk_memory -Doptimize=ReleaseFast -Dstrip=true)
emcc -pthread -O2 -DGHOSTTY_STATIC -I "$source/include" -c src/Ghostty.Avalonia/Native/GhosttyVt.c -o "$cache/GhosttyVt.o"
cp "$cache/ghostty/lib/libghostty-vt.a" "$cache/GhosttyAvaloniaVt.a"
emar r "$cache/GhosttyAvaloniaVt.a" "$cache/GhosttyVt.o"

scintilla="$root/.tools/scintilla"
bidi="$root/.tools/sheenbidi"
patched="$cache/scintilla-patched"
patch_hash="$(shasum -a 256 src/SharpRail.Scintilla/Native/EditView.patch)"
if [ "$(cat "$patched/.patch" 2>/dev/null)" != "$patch_hash" ]; then
    # Each patch revision has its own source directory; existing build inputs stay intact.
    patched="$cache/scintilla-$(printf '%s' "$patch_hash" | cut -c1-16)"
    if [ ! -d "$patched" ]; then
        cp -R "$scintilla/." "$patched"
        chmod -R u+w "$patched"
        patch -s -p1 -d "$patched" < src/SharpRail.Scintilla/Native/EditView.patch
        printf '%s\n' "$patch_hash" > "$patched/.patch"
    fi
fi
mkdir -p "$cache/editor"
emcc -pthread -O2 -DSB_CONFIG_UNITY -I "$bidi/Headers" -I "$bidi/Source" -c "$bidi/Source/SheenBidi.c" -o "$cache/editor/SheenBidi.o"
for file in "$patched"/src/*.cxx "$root"/src/SharpRail.Scintilla/Native/*.cxx; do
    case "$file" in */AutoComplete.cxx|*/CallTip.cxx|*/ScintillaBase.cxx) continue;; esac
    name="$(basename "$file" .cxx)"
    case "$file" in */Native/*) name="Bridge$name";; esac
    emcc -pthread -DPTRDIFF_DOESNT_ALIAS_INT -x c++ -std=c++17 -O2 -DNDEBUG -fwasm-exceptions -I "$patched/include" -I "$patched/src" -I "$bidi/Headers" -c "$file" -o "$cache/editor/$name.o"
done
emar rcs "$cache/SharpRail.Scintilla.a" "$cache/editor"/*.o
