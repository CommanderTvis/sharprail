---
id: module-plugin-tailscale
type: module-design
status: draft
title: Tailscale — host discovery and device pairing
parent: module-plugin-api
depends-on: [module-plugin-api, module-plugin-ui-kit]
references: [submodule-host-plugins, submodule-ui-plugins, submodule-ui-panels]
tags: [plugins, tailscale]
---

# Tailscale — host discovery and device pairing

No upstream counterpart. Original SharpRail builtin plugin.

## Responsibility

Connect clients to a host on the same tailnet without exchanging numeric addresses, ports or
session tokens. The plugin ships disabled. Enabling it starts a bootstrap listener on the host's Tailscale
IPv4 address at port 54124; it never binds a LAN, wildcard or public address. It uses the installed Tailscale
CLI and requires an active connection. Missing CLI, disconnected Tailscale and port conflicts appear in
Settings › Tailscale. Disabling the plugin stops bootstrap and invalidates its grants for serving.

The desktop picker discovers online peers through `tailscale status --json`, probes at most 256 of them
with eight concurrent requests, and lists only SharpRail hosts. Android owns an embedded userspace
Tailscale node instead of reading another application's private peer list. It signs in through the system
browser, retains its identity in app-private storage, enumerates online IPv4 peers from its own netmap and
probes the same bootstrap endpoints. No separate Tailscale Android application, system VPN or OS MagicDNS
configuration is required. The Android adapter supplies ConnectivityManager snapshots through Tailscale's supported interface hook,
avoiding restricted raw netlink enumeration and updating the same node on network changes.
The shared picker accepts an injected client network for HTTP, name resolution
and peer enumeration; desktop keeps its installed-CLI implementation. Embedded discovery returns numeric
peer addresses so choosing a host never depends on system DNS. Pairing and settings buttons use consistent 40-pixel minimum
heights with horizontally and vertically centered text. No tailnet API key, central registry,
Tailscale Serve configuration, DNS changes or installation of another VPN is needed.

## Pairing and authentication

Bootstrap verifies the request's real source address through `tailscale whois --json`; forwarded headers
are ignored. Only the Tailscale address ranges are eligible. Discovery returns the host name alone. A
client generates a random 256-bit secret and sends it only to a directly resolved tailnet address; the
HTTP client disables redirects. Desktop disables proxies; Android explicitly uses only its authenticated
loopback SOCKS5 proxy, whose dialer is the embedded Tailscale node. Tailscale supplies transport encryption.

A new secret creates a request bound to the verified Tailscale node, with its device name and user identity.
The host shows that identity and an eight-digit hexadecimal fingerprint; the client shows the same code.
An activation-lifetime subscription shows a popup with the device identity, comparison code and
instructions to open Settings › Tailscale even while Settings is closed. Repeated polls do not repeat
the popup for the same pending request. The user approves or declines it in Settings › Tailscale. Requests expire after two minutes, declined
requests cannot be recreated by polling, and pending requests and approved grants are bounded to 32 and
128 respectively. Pairing never releases the host's master session token.

Approval persists the hash of the client's secret and its node identity in plugin state. Remembered
devices are grouped by verified node identity, including existing persisted grants. Multiple approved
client secrets on one node remain valid; revoking its single device row revokes every grant for that node. The client stores
its own secret and host name in client-local `tailscale-client.json`, with user-only Unix permissions.
An approved bootstrap returns the actual WebSocket RPC address and port; the client authenticates using its own
secret. A remembered bootstrap secret cannot be used by another Tailscale node. The listener accepts
approved client secrets alongside the existing master token. Revoking a grant rejects new calls and ends
its running streaming calls within one second; disabling does the same for every plugin grant. Terminal
shells retain the normal host-owned lifetime.

## Composition and lifecycle

The plugin owns discovery, identity checks, pairing state, bootstrap HTTP and its UI. A plugin-owned
`TailscaleServing` bridge is injected into builtin host construction by the app and standalone host;
it supplies listener startup and authentication without introducing a Remote or UI reference into the
plugin host or Core. The bridge lives in the plugin contract assembly and is not an extension of the public plugin API. Bootstrap cannot depend on
plugin RPC because the client has no connection yet: the builtin UI exposes a client-local picker before
ordinary UI activation. External plugin activation and capability parity are unchanged.

