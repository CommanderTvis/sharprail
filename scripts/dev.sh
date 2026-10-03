#!/bin/sh
# Builds SharpRail from source and runs it, like the reference's `bun run desktop:dev`.
# The workspace defaults to this checkout (SHARPRAIL_ROOT) and the profile to ~/.sharprail;
# set SHARPRAIL_PROFILE to try something without touching your own. Arguments go to the app.
set -eu
cd "$(dirname "$0")/.."
[ -x .tools/dotnet/dotnet ] || sh scripts/bootstrap.sh
# The app host finds the checkout's runtime, and terminal relays inherit it.
export DOTNET_ROOT="$PWD/.tools/dotnet"
export SHARPRAIL_ROOT="${SHARPRAIL_ROOT:-$PWD}"
exec .tools/dotnet/dotnet run --project src/SharpRail.UI -c "${SHARPRAIL_CONFIGURATION:-Debug}" -- "$@"
