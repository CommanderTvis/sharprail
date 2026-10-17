#!/bin/sh
# Build the app-private userspace Tailscale node; no system Go installation is used.
set -eu
cd "$(dirname "$0")/.."
root="$PWD"
version=1.27.2
case "$(uname -s)-$(uname -m)" in
 Darwin-arm64) platform=darwin-arm64; checksum=76812b213b1b2302c978d28fa52fa92d541704b9e7d9d5db8002c50e4018c4c5 ;;
 Darwin-x86_64) platform=darwin-amd64; checksum=587b59182488b23aa6e5fc25110405a3e0e5b38ed2f5b2f46ed13c32aee356fe ;;
 Linux-aarch64) platform=linux-arm64; checksum=94f3e30b8e374bc285e7dadc11e0865726b9bc6e85b841ccceaabc0214c6b7c8 ;;
 Linux-x86_64) platform=linux-amd64; checksum=ecbadb99091a3f46e31f5f934b068b1864eafa7995211b39eaddf76996045fe5 ;;
 *) echo 'Tailscale Android builds require macOS or Linux, arm64 or x64.' >&2; exit 1 ;;
esac
cache="$root/.tools/tailscale-go"
abi="${1:-arm64-v8a}"
case "$abi" in
 arm64-v8a) goarch=arm64; compiler=aarch64-linux-android24-clang ;;
 x86_64) goarch=amd64; compiler=x86_64-linux-android24-clang ;;
 *) echo "Unsupported Android ABI: $abi" >&2; exit 1 ;;
esac
out="$root/.tools/tailscale-android/$abi"
mkdir -p "$cache" "$out"
while ! mkdir "$cache/build.lock" 2>/dev/null; do sleep 1; done
trap 'rmdir "$cache/build.lock"' EXIT HUP INT TERM
if [ ! -x "$cache/go/bin/go" ] || [ "$(cat "$cache/version" 2>/dev/null || true)" != "$version-$platform" ]; then
 curl -fL --retry 3 "https://go.dev/dl/go$version.$platform.tar.gz" -o "$cache/go.tar.gz"
 printf '%s  %s\n' "$checksum" "$cache/go.tar.gz" | shasum -a 256 -c - >/dev/null
 tar -xzf "$cache/go.tar.gz" -C "$cache"
 rm "$cache/go.tar.gz"
 printf '%s\n' "$version-$platform" > "$cache/version"
fi
ndk="${ANDROID_NDK_HOME:-$root/.tools/android-ndk}"
[ -d "$ndk/toolchains" ] || { echo 'Run scripts/mise run bootstrap:android first.' >&2; exit 1; }
case "$(uname -s)" in Darwin) ndk_host=darwin-x86_64 ;; Linux) ndk_host=linux-x86_64 ;; esac
export GOOS=android GOARCH="$goarch" CGO_ENABLED=1 GOTOOLCHAIN=local
export GOCACHE="$cache/build-cache" GOMODCACHE="$cache/modules"
export CC="$ndk/toolchains/llvm/prebuilt/$ndk_host/bin/$compiler"
cd src/SharpRail.Android/Tailscale.Native
"$cache/go/bin/go" build -mod=readonly -trimpath -buildmode=c-shared -ldflags='-s -w -buildid= -extldflags=-Wl,-z,max-page-size=16384' -o "$out/libSharpRailTailscale.so" .
# Module cache licenses are read-only. Keep staged copies writable so repeated
# builds replace them cleanly, and propagate all real copy/list failures.
copy_license() {
 source="$1"
 target="$2"
 if [ -f "$target" ]; then chmod u+w "$target"; fi
 cp "$source" "$target"
 chmod u+w "$target"
}
copy_license TAILSCALE-LICENSE "$out/TAILSCALE-LICENSE"
copy_license "$cache/go/LICENSE" "$out/GO-LICENSE"
"$cache/go/bin/go" list -m -f '{{if .Dir}}{{.Path}}|{{.Dir}}{{end}}' all > "$cache/license-modules.list"
while IFS='|' read -r module directory; do
 [ -n "$directory" ] || continue
 destination="$out/licenses/$module"
 mkdir -p "$destination"
 for license in "$directory"/LICENSE* "$directory"/COPYING* "$directory"/NOTICE*; do
  if [ -f "$license" ]; then
   copy_license "$license" "$destination/$(basename "$license")"
  fi
 done
done < "$cache/license-modules.list"
# A constant-name bundle avoids Android asset wildcard metadata differences.
# License paths identify modules; bytewise sorting keeps the bundle deterministic.
find "$out/licenses" -type f | LC_ALL=C sort > "$cache/license-files.list"
: > "$out/THIRD-PARTY-NOTICES.txt"
while IFS= read -r license; do
 printf '\n===== %s =====\n\n' "${license#"$out/licenses/"}" >> "$out/THIRD-PARTY-NOTICES.txt"
 cat "$license" >> "$out/THIRD-PARTY-NOTICES.txt"
 printf '\n' >> "$out/THIRD-PARTY-NOTICES.txt"
done < "$cache/license-files.list"
printf '%s\n' "$out/libSharpRailTailscale.so"