The embedded host starts an ephemeral WebSocket RPC listener on its Tailscale address after approval. An already
running listener is reused when bound to that address or a wildcard; an incompatible listener produces
an actionable error instead of being reconfigured. The plugin stops only the listener it started and only
if that instance is still running. The standalone host reuses its configured listener and requires its
bind address to be reachable through Tailscale. No local adapter is moved onto RPC.

Desktop Settings › Host opens a dedicated client instance; `--tailscale` opens the picker directly. The
Android connect screen offers Tailscale alongside manual entry and QR scanning of host codes. It opens browser sign-in
only after an explicit action, then discovers hosts automatically. Administrator device approval, pending
authentication, missing browser and connection errors remain visible with retry and sign-out controls.
Sign-out logs the node out and forgets the matching SharpRail pairing and remembered endpoint token.
Automatic startup restoration reconnects the remembered host, while opening the picker explicitly keeps
host selection and sign-out available instead of immediately reopening that host. After discovery the
picker selects the peer matching the host field (name, address or MagicDNS prefix, case-insensitive) without
rewriting it, else the first peer, and reports "Found N hosts."; the embedded picker hides the host-name field behind
"Enter a host name" while a peer is selected, and the embedded status line shows only non-obvious states.
The app closes the node on session disposal without deleting identity; backgrounding retains it, and
returning to the foreground resumes status polling and existing RPC reconnection. Both pairing HTTP and
every RPC adapter use this node: RPC handlers are registered for the approved endpoint before channels
are created and removed after adapters close. Manual connections keep ordinary network routing. Remembered pairings
bootstrap again on client launch, so host port changes need no manual editing. Disconnecting on Android also forgets that pairing secret. Existing manual and QR
connections keep their current behavior. A live workbench keeps the existing reconnect behavior; after
a host restart changes its listening port, reopening the picker refreshes the endpoint.

Tailscale state is detected again without restarting the app. The host detects once on activation and then
only when asked: the `refresh` plugin method wakes its supervisor, which retries detection and the
bootstrap listener while pairing is unavailable (a ready host ignores it) and republishes the status.
Settings › Tailscale asks on opening, on window activation (throttled to three seconds), after an
approval or revocation while unavailable, and every five seconds while it is visible and unavailable.
The poll stops when the page detaches and a newer request cancels the previous one. A hidden page
causes no detection.

All CLI, DNS, filesystem, identity and HTTP work runs off the dispatcher. Discovery and pairing are
cancellable and bounded by timeouts. The plugin's disposer cancels requests, closes bootstrap, clears its
authentication callback and stops any listener it owns before returning.

## Verification

`--tailscale` exercises registry expiry and limits, node binding, credential secrecy and persistence,
actual HTTP discovery and approval against a fake Tailscale identity provider, embedded WebSocket RPC startup,
local/remote status parity, authenticated calls, revocation and plugin teardown, and a fake CLI that
flips from disconnected to connected: no detection without a request, and a refresh then makes pairing ready. Fixtures never call the
user's Tailscale daemon. `--android` also checks embedded state reuse, netmap validation and real
authenticated SOCKS5 HTTP probing against a local fake proxy; `--transport` checks endpoint-scoped
WebSocket handler routing, reconnect and disposal. The native bridge has isolated Go tests for its
operation and proxy lifetimes. Desktop and Android device-to-device operation requires manual verification.

`--headscale` (opt-in, slow; see the checks module spec) joins a real host and a real embedded-library
client to a throwaway Headscale tailnet as distinct nodes and verifies discovery, pairing, authenticated
WebSocket RPC, broadcasts and reconnection across it. It does not exercise the host plugin's kernel-interface
pairing listener, which still needs a manual device check.


The plugin list and settings navigation use the official Tailscale nine-dot SVG icon,
staged as a host-served plugin asset. Its fill follows the theme; geometry and opacity
are preserved. Source and trademark attribution are recorded in NOTICE.md.
Android's embedded bridge initializes Tailscale's supported log directory under
app-private node storage, with mode 0700; it does not assume HOME or XDG paths exist.
Library log uploads are disabled. A synchronous native panic is converted into a
credential-free error at the C ABI boundary; it is not a substitute for preventing
background-goroutine failures. The Android module's verification section records the
actual emulator auth milestone and the remaining real-account acceptance boundary.
