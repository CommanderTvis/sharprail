#!/bin/bash
# Test of headless-cloud-setup.sh against a temporary HOME and a local file:// "release": the config-writing functions, then a whole run (download, checksum,
# pin, install, second run, failures). No network, never the real ~/.claude or ~/.local. Needs bash, jq, tar, gzip and cksum. Run by the headless check (Tereditor.Checks --headless) and by hand: scripts/headless-cloud-setup.test.sh
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
root=$(mktemp -d)
trap 'rm -rf "$root"' EXIT
export HOME=$root/home
unset CLAUDE_CONFIG_DIR
mkdir -p "$HOME" "$root/stub"
# A claude that fails, so the direct JSON merge is what runs (the CLI path is not testable without the real CLI, which would write its own state).
printf '#!/bin/sh\nexit 1\n' > "$root/stub/claude" && chmod +x "$root/stub/claude"
# No package manager runs in the test.
printf '#!/bin/sh\nexit 0\n' > "$root/stub/apt-get" && chmod +x "$root/stub/apt-get"
export PATH=$root/stub:$PATH

check() { if ! "$@"; then echo "FAIL: $*" >&2; exit 1; fi; }
sum() { cksum "$HOME/.claude.json" "$HOME/.claude/settings.json" "$HOME/.claude/CLAUDE.md" | tr '\n' ' '; }

echo '{"userID":"u1","mcpServers":{"other":{"type":"stdio","command":"/bin/other"}}}' > "$HOME/.claude.json"
mkdir -p "$HOME/.claude"
echo '{"theme":"dark","env":{"FOO":"1"}}' > "$HOME/.claude/settings.json"
printf 'my own notes\n' > "$HOME/.claude/CLAUDE.md"

export TEREDITOR_SETUP_SOURCE_ONLY=1
# shellcheck source=headless-cloud-setup.sh
source "$here/headless-cloud-setup.sh"
unset TEREDITOR_SETUP_SOURCE_ONLY

run() { register_mcp /opt/tereditor/tereditor-headless > /dev/null; configure_timeouts > /dev/null; write_notes '  - C# (Roslyn) (csharp): .cs'; }
run

j=$HOME/.claude.json
check jq -e '.userID == "u1" and .mcpServers.other.command == "/bin/other"' "$j" > /dev/null
check jq -e '.mcpServers.tereditor == {type: "stdio", command: "/opt/tereditor/tereditor-headless", args: ["mcp"], env: {TEREDITOR_TOOLCHAINS: "managed"}, alwaysLoad: true}' "$j" > /dev/null
check jq -e '.theme == "dark" and .env.FOO == "1" and .env.MCP_TIMEOUT == "300000"' "$HOME/.claude/settings.json" > /dev/null
check grep -q '^my own notes$' "$HOME/.claude/CLAUDE.md"
check test "$(grep -c 'tereditor-headless:begin' "$HOME/.claude/CLAUDE.md")" = 1
check grep -q 'mcp__tereditor__' "$HOME/.claude/CLAUDE.md"

# A second run changes nothing.
first=$(sum)
run
check test "$(sum)" = "$first"

# A moved binary updates the entry; a timeout the user set stays; invalid JSON is never touched.
register_mcp /elsewhere/tereditor-headless > /dev/null
check jq -e '.mcpServers.tereditor.command == "/elsewhere/tereditor-headless" and (.mcpServers | length) == 2' "$j" > /dev/null
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
check jq -e '.mcpServers.tereditor.alwaysLoad == true' "$CLAUDE_CONFIG_DIR/.claude.json" > /dev/null
check jq -e '.env.MCP_TIMEOUT == "300000"' "$CLAUDE_CONFIG_DIR/settings.json" > /dev/null

