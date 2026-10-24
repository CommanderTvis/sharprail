# Browser userspace Tailscale node

`sh scripts/build-tailscale-wasm.sh` (or `scripts/mise run tailscale:wasm`) builds this module for
`GOOS=js GOARCH=wasm` with the checksum-pinned Go from `scripts/tailscale-go.sh`, Tailscale v1.104.1 from
`go.sum`, and `ts_omit_*` tags for features a browser node does not use. The browser build runs it when its
inputs change. Output: `src/SharpRail.Browser/obj/tailscale-wasm`, deployed under `tailscale/` in the AppBundle:

- `manifest.json`: `version`, `build` (`<version>-<input hash>`), `module`, `exec`, `size`, `transferSize` (Brotli);
- `<build>/sharprail-tailscale.wasm` and its `.br` sidecar, `<build>/wasm_exec.js` (from the pinned Go);
- `licenses/`: Tailscale and Go licenses and the dependency notice bundle.

`main.go` is adapted from upstream `cmd/tsconnect/wasm/wasm_js.go`: the same userspace engine and netstack, but a
persistent (non-ephemeral) node, no SSH or fetch, no log uploads (no-logs-no-support and the logtail kill switch),
and a TCP dial for SharpRail's pairing and WebSocket RPC.

`sharprailTailscale({ stateStorage, hostname, controlURL?, authKey? })` returns:

- `start()`: starts the backend; does not wait for authentication.
- `status()`: JSON `{ state, authUrl, online, peers: [{ name, address }], error? }`; peers are online IPv4 nodes.
- `login()`, `logout()`: promises; the sign-in URL then appears in `status().authUrl`.
- `dial("100.x.y.z:port", timeoutMs)`: promise of a connection id; numeric addresses only.
- `read(id)` (promise of a `Uint8Array`, `null` at end of stream), `write(id, bytes)` (promise), `close(id)`.

`stateStorage` has synchronous `getState(key)` / `setState(key, hex)`; `wwwroot/tailscale.js` backs it with IndexedDB.
Callbacks never block Go's event loop: every blocking call runs on a goroutine behind a promise.