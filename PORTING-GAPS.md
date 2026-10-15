# ThinkRail porting and UI gaps

SharpRail has the workspace, editor, Git, terminal and plugin foundations. This is the current cross-module backlog for applicable ThinkRail ports and Claude Code/Codex terminal workflows, including implementation and acceptance gaps. Module specs define the contracts; UPSTREAM.md records port history, COMPLETION.md records the completion criteria, VALIDATION.md records evidence and E2E.md inventories translated scenarios. Those documents do not maintain a second current gap list.

Embedded chat, pi execution and their transcript, composer, session-resource and provider-service internals are outside this backlog. Retain shared workbench behavior and capabilities deliverable through the supported CLI plugins, configuration, hooks, MCP tools or shipped skills.

## Comparison boundaries

- Research date: 2026-10-14.
- Original research baseline: `main` at `16138700b2fbf86d845ad76a133dacb7d6ab1d5c`, copied to `audit-thinkrail-gaps`. Other workspaces and the original checkout's uncommitted `scripts/browser.sh` are outside this baseline.
- Latest researched JetBrains commit: [`be06fb01927e8386e3aec215bbcdb1a312ec66fa`](https://github.com/JetBrains/thinkrail/commit/be06fb01927e8386e3aec215bbcdb1a312ec66fa), “feat(web): list newest workspaces first in the projects panel (#725)”, dated 2026-10-14. The local upstream ref was fetched from JetBrains for this comparison.
- Last recorded specification sync on this SharpRail line remains `f7cca7bfb0b1d0543eb9898c52d28df5a784eb09`. Researching a newer revision does not advance implementation or specification-sync coverage.
- `main` ports CommanderTvis's fork; the separate `upstream` branch ports JetBrains. No code was carried between those lines. Fork history and its older omissions use [UPSTREAM.md](UPSTREAM.md); its separately tracked tip was not refreshed in this task.
- Coverage includes the 475-commit JetBrains history through this revision, its 98 `SPEC.md` paths and E2E scenario catalogue as discovery surfaces, the 22 commits after the recorded sync, SharpRail's module gap sections, [COMPLETION.md](COMPLETION.md), [E2E.md](E2E.md), recent continuation records and relevant implementation paths. Feature families are grouped below rather than counting every implementation detail as a separate product feature.

The current rows incorporate the workspace-13 integration and newer main fixes; the dated research baseline remains provenance. Implemented rows are recorded below rather than retained as missing features.

Feature statuses: `Missing` means the surface or operation is absent; `Partial` means a narrower counterpart exists; `Decision` means an applicable capability needs a scope or integration decision. Feature tables contain implementation differences. Passed checks are recorded in [VALIDATION.md](VALIDATION.md); remaining acceptance work is consolidated once under [Outstanding acceptance work](#outstanding-acceptance-work), with explicit failed or not-run results.

## Implemented since the research baseline

| Original IDs | Implementation |
| --- | --- |
| A33, A34 | Four shared CLI workflow skills: interview/bug brainstorming, questions, routing and spec-graph guidance. |
| H11 | Spec traversal and authoring exclude the five assistant/mockup scratch folders. |
| P13 | Terminal companions start with the fork's 45% share. |
| L01–L05, L09 | Persistent edge rails, compatible tool/terminal panes, placement/drop rules and independent Project Home visibility. The bottom zone remains retired. |
| W01–W07, W09 | Host activity, settling overrides/settings, client partition/latch, shelf/paging/notice and differentiated creation/activity ordering. |
| U02, U07, G01 | Semantic typography, quiet scrollbars/curtains and the calm 40px location strip. Minimal palettes are included under the narrowed U01 entry. |
| V08 | Browser Settings renderer ownership is already fixed; normal-bundle and physical-browser acceptance remains V07. |
| W13 | Intentional divergence: workspaces start empty and terminals launch explicitly. Initial-terminal provisioning is no longer a product requirement. |

These dispositions do not close native appearance, physical-device, complete packaged-suite or browser-engine acceptance. See VALIDATION.md for the applicable source and combined checks.

## Changes since the recorded JetBrains sync

Applicable commits below are linked to their immutable source diffs. Chat/pi-only changes are omitted; mixed commits retain only their applicable parts. Later commits supersede earlier ordering requirements.

| Commit | Change and SharpRail disposition |
| --- | --- |
| [1ab1b1e31](https://github.com/JetBrains/thinkrail/commit/1ab1b1e31) | Denser named layout tokens and tighter line heights. Named dense layout tokens and semantic typography are now implemented; remaining surface fidelity is tracked under U03 and V05. |
| [951863595](https://github.com/JetBrains/thinkrail/commit/951863595) | Explain and pass project trust for skills, extensions, settings and MCP. Provider-specific configuration/trust exists; no common pre-launch project-resource trust summary. |
| [dedfc1bac](https://github.com/JetBrains/thinkrail/commit/dedfc1bac) | OS window focus suppresses desktop notifications. SharpRail already uses app-window focus; live signed-bundle behavior still needs verification. |
| [9d6ad0b2f](https://github.com/JetBrains/thinkrail/commit/9d6ad0b2f) | Calmer captioned location bar. The calm 40px location strip is implemented; branch headlines and broader PR treatment remain separate gaps. |
| [1166af01a](https://github.com/JetBrains/thinkrail/commit/1166af01a) | Login environment only for launchers opting in. SharpRail lacks the equivalent bounded host environment repair/import. |
| [530dffdb1](https://github.com/JetBrains/thinkrail/commit/530dffdb1) | Stable rail order, recent-activity topbar switcher. Superseded in part by newest-created-first at the tip; Activity stamps and differentiated rail/switcher ordering are implemented. |
| [e04b2681f](https://github.com/JetBrains/thinkrail/commit/e04b2681f) | Shell environment probe must not pollute history. The opted-in environment probe is absent; pi background-completion cleanup is excluded. |
| [bf4da6614](https://github.com/JetBrains/thinkrail/commit/bf4da6614) | No unread-result attention while background children still work. SharpRail has terminal status marks, not this normalized parent/child attention model. |
| [ba8d9d898](https://github.com/JetBrains/thinkrail/commit/ba8d9d898) | Restore design interview and durable spec amendment workflow. The shared CLI bundle now ships brainstorming, question, routing and spec-graph workflows. |
| [e5eeebffe](https://github.com/JetBrains/thinkrail/commit/e5eeebffe) | MCP Settings editor, presets, admission, sign-in and diagnostics. Claude configuration provides part of this; no common cross-agent manager. |
| [5ab283a35](https://github.com/JetBrains/thinkrail/commit/5ab283a35) | Show rejected design alternatives and tradeoffs. Rejected alternatives and durable spec-amendment guidance now ship in the brainstorming workflow. |
| [ffc934fa4](https://github.com/JetBrains/thinkrail/commit/ffc934fa4) | Background work keeps running treatment; seen results clear owner-wide unread state. No equivalent SharpRail lifecycle/read watermark across clients. |
| [be06fb019](https://github.com/JetBrains/thinkrail/commit/be06fb019) | Rail workspaces newest-created-first with Default pinned, switcher still recent-activity-first. The rail now pins Default and reverses registry creation order; the switcher orders by activity. |

The researched range contains 22 newer commits; this table lists only applicable changes.

## Agent workflow gaps for Codex and Claude Code

These gaps concern the supported terminal workflows. Provider-owned adapters and host-neutral contracts may be needed; Decision entries remain proposals rather than accepted feature designs. CLI-owned chat controls are excluded.

SharpRail counterparts: [Codex](src/SharpRail.Plugins.Codex/SPEC.md), [Claude Code](src/SharpRail.Plugins.ClaudeCode/SPEC.md), [shared accessories](src/SharpRail.Plugins.Agent.UI/TerminalFacts.cs), [rail](src/SharpRail.UI/ProjectRail.cs) and [notification collection](src/SharpRail.UI/Notifications/AttentionNotifications.cs).

| ID | Status | ThinkRail has; SharpRail lacks or narrows |
| --- | --- | --- |
| A08 | Partial | New-workspace launch supports prompt, image, skill and model choices. SharpRail has prompt/images and provider launcher choices, but lacks the full completion/template/skill-selection workflow. |
| A13 | Partial | Unified model/effort picker with favorites, recents, provider glyphs, compatible efforts and refreshed catalog. Codex has a catalog/picker; Claude has CLI-driven choices; no shared full preference/catalog semantics. |
| A14 | Partial | Consistent account connection and default model/effort controls for the supported CLIs. Launch defaults and provider-owned account/config surfaces exist, but their workbench presentation remains uneven. |
| A16 | Partial | Live tokens/cost/context telemetry with explicit finalized boundaries. SharpRail exposes rollout/transcript tokens and account limits, but not the complete common context/cost presentation; Claude deliberately avoids estimated subscription cost. |
| A17 | Missing | Workspace/project rollup replaces identity glyph with the shared working brand badge; attention is a separate static binary dot. Plugin decorations are on terminal tabs, including nested Projects tabs, not a normalized workspace/project rollup. |
| A18 | Missing | Owner-wide unread-result watermark cleared only when the relevant result is visibly seen; converges across clients. Terminal done/blocked statuses are not this durable read model. |
| A19 | Missing | CLI-reported background work keeps the workspace working and suppresses premature done attention. Establish which lifecycle signals Claude Code and Codex expose before defining a shared status contract; SharpRail does not own their command/subagent execution. |
| A26 | Partial | Common MCP Settings: user/project scope, shadowed entries, stdio/HTTP editors, JSON paste, presets and field-preserving writes. Claude has reviewed configuration composers; Codex exposes configuration/provenance; no unified manager matching this complete workflow. |
| A32 | Partial | Portable skills loaded from multiple agent roots with source provenance, group/individual enablement, live stale/reload badge and project trust acknowledgement. Provider capability panes expose discovery, but no shared per-session manager with those guarantees. |
| A35 | Partial | Before starting work, explain which project resources trust admits and which run code; revoke/acknowledge through one projection. Claude/Codex have their own trust/config semantics, but no common Welcome/Start-work/Skills summary. |

## Review and agent-run change gaps

Upstream authorities: [reviews](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/reviews/SPEC.md), [turn receipts](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/turns/SPEC.md), [panels](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/panels/SPEC.md). SharpRail: [Panels contract](src/SharpRail.UI/Panels.SPEC.md), [continuous review](src/SharpRail.UI/ChangesReviewPane.axaml.cs), [Git contract](src/SharpRail.Host.Core/Git.SPEC.md), [Review panel](src/SharpRail.UI/ReviewPull.cs).

| ID | Status | Gap |
| --- | --- | --- |
| R01 | Missing | Durable review records with draft/sent/resolved/dismissed lifecycle, clear/archive and per-file finished state. SharpRail's Review panel is principally branch/PR actions; continuous Changes review is a diff reader with local triage. |
| R02 | Missing | Select code or a hunk and write a review comment on it; whole-file and whole-change-set remarks. Editor-to-agent selection context already exists and is not review authoring. |
| R03 | Missing | Base/worktree anchor spaces pinned to immutable original blobs, quoted context and stale/moved/outdated re-anchoring. Existing IDE selections and CAS revert hashes do not persist review anchors. |
| R04 | Missing | Image/SVG region, CSV cell, JSON pointer, notebook cell and PDF page-region anchors, geometry projection and unplaced-thread strip. No shared review contract on rich renderers. |
| R05 | Missing | Per-file accordion with editable drafts, sent history, finding badges, focus/jump and review flags on tabs. |
| R06 | Decision | Send one or all reviewed comments as structured context to the chosen Codex/Claude terminal, retain a per-file recipient and resolve comments through MCP. The fork's Claude review round trip remains pending; no complete host comment subsystem backs it. |
| R07 | Missing | Hunk Ask agent action with displayed hunk context, source-side semantics and a review composer. Revert/Undo and Keep exist; this third action does not. |
| R08 | Missing | Review-section scratch drafts survive collapse, virtualization, renderer changes and Stacked/One-file switching without accidental focus/submission. |
| R09 | Decision | Read-only Last turn and selected agent-run Changes scopes from before/after Git tree snapshots, persisted bounded receipts and turn-divider navigation. Current commit/branch/working/index scopes do not answer what one run changed. |
| R10 | Decision | Agent-neutral run-boundary receipts for Codex/Claude, including shell/generator writes rather than trusting tool arguments. Hooks would need a new bridge; overlapping runs must remain interval changes, not exclusive authorship claims. |
| R17 | Partial | Complete review keyboard ownership: one active review tab answers V/J/N/P, and modal/menu/text-entry/terminal input wins. Current specs retain the shared keyboard gate as an open requirement. |
| R18 | Partial | Delayed section sizing preserves requested scroll/reveal until content can satisfy it or the user takes over. Current outer scroll restores only the available offset. |
| R19 | Partial | Registry-declared natural versus bounded review section height. Source/Markdown grow; other formats use generic bounded viewports rather than their own capability contract. |
| R20 | Missing | A diff whose file left the change set announces identical sides. Current open diff retains its previous content instead. |

## Workbench and docking gaps

Authorities: upstream [layout](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/shell/layout/SPEC.md), [intents](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/shell/layoutIntents/SPEC.md); SharpRail [Docking contract](src/SharpRail.UI/Docking/SPEC.md), [UI contract](src/SharpRail.UI/SPEC.md), [profile](src/SharpRail.UI/State/ProfileStore.cs), and the older fork log in [UPSTREAM.md](UPSTREAM.md).

| ID | Status | Gap |
| --- | --- | --- |
| L06 | Partial | Preserve surviving pane chrome and bodies through visibility, folds and topology changes, not just cached document controls or unchanged refreshes. Full latest remount-free contract remains open. |
| L08 | Partial | Drop normalized no-op writes and atomically commit document/attention/tool fan-out; review-local writes invalidate only their body. |
| L10 | Missing | Older fork's per-project frame stash: returning to a project restores its own arrangement. Current profile owns a frame per window and workspace document views. |
| L11 | Missing | Older fork's drawn tab-placement picker instead of only text destination menus. |
| L12 | Partial | Older fork's open-beside-Claude rule and unoffered-tool handling in Projects tab mode. The core vertical/Projects mode itself is implemented. |

## Projects and workspace lifecycle gaps

Authorities: upstream [workspaces](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/workspaces/SPEC.md), [panels](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/panels/SPEC.md), [latest ordering](https://github.com/JetBrains/thinkrail/commit/be06fb019); SharpRail [Workspaces](src/SharpRail.Host.Core/Workspaces.SPEC.md), [HostState](src/SharpRail.Host.Core/HostState.SPEC.md), [State](src/SharpRail.UI/State/SPEC.md), [Panels](src/SharpRail.UI/Panels.SPEC.md).

| ID | Status | Gap |
| --- | --- | --- |
| W08 | Decision | Automatic settling on merged/closed PR and authoritative host review snapshot. Existing PR lookup does not provide this lifecycle. |
| W10 | Partial | Active workspace's parent project is revealed on initial mount/ownership change while a later manual collapse stays respected. SharpRail primarily restores its collapsed set. |
| W11 | Missing | Prefetch a selected non-default base off the creation critical path. Default-base prefetch and create-time fetch already exist. |
| W12 | Missing | Retry rejected workspace creation inside the same dialog. SharpRail closes it before the operation and reports failure separately. |
| W14 | Missing | Seed the scratch directory when a session starts in a user-owned attached workspace. Managed creation seeding is not that case. |
| W15 | Partial | Managed branch rename after its initial automatic name. First `set_title` names a untouched generated branch; later label renames deliberately do not rename Git. |
| W16 | Decision | Create a new project through an agent-assisted setup brief, not only open/clone an existing project. Existing starting-a-new-project skill does not provide this native flow. |
| W17 | Partial | Fork's gitless tool withholding/notices and Start-work copy. Plain folders are intentionally supported, but every unborn/plain-folder Changes/Review/launcher state needs the same policy. |

## Location bar and Git UI gaps

Authorities: upstream [location bar](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/shell/locationBar/SPEC.md), fork history in [UPSTREAM.md](UPSTREAM.md); SharpRail [header layout](src/SharpRail.UI/WorkbenchWindow.axaml), [location actions](src/SharpRail.UI/LocationBar.cs), [branch list](src/SharpRail.UI/BranchList.cs), [panel gaps](src/SharpRail.UI/Panels.SPEC.md).

| ID | Status | Gap |
| --- | --- | --- |
| G02 | Missing | Header Remote divergence and Pull request chips; open/merged/closed state, provider URL and authoritative snapshot arbitration. PR details are confined to Review. |
| G03 | Partial | Branch card's separate Based on row, Remote/review rows and shared Compare-to chooser. Caption currently derives “from” from the comparison choice, so creation base and retargeted comparison are not separately represented. |
| G04 | Partial | Shared searchable comparison branch combobox; Local/Remote folder groups, leaf labels, full-ref tooltips and chosen target. Current Changes target menu does not implement the full grammar. |
| G05 | Partial | Fork's topbar branch picker lists every remote collapsibly and click switches to an existing worktree or starts a workspace. Native branch list currently lists local refs and delete/fetch actions. |
| G06 | Partial | Fork's branch deletion can first remove its worktree, with explicit dirty-force confirmation. Dirty workspace deletion exists, but branch deletion refuses occupied branches rather than composing the teardown. |
| G07 | Missing | PR setup guidance Run action opens a workspace terminal; preserve Git's authentication error text and host-key hint; remote-host commands follow host platform. Copyable generic guidance exists. |
| G08 | Missing | Registered OS URL scheme for serialized project/workspace/resource links. Serialization, in-app history and `--link` launch routing are implemented. |
| G09 | Partial | Remote host confirms workspace existence before history traversal. Current remote navigation cannot make that confirmation. |
| G10 | Partial | Back/Forward records user navigation separately from passive remote removal/close fallbacks, with observed locations persisted as links. Current history records any location change. |
| G11 | Missing | CI status beside the pull request chip is a recorded SharpRail request. [Panels contract](src/SharpRail.UI/Panels.SPEC.md) lists it as missing; [LocationBar.cs](src/SharpRail.UI/LocationBar.cs) and the window header have no CI chip. Keep separate from JetBrains parity. |

## Rendering and resource gaps

Authorities: upstream [resource registry](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/resources/SPEC.md), [rich panels](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/panels/SPEC.md), [themes](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/apps/web/src/themes/SPEC.md); SharpRail [registry](src/SharpRail.UI/Resources/ResourceRegistry.cs), [Rendering](src/SharpRail.UI/Rendering/SPEC.md), [Themes](src/SharpRail.UI/Rendering/Themes.SPEC.md), [PDF plugin](src/SharpRail.Plugins.PdfPreview/SPEC.md), [Scintilla limits](src/SharpRail.Scintilla/README.md#limits).

| ID | Status | Gap |
| --- | --- | --- |
| U01 | Partial | Minimal Light and Minimal Dark now join the four original palettes. The latest independently tuned standard Light solid accent remains unported. |
| U03 | Partial | Dense named layout tokens, semantic typography and line heights are implemented, including the 40px location strip, 28px panel headers and 24px header controls. Remaining surface adoption and matched-content fidelity belong to the visual acceptance audit. |
| U04 | Missing | User-selected code font and ligatures across editor, terminals, Markdown code and diagrams. Font size controls are not font-family selection. |
| U05 | Partial | Consume manifest syntax colours consistently across source and Markdown code. Editor TextMate grammars and theme updates exist, but their palette is separately authored; code fences have narrower tinting. |
| U06 | Missing | Content-shaped loading skeletons and resolved-content fade-in. Several native placeholders remain text. |
| U08 | Partial | Shared tooltip timing and disabled-trigger wrapper; pointer-transparent fork tooltips need framework-specific acceptance. Avalonia tooltips and individual disabled-control tips already exist. |
| U09 | Missing | Shared non-modal dialog/inspector semantics, Escape and focus return while other controls remain usable. Most native dialogs are modal windows. |
| U10 | Missing | Toast swipe dismissal and timer pause on hover/focus; route remaining gesture/error-line notices through the bounded/coalesced toast queue. |
| U11 | Partial | Find paints every match rather than just selecting the current one. Terminal search and the full preview search surface remain narrower than the fork's expectation. |
| U12 | Partial | Workspace-search Markdown hit opens and flashes the matching rendered block. Navigation exists; block flash does not. |
| U13 | Missing | Registry advertises phone support, anchor geometry, copy/layout/whitespace and natural/bounded height per renderer. Current registry primarily declares matching/rank and view/diff factories. |
| U14 | Missing | Sanitized HTML preview and 2-up HTML diff. HTML currently opens as source; notebook HTML output also lacks the upstream sandboxed rich presentation. |
| U15 | Missing | PDF page-pair diffs, synchronized page sequence and 2-up/swipe/onion/difference comparison. PDF file preview exists through the plugin. |
| U16 | Partial | PDF viewport render window with two-page overscan, cancellation/release of offscreen canvases, page/zoom state restoration and page-region review focus. Current plugin rasterizes a full page set and has no review geometry. |
| U17 | Partial | Free image zoom/pan gestures and intrinsic-space region selections. Fit/natural/button zoom and four visual diff modes already exist. |
| U18 | Partial | JSON array move detection and stable object hashes. Native alignment presents moves as removed/added. JSON/JSONC trees themselves exist. |
| U19 | Partial | Notebook source-language treatment, rich HTML outputs and structural review/selection parity. Notebook view/diff exists; full upstream output and authoring contract does not. |
| U20 | Partial | Rendered Markdown raw HTML tables/lists/styles and text wrapping around floated pictures. Current limited HTML treatment can show text or place a floated picture on its own line. |
| U21 | Partial | Regroup a multi-block `<details>` in Markdown diffs; compare list items robustly when documents contain different counts of top-level lists. Current native diff has documented limits. |
| U22 | Partial | Per-tab code/table/JSON/notebook view state and delayed scroll restoration after detach/reattach. Markdown/source positions and image-mode state are implemented subsets. |
| U23 | Partial | Deleted-file banner in every applicable preview, not only source editor; other file formats mainly receive the deleted tab mark. |
| U24 | Partial | Editor text input exists, but full IME preedit, an accessibility text provider, touch selection handles and a copy menu are absent, confirmed by [Scintilla limits](src/SharpRail.Scintilla/README.md#limits). These are implementation gaps, followed by platform acceptance; completion/LSP workstation features are not automatically upstream requirements. |
| U25 | Partial | Native AppKit Edit role items work inside native picker fields, plus About. Current Avalonia menu uses app commands instead. |

## Host and transport gaps

Authorities: upstream [shared environment](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/shared/SPEC.md), [host](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/host/SPEC.md), [logging](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/log/SPEC.md), [watchers](https://github.com/JetBrains/thinkrail/blob/be06fb01927e8386e3aec215bbcdb1a312ec66fa/packages/server/src/watch/SPEC.md); SharpRail [Core](src/SharpRail.Host.Core/SPEC.md), [Files](src/SharpRail.Host.Core/Files.SPEC.md), [Git](src/SharpRail.Host.Core/Git.SPEC.md), [Client](src/SharpRail.Host.Client/SPEC.md), [Remote](src/SharpRail.Host.Remote/SPEC.md), [protocol counter](src/SharpRail.Host.Abstractions/Handshake.cs).

| ID | Status | Gap |
| --- | --- | --- |
| H01 | Missing | Bounded login-shell PATH/SSH_AUTH_SOCK repair and environment import for opted-in GUI launchers, inherited-value precedence, noisy-probe parsing, retry and excluded control/history variables. Terminal login-shell spawning alone does not fix the host's own child environment. |
| H02 | Missing | Structured leveled rotating host logs and crash report under state directory. Console diagnostics are not this persistent support surface. |
| H03 | Partial | One bounded idempotent graceful shutdown path awaited by every launcher, signal handling and in-flight work drain. Existing disposers are not evidence of the entire upstream contract. |
| H04 | Missing | Retrying recursive teardown and automatic first-free-port selection for launchers. Explicit listener configuration is a different contract. |
| H05 | Partial | Streamed bounded file/blob bytes and child output, with cancellation/nonzero/truncation semantics. Native single-message byte reads and callback-loaded images work, but do not offer streamed large-resource transfer. |
| H07 | Partial | Additive capability gates for later host operations such as path inspection/prewarm/spec queries. Current protocol is 6 and request replay is enabled; not every later operation has its own introduction gate. |
| H08 | Partial | Immutable original snapshot for index/working-side diffs. Index bytes can change between reads; commit-side scopes already resolve immutable originals. |
| H09 | Missing | Periodic identity healing when a watched root is replaced while all subscribers remain attached and no fresh admission occurs. Current watcher heals on a new subscriber/prewarm or error. |
| H10 | Missing | Watcher admission/activity lifecycle seam that seeds retained HEAD baseline before metadata events, including recreation and failed-registration cleanup. |
| H12 | Partial | Full frontmatter YAML semantics for graph metadata: anchors, nested maps and multiline scalars. The portable spec dialect is deliberately narrower; editing preview properties does not make graph parsing a full YAML parser. |
| H13 | Partial | Windows shell resolution, ConPTY and platform filesystem/native build paths are implemented. The Settings shell picker and per-shell startup guidance are missing; current guidance only distinguishes SHELL from the default. See [Windows](WINDOWS.md), [terminal contract](src/SharpRail.Host.Core/Terminals.SPEC.md) and [startup handling](src/SharpRail.Host.Core/PtyTerminalService.cs). Real Windows execution is tracked once under V09. |

## Fork plugin differences and acceptance gaps

These entries retain old omissions that a JetBrains-only delta audit would miss. Sources are [UPSTREAM.md](UPSTREAM.md#fork-port-log), [E2E.md](E2E.md), owning module specs and implementation. Implemented plugin behavior is not listed as a feature gap merely because a native acceptance journey has not been run.

| ID | Status | Gap |
| --- | --- | --- |
| P01 | Partial | Plugin `WorkspaceUpdated` on actual branch switch; existing creation/removal/relabel notifications are narrower. [Host plugin runtime](src/SharpRail.Host.Core/Plugins.SPEC.md). |
| P03 | Partial | Faint-cell blanking when accessories read terminal buffer tail. Native text reads lack the cell-attribute information; picker placeholder detection can therefore differ. [Terminal](src/SharpRail.UI/Terminal/SPEC.md). |
| P04 | Partial | Claude IDE `openDiff` displays proposed unsaved content and returns real accept/reject/save results. It currently opens the target file and reports `diffShown: false`, also the fork's limitation; this is an integration opportunity rather than regression against that fork. [Claude](src/SharpRail.Plugins.ClaudeCode/SPEC.md). |
| P05 | Decision | Claude IDE diagnostics/execute-code capabilities. They are not offered; compare real supported CLI/IDE protocols before adding them. They are not declared fork parity requirements. |
| P06 | Partial | Codex IDE reply lists editors per answering frontend. Current app context unions editor tabs across windows in the workspace; background-window focus selection is otherwise implemented. [Codex](src/SharpRail.Plugins.Codex/SPEC.md). |
| P07 | Partial | Codex failed-state/summary notifications. Its current hook vocabulary has blocked/done but no failed status or summary; cancellation stays silent. Claude has richer failure notifications. |
| P08 | Partial | Codex skill-file ownership across multiple hosts and shutdown. User edits are protected, but disabling and host shutdown share disposal, and a second host is not coordinated. |
| P10 | Partial | Input sealing during picker driving is missing: [ClaudeTerminal.cs](src/SharpRail.Plugins.ClaudeCode/UI/ClaudeTerminal.cs) shows a notice without an exclusive-input overlay. Model/effort, attach, usage and plan controls exist; [Pickers.cs](src/SharpRail.Plugins.ClaudeCode/UI/Pickers.cs) implements draft refusal and Esc fallback. Live terminal-facts acceptance is included in V12 below. |
| P11 | Partial | Codex Windows IDE IPC is absent: [IdeBridge.cs](src/SharpRail.Plugins.Codex/Host/IdeBridge/IdeBridge.cs) declares Windows unsupported and [Codex checks](tests/SharpRail.Checks/CodexChecks.cs) skip it there. The registered model supplier is not consumed by a reactive Start-work selector. The failed recovery check is recorded under V06; installed CLI and native lifecycle acceptance is included in V12. |

## Notifications and platform acceptance gaps

Authorities: [UI](src/SharpRail.UI/SPEC.md), [terminal contract](src/SharpRail.UI/Terminal/SPEC.md), [browser](src/SharpRail.Browser/SPEC.md) and [validation evidence](VALIDATION.md). Implementation gaps are listed here; acceptance tasks follow separately.

| ID | Status | Gap |
| --- | --- | --- |
| V01 | Missing | Equivalent away notifications outside macOS and explicit system-permission state presentation; current denial can be silent. |
| V02 | Partial | Multiple attention notifications lead to a chooser. Current batch opens the latest terminal rather than letting the user choose among all waiting terminals. |
| V04 | Missing | Busy-close confirmation does not dismiss when another client closes the terminal. [TerminalTabs.cs](src/SharpRail.UI/TerminalTabs.cs) awaits a normal confirmation without catalog-driven invalidation; this remains an explicit [terminal contract](src/SharpRail.UI/Terminal/SPEC.md) implementation gap. |

## Outstanding acceptance work

These eight tasks consolidate the remaining checks; they do not assert missing product features. Existing automated passes and source findings are retained in [VALIDATION.md](VALIDATION.md#2026-10-14--verify-entry-source-and-acceptance-audit). Original row IDs in parentheses identify merged entries. No unrun journey is treated as passed.

| ID | Latest result | Remaining work |
| --- | --- | --- |
| V03 | Not run | In a signed macOS app, see a real away notification and click it to focus the correct terminal/window. Delivery/activation wiring exists and recording-notifier checks pass. |
| V05 | Not run | Compare native Settings, menus, Markdown, all plugins and docking at matched content/dimensions; exercise PDF trackpad magnify. Include drop/overflow/spacer/folded-weight geometry (L13, P15–P18). Functional/package checks pass, but there is no matched capture corpus. |
| V06 | Failed/incomplete | Resolve the plugin settings-observer timeout and failed Codex recovery fixture, complete the interrupted terminal/Blueprint UI checks, then obtain a clean packaged gate. Add direct regressions for reused-tab-ID workspace focus, held Visualize render/remount and same-terminal/two-window rendering, held graph requests after detach/disable, and held PDF read/raster replacement/detach (L07, P12, P14–P16). Release, formatting, package identity/signature and 13 targeted cases pass; the full gate does not. |
| V07 | Not run on required platforms | Exercise Safari/Firefox, non-macOS keyboards/IME, real notification permission and production HTTPS/proxies. Accept the Settings fix in a normal bundle and on physical Retina; its parallel-workspace Chromium test-build acceptance already passes. |
| V09 | Not run on Windows | Execute the Windows package and its ConPTY, Recycle Bin, native editor/Skia, clipboard/IME and client journeys. Portable macOS checks skip Windows runtime branches. This covers the platform part of H13. |
| V10 | Emulator/fixture coverage only | Exercise physical Android, camera/QR and real two-device Tailscale pairing, approval, reconnect, revoke and remote UX, including packaged deployment. Emulator and fake-identity transport checks pass. |
| V11 | 120 Hz presentation not measured | Measure physical 120 Hz browser presentation. Recorded restty GPU draw cadence is about 60/s and output throughput is 111 FPS; neither proves the presentation target. No new benchmark was run. |
| V12 | Fixture coverage only | Exercise installed Codex/Claude during sustained streaming while typing/resizing/switching controls, interruptions, process replacement, auth/model changes, editor reload/remount/title changes, remote reconnect and physical drops (A36, P10–P11). Complete Claude pane-driven composers/account/usage/MCP health, nested source navigation after unsaved edits, uninstall/marketplace removal and overrides (P09); Blueprint live-model controls, frontmatter discard, outline, alerts and Mermaid (P12); and live Discord IPC/presence (P18). Generic host/UI tests do not establish these journeys. |

## Suggested implementation batches

These are priorities inferred from the comparison, not accepted feature designs.

1. Close the remaining normal-bundle, real-platform input and notification acceptance gates; browser Settings renderer ownership is implemented.
2. Finish retained-chrome, per-project frame, placement-picker and visual-fidelity gaps; rails, compatible panes, Home visibility and density/location typography are implemented.
3. Extend implemented activity/settling with the remaining workspace attention/read and PR-driven semantics.
4. Build review records/anchors and terminal send/resolve, then evaluate CLI run-boundary receipts for Last turn changes.
5. Close CLI configuration, MCP configuration and skills/design workflow gaps through the provider plugins and shared shipped skills.
6. Close resource parity: HTML/PDF diffs, renderer capabilities, view state, rich anchors and font/syntax consistency.

The inventory records implementation dispositions and outstanding decisions; verification evidence stays in VALIDATION.md.
