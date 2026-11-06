#!/bin/sh
# Smoke test of the headless image: starts it on a small fixture workspace, sends initialize, notifications/initialized and
# tools/list as JSON lines on stdin, and checks that stdout holds exactly the two replies, as valid JSON-RPC, with the expected tools.
# Needs docker and jq. Usage: sh scripts/container-smoke.sh <image>
set -eu
image=${1:?usage: container-smoke.sh <image>}
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
# The container user is uid 1000, so the fixture must be readable by anyone.
mkdir "$tmp/workspace"
printf 'hello\n' > "$tmp/workspace/README.md"
chmod -R a+rwX "$tmp/workspace"

printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  | docker run -i --rm --init -v "$tmp/workspace:/workspace" "$image" > "$tmp/out" 2> "$tmp/err" \
  || { echo "container exited with $?"; cat "$tmp/err"; exit 1; }

fail() { echo "smoke test failed: $1"; echo "--- stdout"; cat "$tmp/out"; echo "--- stderr"; cat "$tmp/err"; exit 1; }

[ "$(wc -l < "$tmp/out" | tr -d ' ')" = 2 ] || fail "stdout must hold exactly two lines"
while IFS= read -r line; do
  printf '%s' "$line" | jq -e '.jsonrpc == "2.0" and has("id") and has("result")' > /dev/null || fail "not a JSON-RPC result: $line"
done < "$tmp/out"
jq -e -s 'map(select(.id == 1))[0].result.serverInfo.name == "sharprail"' "$tmp/out" > /dev/null || fail "initialize does not name the server"
jq -e -s 'map(select(.id == 1))[0].result.instructions | type == "string" and length > 0' "$tmp/out" > /dev/null || fail "initialize has no instructions"
names=$(jq -r -s 'map(select(.id == 2))[0].result.tools[].name' "$tmp/out") || fail "tools/list has no tools"
for want in lsp_navigate lsp_diagnostics lsp_status blueprint_check; do
  printf '%s\n' "$names" | grep -qx "$want" || fail "tools/list lacks $want"
done
printf '%s\n' "$names" | grep -q '^spec_' || fail "tools/list has no spec_ tools"
echo "smoke test passed: $(printf '%s\n' "$names" | wc -l | tr -d ' ') tools"
