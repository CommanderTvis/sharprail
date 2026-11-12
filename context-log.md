# Continuation log

Recent task outcomes that a later session may need to continue: what landed, what was checked, and what was not.
Durable facts belong in the owning module `SPEC.md` files and open work in [STATUS.md](STATUS.md); this file is not
a second contract and code is the source of truth.

Rules for adding to it: one short entry per landed task (heading `## YYYY-MM-DD — topic`, at most about ten lines:
what changed, checks run, what was not verified). Keep roughly the last twenty entries; when adding one, delete the
oldest, and delete any entry whose facts have moved into a spec or STATUS.md or that a later entry supersedes. No
build logs, step-by-step progress or rebase notes.

## History before 2026-10-26 (summary)

The full chronological log is in Git history (`git log -p -- context-log.md`). In short: September 2026 built the
Avalonia prototype over the transport-independent host and ported the upstream docking, layout and E2E behaviour;
the separate validation ledger was retired on 2026-10-15 with its durable facts moved into module specs and
STATUS.md. October added language servers (later each its own builtin plugin through Plugin API H21), the optional
IntelliJ provider with explicit terms, embedded Air Context (enabled per project), the Linux x64 and arm64 AppImage
port (STATUS.md V13), embedded Tailscale for Android and the browser (real tailnet sign-in still unverified), the
Specs and Blueprint builtins switched off by default, and workspace switching without the loading flash.

## 2026-10-26 — Claude Code chat mode (branch claude-chat-mode)

Claude Code can now run as a chat: the plugin's Mode setting (Terminal or Chat, Settings › Claude Code › Presentation)
chooses how a tereditor launch is shown. Chat mode spawns the CLI headless over stream-json and draws the conversation in a
document tab of the editor area, built on a new plugin API for plugin-owned document tabs (W26: `Document`, `OpenDocumentAsync`,
`Closed`, `OpenTextDiffAsync`, an optional `AgentLauncher.Open`). The semantics follow the official VS Code extension's chat
(message origins, no per-turn result rows, permission and question panels above the composer, tool tables, compaction, queued
prompts, rewind and fork), recovered from its bundle and recorded in the plugin SPEC.md; the look follows ThinkRail's chat.
The list is drawn and virtualized (20,000 rows realize under 60), with its own height estimates, a reading band, text selection,
hit-rect hover, per-chat zoom and an adaptive footer. Sessions revive from the transcript with `--resume`. Visualizations made from
a chat open as their own editor tab. A chat child cannot reach the IDE bridge (probed against CLI 2.1.310 in `-p` mode), so diffs
open from the tool input and the IDE context is an explicit attachment. Effort changes live through `apply_flag_settings`.

Verification at tip a5ec58187 plus the Android and Browser constructor fix: whole-solution `scripts/mise run format` clean; a full
no-incremental `dotnet build Tereditor.slnx -c Release` with 0 warnings and 0 errors; `scripts/mise run browser` and
`scripts/mise run android` exit 0 without warnings; `--claude-chat-ui`, `--claude-chat-e2e`, `--claude-chat-host` and `--claude-code`
pass; one run of every gated case except `WelcomeE2E` passed with 717 checks. `WelcomeE2E` times out in `InitialiseFromWelcome`
on main itself (acf592d6e) and on this branch, so the argument-free suite is red for a reason that is not this branch. Two
chat checks (footer density) failed only in full runs because they measured text widths that differ by process state; the
last fix is test-only and its failure was not reproduced, so a repeat would mean it is not fixed.

Not verified: nothing was seen in a real window (all rendering checks are headless renders); the real macOS pasteboard
(image paste), a trackpad pinch, and whether the page-zoom and drag-resize flicker are gone; hover over links inside reply
blocks; the popups inside their real popup hosts; `lsp_diagnostics` and discovery lookups were thin on several agent commits.
Open gaps: the session list assertion in the host check is weak; the Edit card's "Modified" rule and the extension's visible
streaming word are not recovered; the chat has no rename, cross-chat history or mid-turn steering because the contract has none.

## 2026-10-27 — GitHub plugin and repo hosting providers

Pull requests and GitHub sign-in moved out of the host into the builtin `GitHub` plugin ([spec](src/Tereditor.Plugins.GitHub/SPEC.md)): the host routes by `origin` URL to the first active `RepoHostProvider` (H24), the plugin's UI half contributes the Pull request strip item, the Review section and the Settings page (W28, W4), and the wire is protocol 15 with the neutral `PullRequest`. Verified: build, format, `--github`, `--branch-status`, `--review`, `--settings`, `--shell`, `--design`, `--plugins`, `--conformance`, `--reconnect`, `--state`, Android and browser builds. Open: the merge choice is still in the UI profile; no registry-level plugin-off check in `--plugins`.

## 2026-10-31 — Processes plugin

The builtin `Processes` plugin ([spec](src/Tereditor.Plugins.Processes/SPEC.md)) adds a Processes side tool (language servers, AI agents and daemons, spawned agents; CPU, memory, 30-sample trend; Stop selected and Stop group) and owns the agent-facing owned-process management of #52: the `owned_process_report`/`owned_process_release` tools and prompt section moved out of core into it, and `OwnedProcesses` (still core) stays inert until the plugin switches it on through H27. Disabled, nothing is sampled, registered, swept or stopped on close or quit. Verified with `--processes`, `--owned-processes`, `--plugins`, `--conformance`, `--design` and the Release build; not seen rendered.

