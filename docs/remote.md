# Remote hosts and clients

Your files, Git operations and shells run on the host machine. A client gives you the workbench to use them.

## Clients

| Client | Platforms | Notes |
| --- | --- | --- |
| Desktop | macOS, Windows (x64, ARM64), Linux (x64, ARM64) | Works on local projects directly, or connects to a remote host. Each window keeps its own layout. |
| [Android](android.md) | Android 7.0 or later | Connects to a host on another machine. |
| Browser | WebAssembly | Connects to a host too. |

iOS clients are not yet supported. Windows and Linux use Skia terminals. See [Windows](windows.md) and
[Linux](linux.md) for setup and platform limits.

## Hosting

There are two ways to run a host.

- In the desktop app, turn on the listener in Settings › Host. Local work keeps using direct calls while it listens for
  remote clients.
- As a standalone host, run `Tereditor.Host.Remote` without a desktop window.

The host keeps its shared state in `TEREDITOR_STATE_DIR` (default `~/.tereditor/host`) and its terminal sessions until it
stops.

### Try the standalone host

This example runs the host and the app on your own machine, with the same token.

```sh
TEREDITOR_TOKEN=your-token artifacts/host/Tereditor.Host.Remote
TEREDITOR_REMOTE=http://127.0.0.1:54123 TEREDITOR_TOKEN=your-token artifacts/tereditor.app/Contents/MacOS/Tereditor.UI
```

### Use another machine

1. Set the host's `TEREDITOR_BIND` to its Tailscale IP.
2. Use that address in `TEREDITOR_REMOTE` on the client.

Remote connections use cleartext WebSockets and require an encrypted network such as Tailscale. To skip addresses and
tokens entirely, see [Pair over Tailscale](tailscale.md).

## See also

- [Building](develop/building.md) for the desktop run instructions.
- [Android client](android.md).
