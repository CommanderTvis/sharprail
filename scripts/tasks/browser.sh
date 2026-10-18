#!/bin/sh
set -eu
# `scripts/mise run browser [build|run|publish|check]`.
cd "$(dirname "$0")/../.."
root="$PWD"
dotnet="$root/.tools/dotnet/dotnet"
sh scripts/tasks/workload.sh wasm-experimental Microsoft.NETCore.App.Runtime.Mono.multithread.browser-wasm --skip-manifest-update
configuration="${SHARPRAIL_CONFIGURATION:-Release}"
case "${1:-build}" in
    build) "$dotnet" build src/SharpRail.Browser -c "$configuration" -m:2;;
    publish) "$dotnet" publish src/SharpRail.Browser -c "$configuration" -m:2 -p:WasmAppDir="$root/artifacts/browser";;
    check)
        node tests/SharpRail.Browser.Checks/compression.mjs
        "$dotnet" build tests/SharpRail.Checks -c "$configuration" -m:2
        "$dotnet" build src/SharpRail.Browser -c "$configuration" -m:2 -p:BrowserChecks=true
        export PLAYWRIGHT_BROWSERS_PATH="$root/.tools/playwright"
        (cd src/SharpRail.Browser && pnpm exec playwright install chromium)
        node tests/SharpRail.Browser.Checks/selection.mjs
        node tests/SharpRail.Browser.Checks/check.mjs
        node tests/SharpRail.Browser.Checks/local-approval.mjs
        node tests/SharpRail.Browser.Checks/restty.mjs --hover
        ;;
    run)
        : "${SHARPRAIL_TOKEN:?Set SHARPRAIL_TOKEN to the host session token.}"
        "$dotnet" build src/SharpRail.Browser -c "$configuration" -m:2
        "$dotnet" build src/SharpRail.Host.Remote -c "$configuration" -m:2
        export SHARPRAIL_PORT="${SHARPRAIL_PORT:-54123}"
        export SHARPRAIL_BROWSER_ROOT="$root/src/SharpRail.Browser/bin/$configuration/net10.0-browser/AppBundle"
        exec "$dotnet" run --no-build --project src/SharpRail.Host.Remote -c "$configuration"
        ;;
    *) echo 'Usage: scripts/mise run browser [build|run|publish|check]' >&2; exit 1;;
esac
