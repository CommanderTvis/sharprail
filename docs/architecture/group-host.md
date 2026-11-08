---
id: group-host
type: architecture-design
status: active
title: Host — the machine-side services every client talks to
parent: architecture
---

# Host

The host owns projects, files, Git, shared settings and shells. This group holds the public host surface, the host core with its parts, and the wire primitives the remote path shares. Clients and plugins sit beside it, not under it.
