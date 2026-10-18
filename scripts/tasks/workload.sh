#!/bin/sh
# Installs a .NET workload into the SDK once: workload.sh <workload> <pack directory that marks it installed> [dotnet options].
# The SDK is shared by every checkout (see mise.toml), so installs are serialised with a lock directory beside it.
set -eu
cd "$(dirname "$0")/../.."
workload="$1"; marker="$2"; shift 2
root="$(cd .tools/dotnet && pwd -P)"
# Workload packs land in packs/ (or, for android, are listed by `workload list`); either proves it is installed.
installed() { [ -d "$root/packs/$marker" ] || .tools/dotnet/dotnet workload list 2>/dev/null | grep -q "^$workload"; }
installed && exit 0
while ! mkdir "$root/.workload.lock" 2>/dev/null; do sleep 1; done
trap 'rmdir "$root/.workload.lock"' EXIT HUP INT TERM
installed || .tools/dotnet/dotnet workload install "$workload" "$@"
