#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
# Under `scripts/mise x --` (or `scripts/mise run perf`) the pinned SDK is the dotnet on PATH.
dotnet_cmd="${DOTNET:-dotnet}"
exec "$dotnet_cmd" run --project performance/SharpRail.Performance -c Release -- "$@"
