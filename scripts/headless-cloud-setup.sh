#!/bin/bash
# Installs the headless tereditor binary without Docker and registers it as an MCP server for Claude Code.
# Made for the setup script of a cloud environment on claude.ai/code. See docs/headless-cloud.md.
# It downloads tereditor-headless-nightly-linux-<x64|arm64>.tar.gz from the nightly GitHub release and checks it against the release's SHA256SUMS.
# Environment: TEREDITOR_HEADLESS_SHA256 (pin the archive's sha256 out of band; the script fails if the download differs, and needs no SHA256SUMS),
# TEREDITOR_HEADLESS_URL_BASE (where the archive and SHA256SUMS are, default the nightly release; for mirrors and tests),
# TEREDITOR_WARM=1 (pre-install the C# server, about 650 MB), TEREDITOR_CLAUDE_MD=0 (do not write the usage notes into ~/.claude/CLAUDE.md).
# It also adds MCP_TIMEOUT=300000 to the env block of ~/.claude/settings.json unless set there.
set -euo pipefail

url_base=${TEREDITOR_HEADLESS_URL_BASE:-https://github.com/CommanderTvis/tereditor/releases/download/nightly}
dest=$HOME/.local/share/tereditor-headless
bin=$HOME/.local/bin
fail() { echo "headless-cloud-setup: $*" >&2; exit 1; }
warn() { echo "headless-cloud-setup: $*" >&2; }

# Claude Code's user-level files. With CLAUDE_CONFIG_DIR set they all live under it (checked with Claude Code 2.1.322: the MCP file is then $CLAUDE_CONFIG_DIR/.claude.json), else ~/.claude.json and ~/.claude/.
config_dir=${CLAUDE_CONFIG_DIR:-$HOME/.claude}
claude_json=${CLAUDE_CONFIG_DIR:+$CLAUDE_CONFIG_DIR/.claude.json}
claude_json=${claude_json:-$HOME/.claude.json}

# merge_json FILE JQ_ARGS...: applies a jq filter to FILE (created as {} if absent or empty), keeping every other key. A file that is not a valid JSON object is never touched.
# Atomic (temp file, then mv) and idempotent: when the result equals the current content the file is not rewritten.
merge_json() {
  local file=$1 tmp
  shift
  mkdir -p "$(dirname "$file")"
  [ -s "$file" ] || echo '{}' > "$file"
  jq -e 'type == "object"' "$file" > /dev/null 2>&1 || { warn "$file is not a JSON object, left alone"; return 1; }
  tmp=$(mktemp "$file.XXXXXX")
  if jq "$@" "$file" > "$tmp" && [ -s "$tmp" ]; then
    if cmp -s "$tmp" "$file"; then rm -f "$tmp"; else mv "$tmp" "$file"; fi
  else rm -f "$tmp"; return 1; fi
}

# register_mcp BINARY: the user-scope stdio server `tereditor` (the key is part of the tool names mcp__tereditor__*). alwaysLoad puts its tools into the first prompt.
# A local or project entry of the same name wins over this one: Claude Code connects once, matching by name, and uses the whole winning entry.
register_mcp() {
  local binary=$1 entry
  entry=$(jq -nSc --arg c "$binary" '{type: "stdio", command: $c, args: ["mcp"], env: {TEREDITOR_TOOLCHAINS: "managed"}, alwaysLoad: true}')
  registered() { [ "$(jq -Sc '.mcpServers.tereditor // empty' "$claude_json" 2> /dev/null)" = "$entry" ]; }
  if registered; then echo "headless-cloud-setup: MCP server tereditor already registered"; return 0; fi
  # The supported way first: add-json accepts alwaysLoad (Claude Code 2.1.322). It refuses a name that exists, so remove ours first.
  if command -v claude > /dev/null; then
    claude mcp remove --scope user tereditor > /dev/null 2>&1 || true
    if claude mcp add-json --scope user tereditor "$entry" > /dev/null 2>&1 && registered; then echo "headless-cloud-setup: registered the MCP server tereditor (claude mcp add-json, user scope)"; return 0; fi
  fi
  if merge_json "$claude_json" --argjson e "$entry" '.mcpServers.tereditor = $e' && registered; then echo "headless-cloud-setup: registered the MCP server tereditor in $claude_json"; return 0; fi
  warn "could not register the MCP server. Commit this .mcp.json instead:"
  echo "{\"mcpServers\":{\"tereditor\":$entry}}"
  return 1
}

# configure_timeouts: MCP_TIMEOUT (how long Claude Code waits for a server to connect, default 30 s) in the user settings env block. A value the user already set stays.
# MCP_TOOL_TIMEOUT stays at its default of about 28 hours. Whether a cloud session reads this file is not documented; the environment's own variables are the documented place, see docs/headless-cloud.md.
configure_timeouts() {
  if merge_json "$config_dir/settings.json" '.env = ((.env // {}) | .MCP_TIMEOUT //= "300000")'; then echo "headless-cloud-setup: MCP_TIMEOUT in $config_dir/settings.json"; fi
}

# write_notes LANGUAGES: the usage notes between marker lines in CLAUDE.md of the config dir, replaced on every run, the rest of the file kept.
write_notes() {
  local languages=$1 notes=$config_dir/CLAUDE.md kept begin='<!-- tereditor-headless:begin -->' end='<!-- tereditor-headless:end -->'
  mkdir -p "$config_dir" && touch "$notes"
  kept=$(mktemp) && awk -v b="$begin" -v e="$end" '$0 == b { skip = 1 } !skip { print } $0 == e { skip = 0 }' "$notes" > "$kept" && cat "$kept" > "$notes" && rm -f "$kept"
  cat >> "$notes" <<NOTES
$begin
# Headless tereditor

The MCP server \`tereditor\` gives you code intelligence; its tools are \`mcp__tereditor__<tool>\` (lsp_navigate, lsp_diagnostics, lsp_status, lsp_code_actions, lsp_rename, lsp_format; spec_* and blueprint_check only when TEREDITOR_TOOLS_ENABLE=spec,blueprint is set). If a first tool search finds none, the server is still connecting: search again (or run \`/mcp\`) and do not start the work without them.

- Use \`lsp_navigate\` instead of grep for symbols, callers and implementations in the languages below; grep only for literals, comments, configuration and other file types, and say why. Name the symbol, no position needed: \`{"operation": "references", "query": "TypeName"}\` (also \`definition\`, \`implementation\`, \`hover\`, \`symbols\` with a \`path\`).
- Run \`lsp_diagnostics\` with \`paths\` for every file you edited in these languages before you declare the work done; there may be no compiler on this machine. Partial, pending or skipped coverage is not clean.
- \`initializing\` means the language server is still downloading (the .NET SDK for C# can take a minute or more): wait and call again. Never treat it as "no results".
- Paths are relative to the directory Claude Code started in; the \`workspace\` argument is ignored. Use \`lsp_code_actions\`, \`lsp_rename\` and \`lsp_format\` (preview, then apply) instead of hand-editing semantic changes.
- Languages with a server (name, id, extensions):
$languages
- A language marked with a note in brackets needs the user's agreement to terms before its first use: the first call returns the terms and the steps. Ask the user in chat; call \`accept_terms\` only after they said yes, never on your own.
- Subagents do NOT receive this text: put these rules in their brief and require their report to state how many \`lsp_navigate\` and \`lsp_diagnostics\` calls they made.
$end
NOTES
}

# For tests: TEREDITOR_SETUP_SOURCE_ONLY=1 defines the functions above and stops before anything is downloaded or installed.
if [ -n "${TEREDITOR_SETUP_SOURCE_ONLY:-}" ]; then return 0 2> /dev/null || exit 0; fi

case "$(uname -m)" in
  x86_64 | amd64) arch=x64 ;;
  aarch64 | arm64) arch=arm64 ;;
  *) fail "unsupported machine $(uname -m): the archives are for x86_64 and aarch64 Linux" ;;
esac
for tool in curl jq tar; do command -v "$tool" > /dev/null || fail "$tool is missing"; done
sha256() { if command -v sha256sum > /dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

name=tereditor-headless-nightly-linux-$arch.tar.gz
url_base=${url_base%/}
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# The expected sha256: the pin if there is one, else the line of the release's SHA256SUMS for this archive.
expected=$(printf '%s' "${TEREDITOR_HEADLESS_SHA256:-}" | tr 'A-F' 'a-f')
if [ -z "$expected" ]; then
  curl -fsSL --retry 3 -o "$tmp/SHA256SUMS" "$url_base/SHA256SUMS" || fail "cannot download $url_base/SHA256SUMS (is github.com allowed, and does the nightly release exist?)"
  expected=$(awk -v n="$name" '$2 == n || $2 == "*" n { print $1; exit }' "$tmp/SHA256SUMS")
  [ -n "$expected" ] || fail "SHA256SUMS lists no $name: the nightly release has no archive for linux/$arch (an arm64 build may have failed that night)"
fi
case "$expected" in *[!0-9a-f]* | "") fail "not a sha256: $expected" ;; esac
[ "${#expected}" = 64 ] || fail "not a sha256: $expected"
echo "headless-cloud-setup: $name sha256 $expected"

if [ "$(cat "$dest/archive.sha256" 2> /dev/null)" != "$expected" ] || [ ! -x "$dest/app/tereditor-headless" ]; then
  curl -fsSL --retry 3 -o "$tmp/archive.tar.gz" "$url_base/$name" || fail "cannot download $url_base/$name (is github.com allowed, and is the release asset there?)"
  actual=$(sha256 "$tmp/archive.tar.gz")
  [ "$actual" = "$expected" ] || fail "sha256 of $name is $actual but $expected was expected${TEREDITOR_HEADLESS_SHA256:+ (pinned by TEREDITOR_HEADLESS_SHA256; the nightly archive moves every night, so pin one you have checked)}"
  mkdir "$tmp/x" && tar -xzf "$tmp/archive.tar.gz" -C "$tmp/x" || fail "cannot unpack $name"
  [ -x "$tmp/x/tereditor-headless/tereditor-headless" ] || fail "$name holds no tereditor-headless/tereditor-headless"
  rm -rf "$dest" && mkdir -p "$dest" && mv "$tmp/x/tereditor-headless" "$dest/app" && echo "$expected" > "$dest/archive.sha256"
fi
mkdir -p "$bin" && ln -sf "$dest/app/tereditor-headless" "$bin/tereditor-headless"
echo "headless-cloud-setup: installed $bin/tereditor-headless"

# The app needs libicu (a bare Ubuntu may not have it; the container image's runtime-deps base does).
if command -v ldconfig > /dev/null && command -v apt-get > /dev/null && ! ldconfig -p | grep -q libicuuc; then apt-get install -y --no-install-recommends libicu74 || echo "headless-cloud-setup: could not install libicu74" >&2; fi

# Outside the image the language servers need the managed .NET SDK, which downloads on first use.
export TEREDITOR_TOOLCHAINS=managed
if [ "${TEREDITOR_WARM:-0}" = 1 ]; then "$bin/tereditor-headless" warm csharp || echo "headless-cloud-setup: warm failed, the first lsp call will install instead" >&2; fi

# The launcher's code-intelligence guidance, adapted to the headless server, with the languages the installed binary serves (id, name and extensions per line; a binary without the command serves C# only).
# TEREDITOR_CLAUDE_MD=0 leaves CLAUDE.md alone (for an arm without these tools).
if [ "${TEREDITOR_CLAUDE_MD:-1}" = 1 ]; then
  languages=$("$bin/tereditor-headless" languages 2> /dev/null | awk -F'\t' '{ printf "  - %s (%s): %s%s\n", $2, $1, $3, ($4 == "" ? "" : "  [" $4 "]") }') || languages=
  [ -n "$languages" ] || languages='  - C# (Roslyn) (csharp): .cs'
  write_notes "$languages"
  echo "headless-cloud-setup: wrote usage notes to $config_dir/CLAUDE.md"
fi

# The server and the timeout go into Claude Code's user config, which the setup script (it runs before Claude Code starts) writes for the session.
register_mcp "$bin/tereditor-headless" || true
configure_timeouts
