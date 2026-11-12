#!/bin/bash
# Installs the headless tereditor binary without Docker and registers it as an MCP server for Claude Code.
# Made for the setup script of a cloud environment on claude.ai/code. See docs/headless-cloud.md.
# Environment: TEREDITOR_IMAGE_TAG (tag or sha256 digest, default nightly), TEREDITOR_WARM=1 (pre-install the C# server, about 650 MB),
# TEREDITOR_CLAUDE_MD=0 (do not write the usage notes into ~/.claude/CLAUDE.md). It also adds MCP_TIMEOUT=300000 to the env block of ~/.claude/settings.json unless set there.
set -euo pipefail

repo=commandertvis/tereditor
ref=${TEREDITOR_IMAGE_TAG:-nightly}
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
  x86_64 | amd64) arch=amd64 ;;
  aarch64 | arm64) arch=arm64 ;;
  *) fail "unsupported machine $(uname -m)" ;;
esac
for tool in curl jq tar sha256sum; do command -v "$tool" > /dev/null || fail "$tool is missing"; done

token=$(curl -fsS "https://ghcr.io/token?service=ghcr.io&scope=repository:$repo:pull" | jq -r .token)
accept="application/vnd.oci.image.index.v1+json,application/vnd.docker.distribution.manifest.list.v2+json,application/vnd.oci.image.manifest.v1+json,application/vnd.docker.distribution.manifest.v2+json"
fetch() { curl -fsS -H "Authorization: Bearer $token" -H "Accept: $accept" "https://ghcr.io/v2/$repo/$1"; }

manifest=$(fetch "manifests/$ref") || fail "cannot read $repo:$ref (is ghcr.io reachable and the package public?)"
if [ "$(jq 'has("manifests")' <<< "$manifest")" = true ]; then
  digest=$(jq -r --arg a "$arch" '[.manifests[] | select(.platform.os == "linux" and .platform.architecture == $a)][0].digest // empty' <<< "$manifest")
  [ -n "$digest" ] || fail "$repo:$ref has no linux/$arch image (it has: $(jq -r '[.manifests[].platform.architecture] | join(", ")' <<< "$manifest")). Publish one or set TEREDITOR_IMAGE_TAG."
  manifest=$(fetch "manifests/$digest")
fi
# The last layer is the COPY of the publish output (/opt/tereditor). Its digest is the sha256 of the blob.
layer=$(jq -r '.layers[-1].digest' <<< "$manifest")
echo "headless-cloud-setup: $repo:$ref linux/$arch, app layer $layer"

if [ "$(cat "$dest/layer" 2> /dev/null)" != "$layer" ] || [ ! -x "$dest/app/tereditor-headless" ]; then
  tmp=$(mktemp -d)
  trap 'rm -rf "$tmp"' EXIT
  # curl drops the Authorization header on the redirect to the blob host.
  curl -fsSL -H "Authorization: Bearer $token" -o "$tmp/layer.tgz" "https://ghcr.io/v2/$repo/blobs/$layer" || fail "layer download failed (is pkg-containers.githubusercontent.com allowed?)"
  [ "$(sha256sum "$tmp/layer.tgz" | cut -d' ' -f1)" = "${layer#sha256:}" ] || fail "sha256 of the layer does not match $layer"
  mkdir "$tmp/x" && tar -xzf "$tmp/layer.tgz" -C "$tmp/x"
  [ -x "$tmp/x/opt/tereditor/tereditor-headless" ] || fail "the layer holds no opt/tereditor/tereditor-headless"
  rm -rf "$dest" && mkdir -p "$dest" && mv "$tmp/x/opt/tereditor" "$dest/app" && echo "$layer" > "$dest/layer"
fi
mkdir -p "$bin" && ln -sf "$dest/app/tereditor-headless" "$bin/tereditor-headless"
echo "headless-cloud-setup: installed $bin/tereditor-headless"

# The app needs libicu (the image's runtime-deps base has it, a bare Ubuntu does not).
if ! ldconfig -p | grep -q libicuuc && command -v apt-get > /dev/null; then apt-get install -y --no-install-recommends libicu74 || echo "headless-cloud-setup: could not install libicu74" >&2; fi

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
