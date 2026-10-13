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

Connect clients to a host on the same Tailscale network without exchanging numeric addresses, ports or
session tokens. The plugin ships disabled. Enabling it starts a bootstrap listener on the host's Tailscale
IPv4 address at port 54124; it never binds a LAN, wildcard or public address. It uses the installed Tailscale
CLI and requires an active connection. Missing CLI, disconnected Tailscale and port conflicts appear in
Settings › Tailscale. Disabling the plugin stops bootstrap and invalidates its grants for serving.

The desktop picker discovers online peers through `tailscale status --json`, probes at most 256 of them
with eight concurrent requests, and lists only SharpRail hosts. Both clients also accept a MagicDNS host
name. Android uses that name because another app cannot assume access to Tailscale's private peer list.
It requires Tailscale routing and MagicDNS to be enabled for SharpRail. No tailnet API key, central registry,
Tailscale Serve configuration, DNS changes or installation of another VPN is needed.

## Pairing and authentication

Bootstrap verifies the request's real source address through `tailscale whois --json`; forwarded headers
are ignored. Only the Tailscale address ranges are eligible. Discovery returns the host name alone. A
client generates a random 256-bit secret and sends it only to a directly resolved tailnet address; the
HTTP client disables proxies and redirects. Tailscale supplies transport encryption.

A new secret creates a request bound to the verified Tailscale node, with its device name and user identity.
The host shows that identity and an eight-digit hexadecimal fingerprint; the client shows the same code.
The user approves or declines it in Settings › Tailscale. Requests expire after two minutes, declined
requests cannot be recreated by polling, and pending requests and approved grants are bounded to 32 and
128 respectively. Pairing never releases the host's master session token.

Approval persists the hash of the client's secret and its node identity in plugin state. The client stores
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
Android connect screen offers Tailscale alongside manual entry and QR scanning. Remembered pairings
bootstrap again on client launch, so host port changes need no manual editing. Disconnecting on Android also forgets that pairing secret. Existing manual and QR
connections keep their current behavior. A live workbench keeps the existing reconnect behavior; after
a host restart changes its listening port, reopening the picker refreshes the endpoint.

All CLI, DNS, filesystem, identity and HTTP work runs off the dispatcher. Discovery and pairing are
cancellable and bounded by timeouts. The plugin's disposer cancels requests, closes bootstrap, clears its
authentication callback and stops any listener it owns before returning.

## Verification

`--tailscale` exercises registry expiry and limits, node binding, credential secrecy and persistence,
actual HTTP discovery and approval against a fake Tailscale identity provider, embedded WebSocket RPC startup,
local/remote status parity, authenticated calls, revocation and plugin teardown. Fixtures never call the
user's Tailscale daemon. Desktop and Android device-to-device operation requires manual verification.

The Tailscale settings page displays a credential-free `sharprail://tailscale?host=…`
QR code for its hostname. Android's existing Scan QR code action opens Tailscale
pairing with that hostname and starts the usual comparison-code/host-approval flow.
The code contains no token or WebSocket RPC port. Existing manual-host QR codes still work.

The plugin list and settings navigation use the official Tailscale nine-dot SVG icon,
staged as a host-served plugin asset. Its fill follows the theme; geometry and opacity
are preserved. Source and trademark attribution are recorded in NOTICE.md.