# A whole run against a local release: an archive laid out like the nightly one (a tereditor-headless/ folder), named for this machine, and a SHA256SUMS.
unset CLAUDE_CONFIG_DIR
case "$(uname -m)" in x86_64 | amd64) arch=x64 ;; aarch64 | arm64) arch=arm64 ;; *) echo "SKIP: no archive for $(uname -m)"; exit 0 ;; esac
archive=tereditor-headless-nightly-linux-$arch.tar.gz
rel=$root/release
mkdir -p "$rel" "$root/pkg/tereditor-headless/plugins"
printf '#!/bin/sh\nif [ "$1" = languages ]; then printf "csharp\\tC# (Roslyn)\\t.cs\\t\\n"; fi\n' > "$root/pkg/tereditor-headless/tereditor-headless"
chmod +x "$root/pkg/tereditor-headless/tereditor-headless"
echo plugin > "$root/pkg/tereditor-headless/plugins/p.txt"
tar -C "$root/pkg" -cf - tereditor-headless | gzip -9n > "$rel/$archive"
if command -v sha256sum > /dev/null; then hash=$(sha256sum "$rel/$archive" | cut -d' ' -f1); else hash=$(shasum -a 256 "$rel/$archive" | cut -d' ' -f1); fi
echo "$hash  $archive" > "$rel/SHA256SUMS"
echo "0000000000000000000000000000000000000000000000000000000000000000  other.zip" >> "$rel/SHA256SUMS"
export TEREDITOR_HEADLESS_URL_BASE=file://$rel
setup() { bash "$here/headless-cloud-setup.sh" "$@"; }
rm -rf "$HOME" && mkdir -p "$HOME"

setup > "$root/out1" 2>&1 || { cat "$root/out1" >&2; echo "FAIL: setup run" >&2; exit 1; }
d=$HOME/.local/share/tereditor-headless
check test -x "$d/app/tereditor-headless"
check test -f "$d/app/plugins/p.txt"
check test "$(cat "$d/archive.sha256")" = "$hash"
check test "$(readlink "$HOME/.local/bin/tereditor-headless")" = "$d/app/tereditor-headless"
check jq -e --arg c "$HOME/.local/bin/tereditor-headless" '.mcpServers.tereditor.command == $c and .mcpServers.tereditor.alwaysLoad == true' "$HOME/.claude.json" > /dev/null
check grep -q 'C# (Roslyn) (csharp)' "$HOME/.claude/CLAUDE.md"
check jq -e '.env.MCP_TIMEOUT == "300000"' "$HOME/.claude/settings.json" > /dev/null

# A second run installs nothing: the archive is not fetched (the release folder is empty of it), files and config stay.
first=$(sum)
mv "$rel/$archive" "$root/$archive"
setup > "$root/out2" 2>&1 || { cat "$root/out2" >&2; echo "FAIL: second run" >&2; exit 1; }
check test "$(sum)" = "$first"
mv "$root/$archive" "$rel/$archive"

# A pinned hash: the right one installs without SHA256SUMS, a wrong one fails and installs nothing.
rm -rf "$HOME/.local" && mv "$rel/SHA256SUMS" "$root/SHA256SUMS"
TEREDITOR_HEADLESS_SHA256=$(printf '%s' "$hash" | tr a-f A-F) setup > "$root/out3" 2>&1 || { cat "$root/out3" >&2; echo "FAIL: pinned run" >&2; exit 1; }
check test -x "$d/app/tereditor-headless"
rm -rf "$HOME/.local"
if TEREDITOR_HEADLESS_SHA256=$(printf '1%.0s' $(seq 64)) setup > "$root/out4" 2>&1; then echo "FAIL: a wrong pin installed" >&2; exit 1; fi
check grep -q 'pinned by TEREDITOR_HEADLESS_SHA256' "$root/out4"
check test ! -e "$d"
if setup > "$root/out5" 2>&1; then echo "FAIL: a missing SHA256SUMS installed" >&2; exit 1; fi
check grep -q 'cannot download .*SHA256SUMS' "$root/out5"
check test ! -e "$d"

# A tampered archive and a SHA256SUMS without this archive both fail; the old install stays on failure.
mv "$root/SHA256SUMS" "$rel/SHA256SUMS"
setup > /dev/null 2>&1
echo tamper >> "$rel/$archive"
rm -f "$d/archive.sha256"
if setup > "$root/out6" 2>&1; then echo "FAIL: a tampered archive installed" >&2; exit 1; fi
check grep -q "sha256 of $archive is" "$root/out6"
grep -v "$archive" "$rel/SHA256SUMS" > "$root/sums" && mv "$root/sums" "$rel/SHA256SUMS"
if setup > "$root/out7" 2>&1; then echo "FAIL: an archive missing from SHA256SUMS installed" >&2; exit 1; fi
check grep -q "lists no $archive" "$root/out7"
echo "PASS headless cloud setup: the MCP entry (alwaysLoad), MCP_TIMEOUT and the notes are written to a temporary HOME, existing content is kept, a second run changes nothing, invalid JSON is left alone; a local release installs from its archive, a second run downloads nothing, a matching pin needs no SHA256SUMS, a wrong pin, a missing SHA256SUMS, a tampered archive and an unlisted archive fail"
