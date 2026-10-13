#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
version=131
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64) platform=arm64-macos; checksum=e441b48dc22163d209b4f05e44dc7210909b01237642b6c9ae48fd710a3ef83e;;
  Darwin-x86_64) platform=x86_64-macos; checksum=d209fadd8a894bdaf3bd3612a23c32a0af184d2f4a979b8c789e6e4f6a4de883;;
  Linux-x86_64) platform=x86_64-linux; checksum=b5bf1f0eaf17c63ee588ff7a5954dc8f6ce2c26989051c66f24dfe9ece3e46db;;
  Linux-aarch64) platform=aarch64-linux; checksum=ba991f677edd9a21d2bc96c0144bc8ac5b112d4d98a3eb266e075e22e557df2a;;
  *) echo 'Unsupported Binaryen build host.' >&2; exit 1;;
esac
cache=.tools/wasm-native/binaryen
[ ! -x "$cache/bin/wasm-opt" ] || exit 0
mkdir -p "$cache.partial"
curl -fL --retry 3 "https://github.com/WebAssembly/binaryen/releases/download/version_$version/binaryen-version_$version-$platform.tar.gz" -o "$cache.partial/archive.tar.gz"
printf '%s  %s\n' "$checksum" "$cache.partial/archive.tar.gz" | shasum -a 256 -c - >/dev/null
mkdir -p "$cache.partial/extracted"
tar -xzf "$cache.partial/archive.tar.gz" -C "$cache.partial/extracted" --strip-components=1
mv "$cache.partial/extracted" "$cache"
