#!/bin/bash
# Test of the config-writing functions of headless-cloud-setup.sh against a temporary HOME: no network, no install, never the real ~/.claude.
# Needs bash, jq and cksum. Run by the headless check (SharpRail.Checks --headless) and by hand: scripts/headless-cloud-setup.test.sh
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(mktemp -d)
trap 'rm -rf "$root"' EXIT
export HOME=$root/home
unset CLAUDE_CONFIG_DIR
mkdir -p "$HOME" "$root/stub"
# A claude that fails, so the direct JSON merge is what runs (the CLI path is not testable without the real CLI, which would write its own state).
printf '#!/bin/sh\nexit 1\n' > "$root/stub/claude" && chmod +x "$root/stub/claude"
export PATH=$root/stub:$PATH

check() { if ! "$@"; then echo "FAIL: $*" >&2; exit 1; fi; }
sum() { cksum "$HOME/.claude.json" "$HOME/.claude/settings.json" "$HOME/.claude/CLAUDE.md" | tr '\n' ' '; }

echo '{"userID":"u1","mcpServers":{"other":{"type":"stdio","command":"/bin/other"}}}' > "$HOME/.claude.json"
mkdir -p "$HOME/.claude"
echo '{"theme":"dark","env":{"FOO":"1"}}' > "$HOME/.claude/settings.json"
printf 'my own notes\n' > "$HOME/.claude/CLAUDE.md"

export SHARPRAIL_SETUP_SOURCE_ONLY=1
# shellcheck source=headless-cloud-setup.sh
source "$here/headless-cloud-setup.sh"
unset SHARPRAIL_SETUP_SOURCE_ONLY

run() { register_mcp /opt/sharprail/sharprail-headless > /dev/null; configure_timeouts > /dev/null; write_notes '  - C# (Roslyn) (csharp): .cs'; }
run

j=$HOME/.claude.json
check jq -e '.userID == "u1" and .mcpServers.other.command == "/bin/other"' "$j" > /dev/null
check jq -e '.mcpServers.sharprail == {type: "stdio", command: "/opt/sharprail/sharprail-headless", args: ["mcp"], env: {SHARPRAIL_TOOLCHAINS: "managed"}, alwaysLoad: true}' "$j" > /dev/null
check jq -e '.theme == "dark" and .env.FOO == "1" and .env.MCP_TIMEOUT == "300000"' "$HOME/.claude/settings.json" > /dev/null
check grep -q '^my own notes$' "$HOME/.claude/CLAUDE.md"
check test "$(grep -c 'sharprail-headless:begin' "$HOME/.claude/CLAUDE.md")" = 1
check grep -q 'mcp__sharprail__' "$HOME/.claude/CLAUDE.md"

# A second run changes nothing.
first=$(sum)
run
check test "$(sum)" = "$first"

# A moved binary updates the entry; a timeout the user set stays; invalid JSON is never touched.
register_mcp /elsewhere/sharprail-headless > /dev/null
check jq -e '.mcpServers.sharprail.command == "/elsewhere/sharprail-headless" and (.mcpServers | length) == 2' "$j" > /dev/null
echo '{"env":{"MCP_TIMEOUT":"5000"}}' > "$HOME/.claude/settings.json"
configure_timeouts > /dev/null
check jq -e '.env.MCP_TIMEOUT == "5000"' "$HOME/.claude/settings.json" > /dev/null
echo '{broken' > "$HOME/.claude/settings.json"
configure_timeouts > /dev/null 2>&1
check test "$(cat "$HOME/.claude/settings.json")" = '{broken'
echo '[1]' > "$j"
if register_mcp /x > /dev/null 2>&1; then echo "FAIL: register_mcp wrote into a file that is not an object" >&2; exit 1; fi
check test "$(cat "$j")" = '[1]'

# CLAUDE_CONFIG_DIR moves all of it.
export CLAUDE_CONFIG_DIR=$root/cfg
claude_json=$CLAUDE_CONFIG_DIR/.claude.json config_dir=$CLAUDE_CONFIG_DIR
run
check jq -e '.mcpServers.sharprail.alwaysLoad == true' "$CLAUDE_CONFIG_DIR/.claude.json" > /dev/null
check jq -e '.env.MCP_TIMEOUT == "300000"' "$CLAUDE_CONFIG_DIR/settings.json" > /dev/null
echo "PASS headless cloud setup: the MCP entry (alwaysLoad), MCP_TIMEOUT and the notes are written to a temporary HOME, existing content is kept, a second run changes nothing, invalid JSON is left alone"