## 2026-11-02 — IntelliJ released build and license flow

The bundled IntelliJ server moved from the expired EAP build to JetBrains' released, licensed build 263.7084.0 (released 2026-10-14; the six archive SHA-256 values match the catalog and the CDN `.sha256` files). The EAP framing, the date countdown and the exit-7 refusal with its `.expired` marker are gone; an old `263.6379.0-<platform>` directory and its marker stay on disk, unused. Language-server plugins can now publish a JSON state, hold language answers with a reason, run named actions and ask single-choice questions with their terms (Plugin API H21); the IntelliJ plugin uses them for JetBrains' `jetbrains/licensing/*` protocol (trial, browser sign-in, activation code, license server, remove, refresh), the unlicensed message for the settings and agents, and the region and data-sharing answers passed to the launch. Checks: `--intellij-license` (fake licensing server), `--intellij-license-ui`, `--lsp`. Not exercised: the real server, a real sign-in or trial, and the real license store, which only the user may touch.

## 2026-10-27: Pi chat, shared chat view, Model providers

Extracted the Claude Code chat's wire records and drawn view into `Tereditor.Plugins.Chat` and `Tereditor.Plugins.Chat.UI` (a plugin
describes itself to the view with a `ChatAgent`), added the Pi plugin (chat over `pi --mode rpc`) and the Model providers plugin (API keys
for Pi's `auth.json` and omp's profile `.env`), and gave manifests `UnavailableOn` so plugins that need an operating system opt out of a pure
web host (the eleven process-bound builtins now do). Checks run: `--claude-chat-ui`, `--claude-chat-host`, `--claude-code`, `--pi`,
`--providers`, `--plugins`, `--conformance`. Not run: the full suite, `--claude-chat-e2e`, the Browser and Android builds, any real `pi`
(the checks use a fixture speaking the RPC protocol), and the Model providers page (no UI check). No pure web host exists yet, so
`UnavailableOn` has nothing to refuse in practice. Claude chat copy changed in three places: "Assistant" replaces "Claude" in the
accessibility text and history filter, and "Ran at once, while the assistant was working.".

## 2026-11-05 — External plugins discovered and refused (branch nativeaot)

The host and the app no longer load external plugin assemblies: the host's load context, the app's external UI
loader and the roster's UI assembly field are gone. External plugins are still discovered, listed and validated;
switching one on refuses it with "External plugins are not supported in this version." The fixture plugin is now a
check-only builtin the checks compose on both sides. Checks run: `--plugins`, `--plugins-settings`, `--conformance`.
Not run: the full suite and the browser check (`tests/Tereditor.Browser.Checks/check.mjs`, retargeted to the Branch
graph and Epub builtins plus an external refusal; only `node --check` was run).

## 2026-11-06 — NativeAOT packages (branch nativeaot)

Every package now ships the desktop app and the remote host as NativeAOT binaries (`NativeAot.props`): macOS links
with Xcode's clang, Windows with the MSVC linker, Linux with the pinned Zig against glibc 2.31
(`scripts/aot-linux-cc.sh`). The SDK pin moved to 10.0.403 (ILCompiler 10.0.14, past the advisory). The packaged gate
checks the payloads are NativeAOT, starts the shipped host and, on Linux, the shipped app as a terminal relay
(`--packaged`, also in `--linux`/`--windows`); ci.yml gained a macOS package job. Sizes: dmg 123 → 61 MiB.
Checks run: build, format, `--runner`, `--gc-settings`, `--linux`, `--packaged`, `--case boundaries`, perf
self-test, `package:macos` with `check:packaged -- --linux`, a linux-x64 AppDir cross-build, and a linux-arm64 host
and app relay in an Ubuntu 20.04 container. Not run: the AOT app with a window on any OS, the Windows package
(MSVC link, ps1 changes), the Android and Browser builds on the new SDK, `perf diagnose`, the full suite.

## 2026-11-06 — NativeAOT packaging review (branch nativeaot)

An independent review of the two NativeAOT packaging commits. Fixed: the packaged gate now requires each payload to
carry the native libraries, helpers and `plugins`/`shell-integration`/`terminfo` trees the checks' own publish has
for it (`TEREDITOR_PACKAGED_CHECKS`), since the staging filled gaps with the checks' copies; the glibc 2.31 floor
covers every ELF file in a Linux payload; the Codex and Claude Code process-group helpers compile on Linux through
`scripts/aot-linux-cc.sh` (they were Mach-O in a macOS cross-build and would need `GLIBC_2.34` from a runner's `cc`);
`Microsoft.NET.WebAssembly.Threading` follows the SDK's runtime to 10.0.14. Verified unchanged: the SDK archive
checksums, Visual Studio's ARM64 C++ tools on `windows-11-arm` (probe log), the locks. Checks run: build, format,
`check:packaged -- --packaged` on macOS (and failing on a payload without a library, a plugin or terminfo), the
payload rule on the cross-built Linux payload, a Debug browser build. Not run: Windows and Linux packages, Android.
