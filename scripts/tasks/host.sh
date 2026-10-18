#!/bin/sh
# `scripts/mise run host`: launches only the remote host; scan the terminal QR code with the Android client.
# SHARPRAIL_CONNECT_HOST overrides the advertised hostname/IP (without a port).
set -eu
cd "$(dirname "$0")/../.."
export DOTNET_ROOT="$PWD/.tools/dotnet"
export SHARPRAIL_ROOT="${SHARPRAIL_ROOT:-$PWD}"
export SHARPRAIL_BIND=0.0.0.0
export SHARPRAIL_PORT="${SHARPRAIL_PORT:-54123}"
export SHARPRAIL_TOKEN="${SHARPRAIL_TOKEN:-$(uuidgen)$(uuidgen)}"
configuration="${SHARPRAIL_CONFIGURATION:-Release}"
.tools/dotnet/dotnet build src/SharpRail.Host.Remote -c "$configuration"
.tools/dotnet/dotnet run --file scripts/host-link.cs
exec .tools/dotnet/dotnet "src/SharpRail.Host.Remote/bin/$configuration/net10.0/SharpRail.Host.Remote.dll" "$@"
