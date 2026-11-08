#!/bin/bash
# Installs the headless SharpRail binary without Docker and registers it as an MCP server for Claude Code.
# Made for the setup script of a cloud environment on claude.ai/code. See docs/headless-cloud.md.
# Environment: SHARPRAIL_IMAGE_TAG (tag or sha256 digest, default nightly), SHARPRAIL_WARM=1 (pre-install the C# server, about 650 MB),
# SHARPRAIL_CLAUDE_MD=0 (do not write the usage notes into ~/.claude/CLAUDE.md).
set -euo pipefail

repo=commandertvis/sharprail
ref=${SHARPRAIL_IMAGE_TAG:-nightly}
dest=$HOME/.local/share/sharprail-headless
bin=$HOME/.local/bin
fail() { echo "headless-cloud-setup: $*" >&2; exit 1; }

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
  [ -n "$digest" ] || fail "$repo:$ref has no linux/$arch image (it has: $(jq -r '[.manifests[].platform.architecture] | join(", ")' <<< "$manifest")). Publish one or set SHARPRAIL_IMAGE_TAG."
  manifest=$(fetch "manifests/$digest")
fi
# The last layer is the COPY of the publish output (/opt/sharprail). Its digest is the sha256 of the blob.
layer=$(jq -r '.layers[-1].digest' <<< "$manifest")
echo "headless-cloud-setup: $repo:$ref linux/$arch, app layer $layer"

if [ "$(cat "$dest/layer" 2> /dev/null)" != "$layer" ] || [ ! -x "$dest/app/sharprail-headless" ]; then
  tmp=$(mktemp -d)
  trap 'rm -rf "$tmp"' EXIT
  # curl drops the Authorization header on the redirect to the blob host.
  curl -fsSL -H "Authorization: Bearer $token" -o "$tmp/layer.tgz" "https://ghcr.io/v2/$repo/blobs/$layer" || fail "layer download failed (is pkg-containers.githubusercontent.com allowed?)"
  [ "$(sha256sum "$tmp/layer.tgz" | cut -d' ' -f1)" = "${layer#sha256:}" ] || fail "sha256 of the layer does not match $layer"
  mkdir "$tmp/x" && tar -xzf "$tmp/layer.tgz" -C "$tmp/x"
  [ -x "$tmp/x/opt/sharprail/sharprail-headless" ] || fail "the layer holds no opt/sharprail/sharprail-headless"
  rm -rf "$dest" && mkdir -p "$dest" && mv "$tmp/x/opt/sharprail" "$dest/app" && echo "$layer" > "$dest/layer"
fi
mkdir -p "$bin" && ln -sf "$dest/app/sharprail-headless" "$bin/sharprail-headless"
echo "headless-cloud-setup: installed $bin/sharprail-headless"

# The app needs libicu (the image's runtime-deps base has it, a bare Ubuntu does not).
if ! ldconfig -p | grep -q libicuuc && command -v apt-get > /dev/null; then apt-get install -y --no-install-recommends libicu74 || echo "headless-cloud-setup: could not install libicu74" >&2; fi

# Outside the image the language servers need the managed .NET SDK, which downloads on first use.
export SHARPRAIL_TOOLCHAINS=managed
if [ "${SHARPRAIL_WARM:-0}" = 1 ]; then "$bin/sharprail-headless" warm csharp || echo "headless-cloud-setup: warm failed, the first lsp call will install instead" >&2; fi

# The launcher's code-intelligence guidance (CorePrompt.LanguageServices), adapted to the headless server: one workspace per process, C# only.
# The block between the marker lines is replaced on every run. SHARPRAIL_CLAUDE_MD=0 leaves the file alone (for an arm without these tools).
if [ "${SHARPRAIL_CLAUDE_MD:-1}" = 1 ]; then
  notes=$HOME/.claude/CLAUDE.md
  begin='<!-- sharprail-headless:begin -->'
  end='<!-- sharprail-headless:end -->'
  mkdir -p "$HOME/.claude" && touch "$notes"
  kept=$(mktemp) && awk -v b="$begin" -v e="$end" '$0 == b { skip = 1 } !skip { print } $0 == e { skip = 0 }' "$notes" > "$kept" && cat "$kept" > "$notes" && rm -f "$kept"
  cat >> "$notes" <<NOTES
$begin
# You are running with headless SharpRail

The MCP server \`sharprail\` gives you code intelligence. Its tools are named mcp__sharprail__<tool>.

## Code intelligence: use \`lsp_navigate\` and \`lsp_diagnostics\`, not grep

grep answers code questions wrongly: it misses implementations, overloads, partial classes and generated code.
- You MUST use \`lsp_navigate\` for every question about a C# symbol (definition, callers, implementations, shape) before you grep or Read whole files. Use grep ONLY for literals, comments, configuration, documentation and non-code files, or after \`lsp_navigate\` returned incomplete results; then say why in your report.
- You MUST run \`lsp_diagnostics\` with \`paths\` listing every C# file you edited before you declare the work done. There may be no .NET SDK on this machine, so it is also how you find compile errors. Partial, pending or skipped coverage is not clean.
- The server's workspace is the directory Claude Code started it in. Its \`workspace\` argument is ignored. Give \`paths\` relative to that directory, for example \`csdemo/Program.cs\` when you work in a subfolder. An absolute path outside it is refused.
- The \`lsp_*\` tools start the language server a file needs, then may report \`initializing\`: wait and call again, NEVER treat that as "no results" and NEVER fall back silently to grep. The first C# start downloads the .NET SDK and the server and can take a minute or more.
- Only C# has a language server here. For other file types use grep and Read and say so in your report.
- Name the symbol, no position needed: \`{"operation": "references", "query": "TypeName"}\` (also \`definition\`, \`implementation\`, \`hover\`); \`Type/Member\` narrows a shared name. \`symbols\` with a \`path\` outlines a file, \`symbolBody\` with a \`path\` and \`query\` reads one member. Explicit \`line\` and \`column\` are zero-based. \`lsp_status\` shows the server state.
- For semantic changes use \`lsp_code_actions\` (list, then preview or apply by id), \`lsp_rename\` (preview, then apply) and \`lsp_format\` instead of hand-editing; a missing capability is reported as unsupported. \`lsp_rename\` with \`path\` and \`newPath\` moves a file and updates its imports; \`lsp_navigate\` also has \`incomingCalls\` and \`outgoingCalls\`; \`lsp_diagnostics\` takes globs and \`minSeverity\`.
- Subagents do NOT receive this text. When you brief one for code work, put these rules in the brief and say that its final report MUST state how many \`lsp_navigate\` and \`lsp_diagnostics\` calls it made.
- Right after a session starts the server may still be connecting. If the tools are missing, run \`claude mcp list\` once and ask again.
$end
NOTES
  echo "headless-cloud-setup: wrote usage notes to $notes"
fi

if command -v claude > /dev/null && claude mcp add --scope user --env SHARPRAIL_TOOLCHAINS=managed --transport stdio sharprail -- "$bin/sharprail-headless" mcp; then
  echo "headless-cloud-setup: registered the MCP server sharprail (user scope)"
else
  echo "headless-cloud-setup: claude mcp add did not run. Commit this .mcp.json instead:"
  echo '{"mcpServers":{"sharprail":{"command":"${HOME}/.local/bin/sharprail-headless","args":["mcp"],"env":{"SHARPRAIL_TOOLCHAINS":"managed"}}}}'
fi
