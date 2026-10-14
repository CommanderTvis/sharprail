# Windows host and desktop client

The [nightly prerelease](https://github.com/CommanderTvis/sharprail/releases/tag/nightly)
includes `SharpRail-nightly-win-x64.zip`, built on Windows and gated by the packaged
`--windows` checks. Extract the ZIP and launch `client/SharpRail.UI.exe`; the
standalone host is `host/SharpRail.Host.Remote.exe`. The package is unsigned.
The nightly workflow publishes Windows, macOS and Android together only after all
three builds pass, so a failed Windows gate leaves the previous nightly available.

The Windows target is Windows 10 (1809 or later) / Windows 11, x64. Install Git for
Windows and keep `git.exe` on PATH. The package includes .NET; source builds use
the SDK pinned in `global.json`.

## Run a package

Extract `SharpRail-win-x64.zip`. Run `client/SharpRail.UI.exe` to use local projects
and the embedded host. Windows terminals always use Skia, including when a profile
has a saved Metal preference.

Shells use `pwsh.exe` from PATH when available, otherwise Windows PowerShell.
An existing executable named by `SHELL` overrides that choice. Shells start in
the workspace and load their usual profile. Closing a window detaches its views;
closing a terminal tab ends its shell and child processes. Exiting the embedded
host ends its shells.

To run a standalone host, open PowerShell in the extracted folder:

```powershell
$env:SHARPRAIL_ROOT = 'C:\code\project'
$env:SHARPRAIL_TOKEN = 'replace-with-your-host-token'
$env:SHARPRAIL_BIND = '127.0.0.1'
$env:SHARPRAIL_PORT = '54123'
.\host\SharpRail.Host.Remote.exe
```

Use the host's reachable interface address when connecting from another machine.
The existing authenticated WebSocket transport and Settings > Host listener work
the same way on Windows. A Windows desktop client can connect to a Windows,
macOS or Linux host. In a separate PowerShell session:

```powershell
$env:SHARPRAIL_REMOTE = 'http://127.0.0.1:54123'
$env:SHARPRAIL_TOKEN = 'replace-with-your-host-token'
.\client\SharpRail.UI.exe
```

Remove `SHARPRAIL_REMOTE` from that session to return to embedded hosting. Remote
project paths name folders on the host, regardless of the client's OS. Local app
state defaults to `%USERPROFILE%\.sharprail`; the standalone host's default state
directory is `%USERPROFILE%\.sharprail\host`.

## Build and check

Run from the checkout in PowerShell. Git, PowerShell and Windows' `tar.exe` are
required; Zig and native sources are downloaded into checkout caches with pinned
hashes/revisions. No Visual Studio C++ installation is needed.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/bootstrap.ps1
.\.tools\dotnet\dotnet.exe build SharpRail.slnx -c Release
.\.tools\dotnet\dotnet.exe format SharpRail.slnx --verify-no-changes --no-restore
.\.tools\dotnet\dotnet.exe run --project tests/SharpRail.Checks -c Release --no-build -- --windows
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish-windows.ps1
```

The package is `artifacts/SharpRail-win-x64.zip`; unpacked client, host and checks
are under `artifacts/windows`. Stop executables running from that directory before
publishing again. CI builds the package on Windows and runs its `--windows` checks.
The check mode covers local/remote terminals, Unicode, resize, reconnect, watching,
descendant cleanup, file saving and recycling, Git/worktrees, native Skia rendering,
editor shortcuts and portable profile paths.

## Platform limits

Metal rendering, native Mermaid rendering and macOS desktop notifications are
unavailable on Windows. The existing Mermaid source fallback remains available.
Windows terminal busy detection conservatively includes background child
processes. Shell builtins run in the shell process and are not detected as busy.
Terminal file links resolve absolute paths; relative links require process CWD
discovery, which the Windows backend does not provide. Unix-only agent process
discovery and the Codex Unix IDE socket retain their existing platform limits.

Cross-compilation and macOS checks do not establish Windows runtime acceptance.
See `VALIDATION.md` for checks actually executed and outstanding Windows gates.
