# Real language-server integration

This suite is explicitly opt-in. It is not registered in SharpRail.Checks or the full gate.
It launches the standalone host with SHARPRAIL_HEADLESS_MCP=1, sends prewritten MCP calls,
and uses the production language provisioners. No AI process, graphical app or fake LSP is involved.

Build Release first, then run `node tests/SharpRail.Language.Integration/run.mjs --all`.
Use `--server typescript` to select one provider. Project revisions and scenarios live in corpus.json.
Repositories and managed servers are cached under $SHARPRAIL_CACHE_DIR/lsp-validation; reports are written under
artifacts/lsp-validation. Dependencies may be downloaded by project package managers/build importers.
The runner never accepts license agreements. Missing prerequisites or user acceptance are BLOCKED,
not PASS. Nonzero exit status means at least one selected case failed or was blocked.

## Headless host

Set `SHARPRAIL_ROOT` to the absolute project directory, `SHARPRAIL_STATE_DIR` to an
isolated persistent directory, `SHARPRAIL_TOKEN` to a private random value,
`SHARPRAIL_BIND=127.0.0.1`, `SHARPRAIL_PORT=54124`, and `SHARPRAIL_HEADLESS_MCP=1`.
Run `dotnet src/SharpRail.Host.Remote/bin/Release/net10.0/SharpRail.Host.Remote.dll`.
POST JSON-RPC MCP requests to `http://127.0.0.1:54124/mcp` with
`Authorization: Bearer <token>`. `initialize`, `tools/list`, and `tools/call` use the
same three language tools as agent terminals. The root is the default workspace;
authenticated callers have host access and may supply another absolute workspace.
The endpoint is absent unless explicitly enabled. This does not expose agreement
acceptance or fabricate a terminal identity.

## Acceptance and prerequisites

The manifest pins repository commits, source-count floors and exact probe files.
Each case asserts semantic symbols and an actual diagnostic publication. Cases
marked `crossFile` also require a reference outside the queried file. Document-only
providers are explicitly labelled; a PASS there does not establish a project-wide
index. A `protocol` hover at line zero only tests request handling. Unversioned
push diagnostics are retained as unversioned, never described as a clean build.
Reports preserve calls, coverage, runtime output, provider catalog version and logs.

Build preparation is explicit in `prepare`; it runs only in disposable downloaded
projects. Swift needs `swift build` to populate its index; clangd uses CMake's
compilation database. The C# corpus uses Humanizer 3.0.10 with .NET 10 (its later
main branch requires a .NET 11 preview). Kotlin uses a JVM Gradle project; Kotlin
Multiplatform support is not assumed. Native package dependencies may still block
Ruby/R or build import. Inspect reports instead of treating startup as success.

The standalone server versions are pinned in `LanguageServers.Catalog`. Dart and
SourceKit-LSP come from the selected host SDK and retain that SDK's version policy;
they are not independently downloadable pinned servers. `runtime-pins.json`
records publisher URLs and SHA-256 for the optional macOS ARM64 Go, Coursier,
PowerShell and Dart runtimes used for validation. Install these into a disposable
local tool directory and put their `bin` paths on PATH; the runner does not change
the machine's SDK selection. The JDK is the repository's pinned Temurin 21
(`scripts/tool jdk`); the runner passes it to the
host as JAVA_HOME and ignores any machine JDK manager. Set JAVA_HOME to run against
another JDK. IntelliJ/Kotlin's project JDK is separate from its bundled server runtime. No IntelliJ acceptance record is created or imported by this suite.

`node tests/SharpRail.Language.Integration/check.mjs` validates the manifest without
network, server launch or downloads. Diagnostic-probe cases temporarily append a
known syntax error to their downloaded fixture file and restore the original bytes
in `finally`; this verifies an actual analyzer response when a provider does not
publish empty diagnostics. Do not point the runner at a working project. The runner
owns its downloaded copies only. Swift preparation disables the shared dependency
cache to avoid stale empty repository entries; it does not delete user caches.
Bundler setup is confined to the fixture's `.bundle/packages` and the suite's
local bundle/spec caches; it must not install gems into the host's system Ruby.

Swift and Scala may begin indexing after their first navigation request. Their
cross-file probes poll for the expected external-file reference for up to 15
minutes; they do not convert an empty or initializing result into a pass. Polling
is confined to the opt-in runner. Providers' production responses retain their
reported freshness and project coverage.

See [RESULTS.md](RESULTS.md) for the recorded run and explicit coverage limits.

## Shared semantic caches

`node tests/SharpRail.Language.Integration/shared-cache.mjs --inventory` classifies all
18 catalog servers before a cache claim is made. It distinguishes a semantic index from
downloads, compiler artifacts and process memory. The native opt-in probes use two Git
worktrees on different branches, warm one workspace, restart in the other and compare
cache records against an independently cold run. They also prove branch diagnostics and
post-edit invalidation. Run one or more with:

```sh
node tests/SharpRail.Language.Integration/shared-cache.mjs --server go,dart
```

The probes require an already built Release host. Dart additionally requires
`SHARPRAIL_LSP_COMMAND_DART` to name the exact provider executable. Kotlin and Metals
are deliberately marked `manual`: their native shared-index stores require a compatible
imported Gradle/Maven or BSP project respectively, and a package download or Coursier
artifact must never turn them into a passing semantic-cache result. clangd is also
`manual`: the shipped 23.1.0 background-index run resolves a common external header but
logs a fresh branch shard build, so it cannot currently prove a cross-workspace hit.
