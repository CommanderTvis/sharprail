# SharpRail browser client

This project compiles the shared Avalonia workbench and native editor/terminal dependencies to WebAssembly.
It connects exclusively to a SharpRail host. See [SPEC.md](SPEC.md) for the parity contract.

The browser uses the pinned restty WebGL2 renderer by default, including shared-terminal
watching. Skia is used only if restty fails to initialize. Query parameters do not select
a renderer. This is a canvas embedding, not the desktop texture-sharing path; remaining
parity limits are listed in the spec.
Run `node tests/SharpRail.Browser.Checks/restty.mjs` after a browser build to check it.
The optional `--profile` runs `term-bench --duration 15 --fps 120` and records browser
draw cadence separately from the benchmark's output FPS under `artifacts/terminal-profile`.

Build with `sh scripts/browser.sh build`. The script installs the checkout-local .NET WebAssembly workload;
native toolchains and source archives stay under `.tools`. Node/npm and uv are required. The deployable
files are in `src/SharpRail.Browser/bin/Release/net10.0-browser/AppBundle`.

To serve it with a standalone host:

```sh
SHARPRAIL_TOKEN='<session token>' SHARPRAIL_ROOT='/absolute/project/path' sh scripts/browser.sh run
```

Open `http://localhost:54123` and enter the token. `SHARPRAIL_PORT` controls the shared endpoint
(default 54123), used by browser, desktop and Android clients.
`SHARPRAIL_BIND` defaults to loopback. The host owns its files, Git, plugin processes, PTYs and shared state.

To connect to the normal desktop app, enable its host listener in Settings and use that listener's
address and session token in the browser connection form. The browser assets can be served separately;
the host address is independent of the page address. Both ends must use host protocol 5 or newer:
an already-running build with the old gRPC listener needs updating and restarting first.

A deployment must support WebSocket upgrades at `/rpc` and preserve `Cross-Origin-Opener-Policy:
same-origin` and `Cross-Origin-Embedder-Policy: require-corp`. Use HTTPS outside loopback for browser
clipboard and notification APIs. The browser keeps its token in session storage and view state in local
storage; Disconnect clears the token. No token is put into a URL.

`sh scripts/browser.sh publish` produces deployment assets under `artifacts/browser`.
The native solution's `--browser` check exercises authentication, unary RPC, subscriptions and duplex PTYs.
`sh scripts/browser.sh check` builds a test-only browser bundle, installs Chromium under `.tools/playwright`
and runs browser acceptance checks against an isolated host. Build or publish again for deployment;
normal bundles exclude the acceptance hooks.

With the test-only bundle built, `node tests/SharpRail.Browser.Checks/restty.mjs --settings`
checks Settings repaint stability, reopening, dismissal and terminal input recovery.
`PROFILE_DPR=2` exercises the same journey at high pixel density.

The acceptance runner also supports `SHARPRAIL_BROWSER_BUNDLE=/absolute/bundle/path node tests/SharpRail.Browser.Checks/check.mjs` for a normal deployment-bundle smoke check.

Release Build and Publish use selective hybrid WASM AOT for the layout/runtime hot paths.
The first native compilation takes longer; later builds reuse its cache. Managed assemblies,
reflection and dynamically loaded plugins are preserved. Debug remains interpreted. For an
interpreted diagnostic build, use
`.tools/dotnet/dotnet build src/SharpRail.Browser -c Release -p:SharpRailBrowserAot=false`.

The measured native runtime grows from 11.61 MB interpreted to 37.90 MB with selective AOT.
Gzip level 9 produces 11.40 MB and Brotli quality 11 produces 7.92 MB, but the standalone host
currently serves the raw files without response compression. These are runtime-file sizes,
not total bundle sizes. Account for the larger cold download when deploying.

After building the test-only bundle, `node tests/SharpRail.Browser.Checks/restty.mjs --prose`
checks the 646 KB prose first-frame budget and exact scroll extents. This optional performance
journey is separate from ordinary browser acceptance.
