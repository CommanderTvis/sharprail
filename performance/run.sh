#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
dotnet_cmd="${DOTNET:-.tools/dotnet/dotnet}"
if [ -z "${DOTNET:-}" ] && [ ! -x "$dotnet_cmd" ]; then
  echo 'Bootstrapping the checkout-local .NET SDK for profiling.' >&2
  sh scripts/bootstrap.sh >&2
fi
exec "$dotnet_cmd" run --project performance/SharpRail.Performance -c Release -- "$@"
