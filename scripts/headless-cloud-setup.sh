#!/bin/bash
# Installs the headless SharpRail binary without Docker and registers it as an MCP server for Claude Code.
# Made for the setup script of a cloud environment on claude.ai/code. See docs/headless-cloud.md.
# Environment: SHARPRAIL_IMAGE_TAG (tag or sha256 digest, default nightly), SHARPRAIL_WARM=1 (pre-install the C# server, about 650 MB).
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

# The self-contained app needs libicu (the image's runtime-deps base has it, a bare Ubuntu does not).
if ! ldconfig -p | grep -q libicuuc && command -v apt-get > /dev/null; then apt-get install -y --no-install-recommends libicu74 || echo "headless-cloud-setup: could not install libicu74" >&2; fi

# Outside the image the language servers need the managed .NET SDK, which downloads on first use.
export SHARPRAIL_TOOLCHAINS=managed
if [ "${SHARPRAIL_WARM:-0}" = 1 ]; then "$bin/sharprail-headless" warm csharp || echo "headless-cloud-setup: warm failed, the first lsp call will install instead" >&2; fi

if command -v claude > /dev/null && claude mcp add --scope user --env SHARPRAIL_TOOLCHAINS=managed --transport stdio sharprail -- "$bin/sharprail-headless" mcp; then
  echo "headless-cloud-setup: registered the MCP server sharprail (user scope)"
else
  echo "headless-cloud-setup: claude mcp add did not run. Commit this .mcp.json instead:"
  echo '{"mcpServers":{"sharprail":{"command":"${HOME}/.local/bin/sharprail-headless","args":["mcp"],"env":{"SHARPRAIL_TOOLCHAINS":"managed"}}}}'
fi
