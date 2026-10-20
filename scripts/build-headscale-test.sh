#!/bin/sh
# Provision what `--headscale` needs under .tools/headscale-test: the checksum-pinned Headscale
# release binary, the userspace tailnet node used as the host side (cmd/tsforward) and the desktop
# build of the embedded Tailscale library the client side loads. No account, root or system Go.
set -eu
cd "$(dirname "$0")/.."
root="$PWD"
version=0.29.5
case "$(uname -s)-$(uname -m)" in
 Darwin-arm64) platform=darwin_arm64; checksum=e4f5ce794b7691a92d04a3dd9fb94ba259224021164c24bf01ad7dc83e8d76fe ;;
 Darwin-x86_64) platform=darwin_amd64; checksum=6ddbcc54859cabfe13ac4ed6c481526251247165088d52622bc7bebc6240b8c1 ;;
 Linux-aarch64) platform=linux_arm64; checksum=3afddd207b89ac22cfad4235cc163c6e538cc59ada139e7c0bf47640c5d9e945 ;;
 Linux-x86_64) platform=linux_amd64; checksum=e979d27fe17d924d33bb6ab7504f6338d0635989a0cfdcfbc0ceb637dbcbe980 ;;
 *) echo 'The Headscale check requires macOS or Linux, arm64 or x64.' >&2; exit 1 ;;
esac
out="$root/.tools/headscale-test"
mkdir -p "$out"
if [ "$(cat "$out/headscale.version" 2>/dev/null || true)" != "$version-$platform" ]; then
 curl -fL --retry 3 "https://github.com/juanfont/headscale/releases/download/v$version/headscale_${version}_$platform" -o "$out/headscale.download"
 printf '%s  %s\n' "$checksum" "$out/headscale.download" | shasum -a 256 -c - >/dev/null
 chmod +x "$out/headscale.download"
 mv "$out/headscale.download" "$out/headscale"
 printf '%s\n' "$version-$platform" > "$out/headscale.version"
fi
sh scripts/tailscale-go.sh
cache="$root/.tools/tailscale-go"
case "$(uname -s)" in Darwin) ext=dylib ;; *) ext=so ;; esac
export CGO_ENABLED=1 GOTOOLCHAIN=local GOCACHE="$cache/build-cache" GOMODCACHE="$cache/modules"
cd src/SharpRail.Android/Tailscale.Native
"$cache/go/bin/go" build -mod=readonly -trimpath -o "$out/tsforward" ./cmd/tsforward
"$cache/go/bin/go" build -mod=readonly -trimpath -buildmode=c-shared -o "$out/libSharpRailTailscale.$ext" .
rm -f "$out/libSharpRailTailscale.h"
printf '%s\n' "$out"
