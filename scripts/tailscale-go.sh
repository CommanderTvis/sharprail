#!/bin/sh
# Install the checksum-pinned Go toolchain shared by the Tailscale builds into the machine-wide cache
# ($SHARPRAIL_CACHE_DIR/tailscale-go/go<version>-<platform>, one directory per pin so checkouts pinning different versions
# never replace each other's) and print its GOROOT. Go's build and module caches beside it are safe for concurrent use.
set -eu
cd "$(dirname "$0")/.."
root="$PWD"
version=1.27.2
case "$(uname -s)-$(uname -m)" in
 Darwin-arm64) platform=darwin-arm64; checksum=76812b213b1b2302c978d28fa52fa92d541704b9e7d9d5db8002c50e4018c4c5 ;;
 Darwin-x86_64) platform=darwin-amd64; checksum=587b59182488b23aa6e5fc25110405a3e0e5b38ed2f5b2f46ed13c32aee356fe ;;
 Linux-aarch64) platform=linux-arm64; checksum=94f3e30b8e374bc285e7dadc11e0865726b9bc6e85b841ccceaabc0214c6b7c8 ;;
 Linux-x86_64) platform=linux-amd64; checksum=ecbadb99091a3f46e31f5f934b068b1864eafa7995211b39eaddf76996045fe5 ;;
 *) echo 'Tailscale builds require macOS or Linux, arm64 or x64.' >&2; exit 1 ;;
esac
. "$root/scripts/lock.sh"
. "$root/scripts/native-cache.sh"
cache="$(sharprail_cache_dir)/tailscale-go"
goroot="$cache/go$version-$platform"
if [ ! -x "$goroot/bin/go" ]; then
 mkdir -p "$cache"
 acquire_lock "$cache/install.lock"
 if [ ! -x "$goroot/bin/go" ]; then
  rm -rf "$goroot.partial"
  mkdir -p "$goroot.partial"
  curl -fL --retry 3 "https://go.dev/dl/go$version.$platform.tar.gz" -o "$goroot.partial/go.tar.gz" >&2
  printf '%s  %s\n' "$checksum" "$goroot.partial/go.tar.gz" | shasum -a 256 -c - >/dev/null
  tar -xzf "$goroot.partial/go.tar.gz" -C "$goroot.partial"
  mv "$goroot.partial/go" "$goroot"
  rm -rf "$goroot.partial"
 fi
fi
printf '%s\n' "$goroot"
