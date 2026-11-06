#!/bin/sh
# NativeAOT's C compiler and linker for the Linux packages (NativeAot.props sets it for linux-x64 and linux-arm64): Zig's
# clang and lld, targeting glibc 2.31, the baseline the AppImages promise, so the binary needs no newer glibc than the
# host it runs on, whatever glibc the build host has. It works the same from a Linux or a macOS host. MSBuild calls it
# with clang's arguments; the target and the linker are Zig's, so --target, -fuse-ld and -gz are replaced or dropped, and
# -Wl,-pie, which Zig refuses, is left to -pie.
set -eu
root="$(cd "$(dirname "$0")/.." && pwd -P)"
# The Zig version is mise.toml's (GHOSTTY_VT_ZIG, the one the Linux natives build with); outside mise, rerun under it.
[ -n "${GHOSTTY_VT_ZIG:-}" ] || exec sh "$root/scripts/mise" x -- sh "$0" "$@"
zig="$("$root/scripts/tool" "zig-$GHOSTTY_VT_ZIG")/zig"
arch=""
for arg; do
  case "$arg" in
    --target=x86_64-*) arch=x86_64 ;;
    --target=aarch64-*) arch=aarch64 ;;
    # MSBuild reads the linker's version to decide how to keep the runtime's module section.
    -Wl,--version) exec "$zig" ld.lld --version ;;
  esac
done
# Without --target the build is native: the host's architecture is the target's.
if [ -z "$arch" ]; then
  case "$(uname -m)" in
    x86_64|amd64) arch=x86_64 ;;
    aarch64|arm64) arch=aarch64 ;;
    *) echo "aot-linux-cc.sh: unsupported architecture $(uname -m)" >&2; exit 1 ;;
  esac
fi
for arg; do
  shift
  case "$arg" in
    --target=*|-fuse-ld=*|-gz=*|-Wl,-pie) ;;
    *) set -- "$@" "$arg" ;;
  esac
done
exec "$zig" cc -target "$arch-linux-gnu.2.31" "$@"
