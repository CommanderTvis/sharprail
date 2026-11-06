---
id: module-plugin-isolation
type: module-design
status: active
title: Plugin isolation channel — the framed channel and the runner process between the host and an external plugin
parent: module-plugin-security
references: [module-wire]
tags: [plugins, security]
---

# Plugin isolation channel

## Responsibility

The trusted side's half of the isolated runtime that does not depend on what a plugin is: the channel, the handles
lent across it, and the process at its other end. [Security.SPEC.md](../SharpRail.Plugins.Api/Security.SPEC.md) under
"The channel and the runner process" states the protocol and the process rules; this project implements them.

- `IsoFrame` and `IsoCodec.Generated.cs` (from `scripts/generate-rpc.py`), `IsoWire` (framing and limits).
- `FdTransport`: the framed stream and its descriptors, a Unix socket with SCM_RIGHTS or a Windows named pipe with
  handles duplicated into the runner. `FdTransport.Loopback` gives the checks a host end and a raw stream.
- `IsolationPeer`: one end of the channel, the same code on both sides (`IsolationRole` decides id parity and
  whether descriptors are accepted). `HandleTable` holds what this side lent, with tombstones.
- `ChildProcess`: spawn with exactly the channel, null standard input and a rate-limited log pipe; exit watch;
  reaping; kill of the session's group when the runner exits or is killed, or of the job. `RunnerSession`: the host's hold on one runner, with the handshake, and the
  kill on a protocol violation.
- `RunnerProbe`: the member ids of the runner's `probe echo` mode.

## Boundary

- References `SharpRail.Wire` only, so no Avalonia and no host. Its internals are visible to the checks, which drive
  the Unix transport directly. Host.Core will compose it behind
  `PluginHostSeams.Isolation`; nothing in a shipping composition uses it yet.
- NativeAOT code: no reflection; the handshake records go through a source-generated JSON context.
- Pending, in the design's terms: generated proxies and member tables (slice 3), the host-mode and UI-mode runner
  (slices 4 and 14), grants and the broker (slices 10 to 13), sandboxes (slices 7 to 9), packaging (slice 6).

## Checks

`--plugin-channel` (also a gate case) covers the codec, protocol violations against the loopback, descriptors sent
beside an unfinished frame on the Unix transport, and the runner's `probe echo` and `probe avalonia-private` modes.
