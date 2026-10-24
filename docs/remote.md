# Remote hosts and clients

Your files, Git operations and shells run on the host machine. A client gives you the workbench to use them.

## Clients

| Client | Platforms | Notes |
| --- | --- | --- |
| Desktop | macOS, Windows (x64, ARM64), Linux (x64, ARM64) | Works on local projects directly, or connects to a remote host. Each window keeps its own layout. |
| [Android](android.md) | Android 7.0 or later | Connects to a host on another machine. |
| Browser | WebAssembly | Connects to a host too. |

iOS clients are not yet supported. Windows and Linux use Skia terminals. See [Windows](../WINDOWS.md) and
[Linux](../LINUX.md) for setup and validation limits.

## Hosting

There are two ways to run a host.

- In the desktop app, turn on the listener in Settings › Host. Local work keeps using direct calls while it listens for
  remote clients.
- As a standalone host, run `SharpRail.Host.Remote` without a desktop window.

The host keeps its shared state in `SHARPRAIL_STATE_DIR` (default `~/.sharprail/host`) and its terminal sessions until it
stops.

### Try the standalone host

This example runs the host and the app on your own machine, with the same token.

```sh
SHARPRAIL_TOKEN=your-token artifacts/host/SharpRail.Host.Remote
SHARPRAIL_REMOTE=http://127.0.0.1:54123 SHARPRAIL_TOKEN=your-token artifacts/SharpRail.app/Contents/MacOS/SharpRail.UI
```

### Use another machine

1. Set the host's `SHARPRAIL_BIND` to its Tailscale IP.
2. Use that address in `SHARPRAIL_REMOTE` on the client.

Remote connections use cleartext WebSockets and require an encrypted network such as Tailscale. To skip addresses and
tokens entirely, see [Pair over Tailscale](tailscale.md).

## See also

- [Building](develop/building.md) for the desktop run instructions.
- [Android client](android.md).
