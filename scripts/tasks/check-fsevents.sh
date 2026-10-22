#!/bin/sh
# `scripts/mise run check:fsevents`. Test-process-only native FSEvents accounting; never inject into a user application.
set -eu
cd "$(dirname "$0")/../.."
[ "$(uname -s)" = Darwin ] || { echo 'FSEvents checks require macOS.' >&2; exit 1; }
case "${1:-}" in ''|--no-build) ;; *) echo 'Usage: scripts/mise run check:fsevents [-- --no-build]' >&2; exit 1 ;; esac
[ -z "${DYLD_INSERT_LIBRARIES:-}" ] || { echo 'Run without an existing DYLD_INSERT_LIBRARIES injection.' >&2; exit 1; }
root="$PWD"
if [ "${1:-}" != --no-build ]; then dotnet build SharpRail.slnx -c Release; fi
mkdir -p .bench/fsevents-probe
scratch="$(mktemp -d "$root/.bench/fsevents-probe/run.XXXXXX")"
trap 'rm -rf "$scratch"; rmdir "$root/.bench/fsevents-probe" 2>/dev/null || true' EXIT HUP INT TERM
probe="$scratch/libFSEventsProbe.dylib"
clang -dynamiclib -O2 -Wall -Wextra -Werror tests/SharpRail.Checks/Native/FSEventsProbe.c -framework CoreServices -o "$probe"
mkdir "$scratch/work"
cd "$scratch/work"
DYLD_INSERT_LIBRARIES="$probe" SHARPRAIL_FSEVENTS_PROBE="$probe" \
    dotnet "$root/tests/SharpRail.Checks/bin/Release/net10.0/SharpRail.Checks.dll" --native-fsevents
