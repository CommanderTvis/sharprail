# tereditor browser client

This project compiles the shared Avalonia workbench and native editor/terminal dependencies to WebAssembly.
It connects exclusively to a tereditor host. See [SPEC.md](SPEC.md) for the parity contract.

The browser uses the pinned restty WebGL2 renderer by default, including shared-terminal
watching. Skia is used only if restty fails to initialize. Query parameters do not select
a renderer. This is a canvas embedding, not the desktop texture-sharing path; remaining
parity limits are listed in the spec.
Run `node tests/Tereditor.Browser.Checks/restty.mjs` after a browser build to check it.
The optional `--profile` runs `term-bench --duration 15 --fps 120` and records browser
draw cadence separately from the benchmark's output FPS under `artifacts/terminal-profile`.

Build with `scripts/mise run browser build`. The script installs the checkout-local .NET WebAssembly workload;
native sources, caches and built libraries stay in the machine-wide `$TEREDITOR_CACHE_DIR/wasm-native`, copied into `obj/wasm-native`. Node and pnpm come from `mise.browser.toml`; JavaScript dependencies are pinned by `pnpm-lock.yaml` and PDFium by checksum there, and both are inputs of the asset-staging target. The deployable
files are in `src/Tereditor.Browser/bin/Release/net10.0-browser/AppBundle`.

To serve it with a standalone host:

```sh
TEREDITOR_TOKEN='<session token>' TEREDITOR_ROOT='/absolute/project/path' scripts/mise run browser run
```

Open `http://localhost:54123` and enter that address and the token. `TEREDITOR_PORT` controls the shared endpoint
(default 54123), used by browser, desktop and Android clients.
`TEREDITOR_BIND` defaults to loopback. The host owns its files, Git, plugin processes, PTYs and shared state.

To connect to the normal desktop app, enable its host listener in Settings and use that listener's
address in the browser connection form. When the browser page and host both use loopback addresses, leave the token blank and
confirm the displayed browser origin in the app’s Allow/Deny prompt. The browser remembers
that approval for its session; Settings › Host can revoke it, and stopping/restarting the listener
expires it. Nonlocal connections and standalone hosts still use a session token. The browser assets can be served separately;
the host address is independent of the page address. Both ends must use host protocol 5 or newer:
an already-running build with the old gRPC listener needs updating and restarting first.

After a test-only Release browser build, `node tests/Tereditor.Browser.Checks/local-approval.mjs`
checks the real browser’s blank-token consent, deny/cancel/allow, reload, revocation, restart and
remote-token behavior against an isolated embedded app. Build the solution first for its
headless app fixture. The focused command
`dotnet run --project tests/Tereditor.Checks -c Release -- --local-browser-approval`
covers the host boundary and headless app approval/Settings interactions.

A deployment must support WebSocket upgrades at `/rpc` and preserve `Cross-Origin-Opener-Policy:
same-origin` and `Cross-Origin-Embedder-Policy: require-corp`. Use HTTPS outside loopback for browser
clipboard and notification APIs. The browser keeps its token in session storage and view state in local
storage; Disconnect clears the token. No token is put into a URL.

GitHub Pages: the `Pages` workflow (`.github/workflows/pages.yml`, dispatched by hand) publishes the bundle to
`https://commandertvis.github.io/tereditor/` as the host-less Playground. Pages cannot set response headers, so the
workflow adds `coi.js` and `coi-sw.js` from `src/Tereditor.Browser/pages`: the first visit registers a service worker and
reloads once, after which every response carries the `Cross-Origin-Opener-Policy` and `Cross-Origin-Embedder-Policy`
headers multithreaded WebAssembly needs. It also drops the pre-compressed `.br`/`.gz` sidecars (Pages compresses itself),
adds `.nojekyll`, and a `404.html` that sends unknown paths back to `/tereditor/`. All client URLs are relative, so the
`/tereditor/` base path needs no rewriting. Pages must be set to the GitHub Actions source in the repository settings.

`scripts/mise run browser publish` produces deployment assets under `artifacts/browser`.
The native solution's `--browser` check exercises authentication, unary RPC, subscriptions and duplex PTYs.
`scripts/mise run browser check` builds a test-only browser bundle, installs Chromium under `$TEREDITOR_CACHE_DIR/playwright`
and runs browser acceptance checks against an isolated host. Build or publish again for deployment;
normal bundles exclude the acceptance hooks.

With the test-only bundle built, `node tests/Tereditor.Browser.Checks/restty.mjs --settings`
checks Settings repaint stability, reopening, dismissal and terminal input recovery.
`PROFILE_DPR=2` exercises the same journey at high pixel density.

The acceptance runner also supports `TEREDITOR_BROWSER_BUNDLE=/absolute/bundle/path node tests/Tereditor.Browser.Checks/check.mjs` for a normal deployment-bundle smoke check.

Release Build and Publish use selective hybrid WASM AOT for the runtime, layout, drawing, editor,
Markdown, highlighting and RPC hot paths (the list and its reasons are in [SPEC.md](SPEC.md)).
The first native compilation takes longer; later builds reuse its cache. Managed assemblies,
reflection and dynamically loaded plugins are preserved. Debug remains interpreted. For an
interpreted diagnostic build, use
`dotnet build src/Tereditor.Browser -c Release -p:TereditorBrowserAot=false`.

The measured native runtime grows from 11.61 MB interpreted to 61.9 MB with selective AOT
(12.4 MB Brotli).
Release bundles now include precompressed `.br` and `.gz` sidecars. The standalone host
negotiates these at the original URLs using `Accept-Encoding`, retaining the original MIME type,
COOP/COEP headers and conditional caching. Uncompressed bundles and clients remain supported.
These historical measurements cover the runtime file, not the total bundle or startup latency.
Compression runs after Release Build and Publish, including the `browser` task's `run`; unchanged sidecars
are reused from a content-addressed cache under `$TEREDITOR_CACHE_DIR/browser-compression`, even when the SDK
regenerates the bundle. Quality 11 Brotli adds initial bundling time and disk space, with no request-time
compression work. Separate static servers must configure sidecar negotiation themselves.
Run `dotnet run --file tests/Tereditor.Browser.Checks/compression.cs` for the bundler regression checks; `scripts/mise run browser check` runs them first.

After building the test-only bundle, `node tests/Tereditor.Browser.Checks/restty.mjs --prose`
checks the 646 KB prose first-frame budget and exact scroll extents. This optional performance
journey is separate from ordinary browser acceptance.
