# Android userspace Tailscale bridge

Run `sh scripts/build-tailscale-android.sh` from the repository. It downloads
checksum-pinned Go 1.27.2 and builds Tailscale v1.104.1 using `go.sum` and the pinned
Android NDK. Output: `.tools/tailscale-android/arm64-v8a/libSharpRailTailscale.so`.
Pass `x86_64` as the optional first argument for the emulator ABI. The default is
`arm64-v8a`; both target API24. `SHARPRAIL_ANDROID_NDK` may reference an existing pinned NDK. No VPN interface,
external Tailscale application or root privileges are required.

C ABI: `char *sr_ts_call(char *operation, char *inputJson)` and
`void sr_ts_free(void *result)`. Every call returns owned UTF-8 JSON, released with
`sr_ts_free`. Calls serialize internally; invoke off the UI thread. Local API
requests have a five-second deadline.

Operations: `start`, `status`, `login`, `logout`, `stop`. Start accepts
`{"stateDirectory":"/app/private/tailscale","hostname":"sharprail-android"}`.
The directory contains the durable node identity and is restricted to mode 0700.
Start does not wait for authentication. Login requests browser authentication;
subsequent status calls expose Tailscale's authentication URL. Logout revokes the
current login; stop closes the node without deleting its identity.

Responses: `state` (upstream BackendState), `authUrl`, `online`, `peers` (online
nodes as `{name,address}`), `proxyAddress` (`socks5://127.0.0.1:port`),
`proxyUsername`, `proxyPassword`, optional `error`. Proxy credentials are random
and change each start. All TCP routes through `tsnet.Server.Dial`, including DNS.
Both HTTP and WebSocket consumers must use the proxy: ordinary OS sockets do not
route through this node. Never log response JSON or credentials. User/backend log
callbacks discard messages to keep authentication URLs out of Android logs.
Discovery does not bypass SharpRail device pairing approval.

The build stamps the node version as `<go.mod tailscale.com version>-sharprail` through
`tailscale.com/version` linker flags, so the admin console shows no `-dev`/`-dirty` suffix.

Tailscale's BSD-3-Clause license is in `TAILSCALE-LICENSE`; exact dependency
versions are recorded in `go.mod`/`go.sum`. Dependency license files are copied into
the output ABI directory's `licenses` tree for packaging.

Android packages `THIRD-PARTY-NOTICES.txt` as a single deterministic dependency
license bundle, plus explicit Tailscale and Go licenses. The output `licenses`
tree is retained for source/build inspection; module/path headers in the bundle
identify each dependency notice.

Android app UIDs cannot enumerate network interfaces through Go's netlink path.
Before `start`, send `network` with the ConnectivityManager snapshot:
`{"interfaces":[{"name":"wlan0","index":2,"mtu":1500,"addresses":["192.168.1.4/24"]}],"defaultInterface":"wlan0","gateway":"192.168.1.1"}`.
Send further snapshots on connectivity changes, including an empty interface list
when offline. Android installs Tailscale's `netmon.RegisterInterfaceGetter` hook;
no root, netlink access or synthetic addresses are required. Updates also inject
a network-monitor event and refresh the platform default route/gateway hints.

After either native build has provisioned the pinned toolchain, run the isolated
native checks from the repository root (macOS/Linux, no Tailscale account needed):

```sh
task_root="$PWD"
cd src/SharpRail.Android/Tailscale.Native
GOCACHE="$task_root/.tools/tailscale-go/build-cache" \
GOMODCACHE="$task_root/.tools/tailscale-go/modules" GOTOOLCHAIN=local \
"$task_root/.tools/tailscale-go/go/bin/go" test -race .
```

These checks cover C ABI operations, unpredictable proxy credentials, rejection of
unauthenticated SOCKS clients, teardown of accepted sockets, eligible peers and
Android network snapshots. Authenticated account enrollment remains a device check.

Backend logs use `stateDirectory/logs` (0700), selected through upstream's
`TS_LOGS_DIR` before backend creation; Android need not provide HOME/XDG variables.
Cloud log uploads are disabled through both Tailscale's no-logs-no-support mode
and its process-wide logtail kill switch. Synchronous Go panics during native
operations return an error snapshot; panics in independent Go goroutines cannot
be recovered across the C ABI.
