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
# Run from inside a Claude Code session, the app must not carry that session's identity into its terminals.
unset CLAUDECODE CLAUDE_CODE_CHILD_SESSION CLAUDE_CODE_ENTRYPOINT CLAUDE_CODE_EXECPATH CLAUDE_CODE_SESSION_ID \
  CLAUDE_CODE_BRIDGE_SESSION_ID CLAUDE_CODE_MESSAGING_SOCKET CLAUDE_CODE_MESSAGING_TOKEN CLAUDE_CODE_SESSION_ATTENDED \
  CLAUDE_CODE_SSE_PORT CLAUDE_PID CLAUDE_EFFORT
exec .tools/dotnet/dotnet run --project src/SharpRail.UI -c "${SHARPRAIL_CONFIGURATION:-Debug}" -- "$@"
