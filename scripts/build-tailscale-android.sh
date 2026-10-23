#!/bin/sh
# Build the app-private userspace Tailscale node; no system Go installation is used.
set -eu
cd "$(dirname "$0")/.."
root="$PWD"
abi="${1:-arm64-v8a}"
case "$abi" in
 arm64-v8a) goarch=arm64; compiler=aarch64-linux-android24-clang ;;
 x86_64) goarch=amd64; compiler=x86_64-linux-android24-clang ;;
 *) echo "Unsupported Android ABI: $abi" >&2; exit 1 ;;
esac
# Output: this checkout's src/SharpRail.Android/obj/tailscale/<abi>, which the Android project packages. The Go toolchain,
# build and module caches are the machine-wide ones scripts/tailscale-go.sh provisions; Go locks them itself.
out="$root/src/SharpRail.Android/obj/tailscale/$abi"
goroot="$(sh scripts/tailscale-go.sh)"
cache="$(dirname "$goroot")"
mkdir -p "$out"
lists="$(mktemp -d)"
trap 'rm -rf "$lists"' EXIT
ndk="${ANDROID_NDK_HOME:-$("$root/scripts/tool" android-ndk)}"
[ -d "$ndk/toolchains" ] || { echo "No Android NDK at $ndk." >&2; exit 1; }
case "$(uname -s)" in Darwin) ndk_host=darwin-x86_64 ;; Linux) ndk_host=linux-x86_64 ;; esac
export GOOS=android GOARCH="$goarch" CGO_ENABLED=1 GOTOOLCHAIN=local
export GOCACHE="$cache/build-cache" GOMODCACHE="$cache/modules"
export CC="$ndk/toolchains/llvm/prebuilt/$ndk_host/bin/$compiler"
cd src/SharpRail.Android/Tailscale.Native
# Library builds would report 1.x.y-devDATE-g<hash>-dirty to the admin console; stamp the pinned module version instead.
tsversion="$(sed -n 's/^require tailscale\.com v\([0-9][0-9.]*\)$/\1/p' go.mod)-sharprail"
"$goroot/bin/go" build -mod=readonly -trimpath -buildmode=c-shared -ldflags="-s -w -buildid= -X tailscale.com/version.shortStamp=$tsversion -X tailscale.com/version.longStamp=$tsversion -extldflags=-Wl,-z,max-page-size=16384" -o "$out/libSharpRailTailscale.so" .
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
copy_license "$goroot/LICENSE" "$out/GO-LICENSE"
"$goroot/bin/go" list -m -f '{{if .Dir}}{{.Path}}|{{.Dir}}{{end}}' all > "$lists/license-modules.list"
while IFS='|' read -r module directory; do
 [ -n "$directory" ] || continue
 destination="$out/licenses/$module"
 mkdir -p "$destination"
 for license in "$directory"/LICENSE* "$directory"/COPYING* "$directory"/NOTICE*; do
  if [ -f "$license" ]; then
   copy_license "$license" "$destination/$(basename "$license")"
  fi
 done
done < "$lists/license-modules.list"
# A constant-name bundle avoids Android asset wildcard metadata differences.
# License paths identify modules; bytewise sorting keeps the bundle deterministic.
find "$out/licenses" -type f | LC_ALL=C sort > "$lists/license-files.list"
: > "$out/THIRD-PARTY-NOTICES.txt"
while IFS= read -r license; do
 printf '\n===== %s =====\n\n' "${license#"$out/licenses/"}" >> "$out/THIRD-PARTY-NOTICES.txt"
 cat "$license" >> "$out/THIRD-PARTY-NOTICES.txt"
 printf '\n' >> "$out/THIRD-PARTY-NOTICES.txt"
done < "$lists/license-files.list"
printf '%s\n' "$out/libSharpRailTailscale.so"
