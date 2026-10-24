#!/bin/sh
# Build the browser client's lazily downloaded userspace Tailscale node (Go -> js/wasm); no system Go installation is used.
# Output: this checkout's src/SharpRail.Browser/obj/tailscale-wasm, which the browser build deploys under `tailscale/` in the
# AppBundle: manifest.json (read first, never cached), and <version>-<build>/ with the module, its Brotli sidecar and Go's
# wasm_exec.js, plus licences. The finished result is cached machine-wide under $SHARPRAIL_CACHE_DIR/tailscale-wasm/<key>.
# Needs node on PATH (the browser toolchain) to measure and write the Brotli sidecar.
set -eu
cd "$(dirname "$0")/.."
root="$PWD"
. "$root/scripts/lock.sh"
. "$root/scripts/native-cache.sh"
source_dir="$root/src/SharpRail.Browser/Tailscale.Wasm"
out="$root/src/SharpRail.Browser/obj/tailscale-wasm"
goroot="$(sh scripts/tailscale-go.sh)"
gocache="$(dirname "$goroot")"
tsversion="$(sed -n 's/^require tailscale\.com v\([0-9][0-9.]*\)$/\1/p' "$source_dir/go.mod")"
[ -n "$tsversion" ] || { echo "No tailscale.com version in $source_dir/go.mod." >&2; exit 1; }
# Features a browser node cannot use or SharpRail does not need; each ts_omit_ tag removes code from the module.
omit="ace acme appconnectors aws advertiseexitnode advertiseroutes bird c2n cachenetmap captiveportal capture clientmetrics
clientupdate cloud conn25 dbus debug debugeventbus debugportmapper dnsresolvecache doctor drive identityfederation kube
linuxdnsfight linkspeed listenrawdisco logtail oauthkey outboundproxy osrouter peerapiclient peerapiserver portlist portmapper
posture netlog networkmanager serviceclientprefs favorites qrcodes relayserver remoteconfig resolved routecheck runtimemetrics
sdnotify serve ssh synology syslog syspolicy systray taildrop tap tpm tundevstats useroutes useexitnode useproxy usermetrics
wakeonlan webbrowser webclient bakedroots completion cli colorable cliconndiag hujsonconf flashappliance androiddns iptables gro"
tags="$(for name in $omit; do printf 'ts_omit_%s,' "$name"; done)"
tags="${tags%,}"
fingerprint="$(cat "$source_dir/go.mod" "$source_dir/go.sum" "$source_dir/main.go" "$root/scripts/build-tailscale-wasm.sh" "$root/scripts/tailscale-go.sh" | shasum -a 256 | cut -c1-64)"
key="$(cache_key "$fingerprint")"
build="$tsversion-$key"
cache="$(sharprail_cache_dir)/tailscale-wasm"
result="$cache/$key"
mkdir -p "$cache"
acquire_lock "$cache/$key.lock"
if [ ! -f "$result/manifest.json" ]; then
 rm -rf "$result.partial"
 mkdir -p "$result.partial/$build" "$result.partial/licenses"
 (
  cd "$source_dir"
  export GOOS=js GOARCH=wasm CGO_ENABLED=0 GOTOOLCHAIN=local GOCACHE="$gocache/build-cache" GOMODCACHE="$gocache/modules"
  stamp="$tsversion-sharprail"
  "$goroot/bin/go" build -mod=readonly -trimpath -tags "$tags" -ldflags="-s -w -buildid= -X tailscale.com/version.shortStamp=$stamp -X tailscale.com/version.longStamp=$stamp" \
   -o "$result.partial/$build/sharprail-tailscale.wasm" .
  "$goroot/bin/go" list -m -f '{{if .Dir}}{{.Path}}|{{.Dir}}{{end}}' all > "$result.partial/modules.list"
 )
 cp "$goroot/lib/wasm/wasm_exec.js" "$result.partial/$build/wasm_exec.js"
 cp "$source_dir/TAILSCALE-LICENSE" "$result.partial/licenses/TAILSCALE-LICENSE"
 cp "$goroot/LICENSE" "$result.partial/licenses/GO-LICENSE"
 # One deterministic dependency licence bundle, module paths sorted bytewise, as the Android build does.
 : > "$result.partial/licenses/THIRD-PARTY-NOTICES.txt"
 LC_ALL=C sort "$result.partial/modules.list" | while IFS='|' read -r module directory; do
  [ -n "$directory" ] || continue
  for license in "$directory"/LICENSE* "$directory"/COPYING* "$directory"/NOTICE*; do
   [ -f "$license" ] || continue
   printf '\n===== %s/%s =====\n\n' "$module" "$(basename "$license")" >> "$result.partial/licenses/THIRD-PARTY-NOTICES.txt"
   cat "$license" >> "$result.partial/licenses/THIRD-PARTY-NOTICES.txt"
   printf '\n' >> "$result.partial/licenses/THIRD-PARTY-NOTICES.txt"
  done
 done
 rm "$result.partial/modules.list"
 node --input-type=module - "$result.partial" "$build" "$tsversion" <<'EOF'
import fs from 'node:fs';
import { brotliCompressSync, constants } from 'node:zlib';
const [directory, build, version] = process.argv.slice(2);
const module = `${directory}/${build}/sharprail-tailscale.wasm`;
const bytes = fs.readFileSync(module);
for (const name of ['sharprail-tailscale.wasm', 'wasm_exec.js']) {
  const file = `${directory}/${build}/${name}`;
  fs.writeFileSync(`${file}.br`, brotliCompressSync(fs.readFileSync(file), { params: { [constants.BROTLI_PARAM_QUALITY]: 11 } }));
}
const manifest = {
  version, build,
  module: `tailscale/${build}/sharprail-tailscale.wasm`,
  exec: `tailscale/${build}/wasm_exec.js`,
  size: bytes.length,
  transferSize: fs.statSync(`${module}.br`).size,
};
fs.writeFileSync(`${directory}/manifest.json`, JSON.stringify(manifest));
EOF
 publish_result "$result"
fi
# Older builds' directories would otherwise accumulate in the checkout's output.
rm -rf "$out.partial"
mkdir -p "$out.partial"
cp -R "$result/." "$out.partial/"
rm -rf "$out"
mv "$out.partial" "$out"
printf '%s\n' "$out/manifest.json"
