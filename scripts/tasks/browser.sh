#!/bin/sh
set -eu
# `scripts/mise run browser [build|run|publish|check]`.
cd "$(dirname "$0")/../.."
root="$PWD"
dotnet=dotnet
sh scripts/tasks/workload.sh wasm-experimental Microsoft.NETCore.App.Runtime.Mono.multithread.browser-wasm --skip-manifest-update
# The Playwright tools (node, pnpm) are the browser mise environment's pins, not on PATH on a plain runner.
mise_browser() { MISE_ENV=browser sh "$root/scripts/mise" x -- "$@"; }
configuration="${SHARPRAIL_CONFIGURATION:-Release}"
case "${1:-build}" in
    build) "$dotnet" build src/SharpRail.Browser -c "$configuration" -m:2;;
    publish) "$dotnet" publish src/SharpRail.Browser -c "$configuration" -m:2 -p:WasmAppDir="$root/artifacts/browser";;
    check)
        node tests/SharpRail.Browser.Checks/compression.mjs
        node tests/SharpRail.Browser.Checks/tailscale.mjs
        "$dotnet" build tests/SharpRail.Checks -c "$configuration" -m:2
        "$dotnet" build src/SharpRail.Browser -c "$configuration" -m:2 -p:BrowserChecks=true
        # pnpm is the mise pin (not on PATH on a plain runner); Linux also needs Chromium's system libraries (apt, via sudo).
        deps=""; [ "$(uname -s)" != Linux ] || deps="--with-deps"
        (cd src/SharpRail.Browser && mise_browser pnpm exec playwright install chromium $deps)
        mise_browser node tests/SharpRail.Browser.Checks/selection.mjs
        mise_browser node tests/SharpRail.Browser.Checks/check.mjs
        mise_browser node tests/SharpRail.Browser.Checks/local-approval.mjs
        mise_browser node tests/SharpRail.Browser.Checks/restty.mjs --hover
        mise_browser node tests/SharpRail.Browser.Checks/restty.mjs --stream
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
