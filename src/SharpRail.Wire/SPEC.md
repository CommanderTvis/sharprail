---
id: module-wire
type: module-design
status: active
title: Wire primitives — the protobuf writer and reader the generated codecs share
parent: architecture
references: [module-plugin-security]
tags: [protocol, plugins]
---

# Wire primitives

## Responsibility

The hand-written protobuf primitives behind every generated codec: `ProtoWriter` (a size-limited, measuring or
streaming writer), `ProtoReader`, the per-type registry `RpcMessage<T>` that a generated codec fills, and the
`[ProtoContract]`/`[ProtoMember]` attributes that mark a message and its fields. The encoding rules are the ones the
Host.Protocol SPEC lists under Payload codec.

## Boundary

- Everything but the two attributes is internal. Only `SharpRail.Host.Protocol` (the client↔host RPC) and
  `SharpRail.Plugins.Isolation` (the host↔plugin-runner channel) see it, through `InternalsVisibleTo`. It adds no
  public surface beyond the attributes, which moved here from Host.Protocol.
- The primitives know no message, service or exception of either user. A writer is given its byte limit and the
  exception to throw past it, so the RPC still fails with `ResourceExhausted` and the plugin channel with its own error.
- No dependencies. No reflection and no dynamic code: it is NativeAOT code like the host.

## Codecs

`scripts/generate-rpc.py` emits one codec per root, from the `[ProtoContract]` classes in that project's directory:
`src/SharpRail.Host.Protocol/RpcCodec.Generated.cs` and `src/SharpRail.Plugins.Isolation/IsoCodec.Generated.cs`.
Repeated `long` fields (unpacked, one field per item) are a shape only the plugin channel uses. The move from
Host.Protocol kept the RPC bytes: the regenerated RPC codec differs only by its `using`, and `--rpc-codec` passes
against the protobuf-net golden records without `--update`.
