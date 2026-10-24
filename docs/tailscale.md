# Pair over Tailscale

Pair a client with a host on the same tailnet without copying IP addresses, ports or session tokens.

## What you need

- A Tailscale account.
- On the host, the Tailscale CLI and TCP port 54124 available.
- On a desktop client, the machine connected to Tailscale.
- On Android and in the browser, nothing else. These clients run their own embedded Tailscale node, so there is no
  Tailscale app or system VPN. The first connection opens a browser to sign in.
- A tailnet policy that allows bootstrap and the host's WebSocket RPC listener.

## Pair

1. On the host, enable Tailscale in Settings › Plugins, then open Settings › Tailscale.
2. On the client, find the host.
   - Desktop. Choose Settings › Host › Connect to a Tailscale host (or launch with `--tailscale`) and click Find hosts.
   - Android. Choose Connect over Tailscale and enter the host's MagicDNS name shown on the host.
3. Compare the pairing code and approve the device on the host.

Later launches remember the pairing.

## Manage

- Revoke a device in Settings › Tailscale on the host.
- A standalone host must bind its listener to its Tailscale address.
