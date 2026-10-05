# Prototype validation

Latest source verification includes lightweight commit catalogs: full Release and
published R2R suites pass in `.bench/commit-catalog-release.log` and
`.bench/commit-catalog-published-checks.log`, each with 39 translated upstream cases.
Formatting and diff checks pass. UI, host and checks publish successfully, and
the update is staged with strict/deep signing verification. The running canonical
bundle remains the previously verified hover-lifecycle package below: its terminal
contains an active Codex process. Restart approval is pending; no active user
terminal was ended to install this update.

Status on 2026-09-29: thirty-nine translated upstream E2E cases pass in the full
Release and published-runtime suites with Git/worktree integration enabled. Formatting and
`git diff --check` pass. The canonical `artifacts/SharpRail.app` was republished
with non-composite ReadyToRun and passes strict/deep signature verification.
The full published-runtime suite also passes, including open-world loading and
Git/worktree integration (`.bench/ghostty-integration/hover-lifecycle-published.log`).

Native display availability recovered: CoreGraphics reports two active displays
and CoreVideo display-link creation succeeds. The canonical package is running
(PID 30104); its 1352x848 window was captured and inspected in
`.bench/ghostty-integration/hover-lifecycle-native.png`. Earlier launch failures with RenderTimer
`-6661` occurred while no displays were active, matching
[Avalonia issue 18895](https://github.com/AvaloniaUI/Avalonia/issues/18895).
Earlier captures and running-app statements below are historical evidence.
The complete visual/docking fidelity objective and upstream suite translation
remain active; the evidence below does not prove the full reference contract.

## Current evidence

Commit listing now has a dedicated host operation with direct local and gRPC
parity. Restoring a commit selection uses it rather than loading a full snapshot
just to read history. The catalog and snapshots share the capped, sanitized log
implementation. Tests verify matching metadata through both transports while a
disposable repository's index is corrupted, prove a full snapshot fails in that
fixture, and restore the index in `finally`. Missing ranges return empty catalogs;
cancellation remains observable even for an empty comparison. Fresh-window Git
selection restoration and all existing UI checks pass. Catalog membership live
refresh and rewritten-away selection fallback remain incomplete.

All changes from the clean `ghostty` worktree at `522d81e` were integrated using
the common ancestor, preserving this checkout's newer docking, Git and test work.
`AGENTS.md` documents the host layers, UI structure, native terminal bridge,
state locations, build scripts and verification entry points.

Whole-tab hover now paints the outer frame, including the close-button slot;
the Fluent label-only hover background is suppressed in XAML. Dark/light pointer
checks cover label, close button, exit and layout replacement while hovered.
Pointer-exit rendering uses the existing selection indicator so removed panes
cannot trigger stale layout lookups. The hidden-bottom shortcut test explicitly
focuses its moved tab before keyboard input.

Final Release and published R2R suites pass with all 39 translated cases:
`.bench/ghostty-integration/hover-lifecycle-release.log` and
`.bench/ghostty-integration/hover-lifecycle-published.log`. Formatting, publishing,
strict/deep signing and diff checks pass. Native shell/Metal checks pass in
`.bench/ghostty-integration/native-bridge.log`; the Avalonia native terminal suite
passes in `.bench/ghostty-integration/native-avalonia-recheck.log` after an initial
Ctrl-C timeout. That retry does not establish native input harness stability.
The final package is running and its own-window capture was inspected; native
pixel comparison of hovered tabs remains distinct from the headless checks.

Workspace Git target, scope and selected commit now persist in the profile.
Selection changes save immediately; switching/closing windows records the active
selection. Catalogs remain derived and reload when restoring a commit scope.
Fresh-window checks verify commit and pending-scope restoration with retained
targets; malformed saved state normalizes without blocking project access. The
initial full Release suite passes (`.bench/git-query-persistence-checks.log`),
as does final formatting (`.bench/git-query-persistence-final-format.log`).
The signed final package is running and inspected; full R2R verification passes
(`.bench/git-query-persistence-published-checks.log`) with all thirty-nine
translated cases and open-world checks. Automatic Git refresh and rewritten-away commit fallback
remain unfinished.

The Changes scope menu now uses real target-to-HEAD commit data, capped at 200,
with subjects/authors sanitized as upstream and metadata carried through gRPC.
Selecting a commit shows its short SHA and subject tooltip, opens a distinct
commit diff tab and excludes working files. Scope switches retain the comparison
target; per-window workspace state restores the target/commit and isolates other
worktrees. Initial full Release checks pass (`.bench/commit-picker-checks.log`).
The signed final package is running and was inspected; final R2R verification
passes (`.bench/commit-picker-published-checks.log`) with all thirty-nine
translated cases and open-world checks. Additional UI checks cover staging availability with a retained
target and readonly commit rows. Menu pixel fidelity,
automatic catalog refresh and rewritten-away commit fallback remain open.

The host supports commit-scoped snapshots and diffs using the same first-parent
range, or the entire tree when no parent is available. It validates commit IDs,
retains unknown commits as errors, and excludes working edits and untracked files
without scanning working status. Existing-commit fixtures verify both ranges and
local/gRPC parity. The initial full Release suite passes
(`.bench/commit-ranges-checks.log`), and final formatting passes
(`.bench/commit-ranges-final-format.log`). The signed package is running and its
own window was inspected; final published verification passes with required
R2R/open-world checks (`.bench/commit-ranges-published-checks.log`). Commit
catalog/picker wiring is recorded above; no new upstream translation is
claimed by these host regressions.

Changes snapshots now accept the selected scope through direct and gRPC adapters.
Lists and line counts use one range: Staged reads the index; Uncommitted reads
HEAD-to-working-tree; explicit targets retain merge-base-to-working-tree behavior.
Status metadata remains available for row actions. Host regressions verify net
counts, cancelled index/worktree changes and transport parity. The initial full
Release suite passes (`.bench/scope-ranges-checks.log`); final formatting passes
(`.bench/scope-ranges-final-format.log`). The final package is signed and inspected
running; its complete published suite passes with required R2R/open-world checks
(`.bench/scope-ranges-published-checks.log`). Its additional UI regression verifies
selected-scope counts and removal after unstaging. Commit
scopes, workspace-local query state and live refresh remain open.

Uncommitted retains staged-only files and opens HEAD-to-working-tree diffs,
including untracked bodies. Host checks verify that a staged addition followed
by an unstaged replacement shows the net current content, with gRPC parity for
untracked reads. Actual row input verifies staged-only visibility and distinct
All changes/Uncommitted tabs. The full Release suite passes
(`.bench/uncommitted-checks.log`), as does formatting
(`.bench/uncommitted-format.log`). The updated package is signed and inspected
running; its final full runtime check passes
(`.bench/uncommitted-published-checks.log`) with all thirty-nine translated cases
and required R2R/open-world checks. Commit-scope selection and live refresh
remain open; the upstream scope case
is not counted as translated by these additional regressions.

Git repository probes now distinguish plain folders from corrupt Git metadata.
Local and gRPC checks preserve failures and their details. An additional headless
regression sees an error instead of a clean change set, opens an accessible
document, repairs its disposable fixture and recovers through the Changes Retry
button. The full Release suite passes (`.bench/git-probe-checks.log`); formatting
passes (`.bench/git-probe-format.log`). This regression does not cover the complete
upstream deleted-target/scope-switch case. The updated package passes strict/deep
signature verification and has been inspected running; its full runtime suite
passes in `.bench/git-probe-published-checks.log`, including all thirty-nine
translated cases and required R2R/open-world checks.

Explicit Git target comparisons now measure the working tree from the merge base,
including untracked files. Snapshots and opened diffs share that baseline, so
target-only commits add no phantom changes and staged/unstaged counts describe the
net working content. Host regressions reuse existing commits in a depth-two clone
and cover committed local changes, an advanced target, untracked bodies and local/
gRPC snapshot-and-diff parity. Full final Release and published R2R suites pass39
translated cases and all additional checks (`.bench/branch-baseline-final-checks.log`,
`.bench/branch-baseline-published-checks.log`). Formatting passes
(`.bench/branch-baseline-final-format.log`). The canonical app was republished,
signed and inspected running. The native capture proves the restored frame, not
a target-selection sequence. Commit scopes and live Git refresh remain open; this
host regression does not mark the full upstream live-update UI case as translated.

Change rows now reserve a sibling20px action trigger with a4px trailing gap.
Compiled XAML controls hover/focus/menu-open appearance. Dropdown and right-click
share the anchored menu; View opens the real diff and Copy path writes the relative
path through Avalonia's clipboard. The translated case covers both List and Tree,
proves trigger input opens no diff, and checks folders have no file action menu.
Folder rows reserve the same trailing slot. Full Git-enabled Release and published
R2R suites pass39 cases and all additional checks
(`.bench/change-actions-verified-checks.log`, `.bench/change-actions-published-checks.log`).
Formatting passes (`.bench/change-actions-verified-format.log`). The canonical app
was republished, signed and inspected running with its restored layout. The capture
is native frame evidence; menu typography and final pixel comparison remain open.

Changes Tree compacts single-child directory chains, sorts folders before files,
shows aggregate added/removed counts, and toggles expansion from the folder row.
Paths are split once during projection. The translated upstream case creates a
real untracked three-line file, checks List/Tree switching, the compact folder and
counts, collapse/expand, opening the real diff and retaining Tree through tool
navigation. Its disposable fixture overrides upstream's docs ignore rule.
Full Release and published R2R suites pass38 cases and all additional checks
(`.bench/changes-tree-final-checks.log`, `.bench/changes-tree-published-checks.log`).
Formatting passes (`.bench/changes-tree-final-format.log`). The canonical app was
republished, signed, relaunched and inspected. Its native capture shows the restored
frame/Markdown/Files/Review layout; it does not prove native Changes Tree pixel parity.

The upstream theme-switch/reload case discovers the available dark/light choices
through Settings, clicks an alternate choice, verifies the immediate workbench
and dialog theme, then opens a fresh window with the same isolated profile and
verifies restoration before selecting the original theme. Full Git-enabled Release
and published R2R suites pass37 cases and all additional checks
(`.bench/theme-reload-complete-checks.log`, `.bench/theme-reload-published-checks.log`).
Formatting passes (`.bench/theme-reload-final-format.log`). Only checks were
republished; application code and the running bundle are unchanged. This case
does not prove the full upstream manifest catalog, high contrast, explicit system
theme pairs or cross-window theme synchronization.

The theme catalogue now mirrors the reference's four bundled manifests. Full
Release checks with Git fixtures pass 74 translated cases plus catalogue,
legacy-profile migration, Mermaid/Markdown re-theme and every-theme selection
checks (`.bench/run3.log`); the native terminal check verifies Ghostty backgrounds
under all four themes (`.bench/native.log`). Headless captures:
`.bench/theme-{dark,light,high-contrast-dark,high-contrast-light}.png`,
`.bench/theme-contrast-tabs.png` and `.bench/theme-settings-system.png`. The
system-mode case runs for one client, with the application theme variant standing
in for the operating system appearance. No native app capture or republish was made.

Workspace switches preserve the mounted workbench and its outer center/left
regions, verified through actual worktree navigation and visual-tree detach counts.
Deferred restored-document completion updates only visible bodies, preserving
tab/header/separator identity and keyboard focus after a held read completes.
Full Release and published R2R suites pass (`.bench/mounted-workbench-checks.log`,
`.bench/mounted-workbench-published-checks.log`); formatting passes
(`.bench/mounted-workbench-format.log`). The native capture verifies the running
frame and Markdown preview, not a native workspace-switch sequence. Inner group
chrome still rebuilds on layout/workspace transitions; broader identity and final
pixel fidelity remain separate gates.

Two upstream cases now exercise drag destination paint and hidden-bottom overlap
priority. Actual pointer input checks the legal center-strip ring, absence of
illegal side hints, subtle edge tint, active half-pane preview and complete Escape
cleanup. A menu-created terminal moves into center, hides bottom, and drags onto
the 24px bottom band at a point also inside a legal center split. Bottom wins,
reveals its retained group and moves the same body without duplicating it or
splitting center. Full Release and published R2R suites pass
(`.bench/drop-hints-final-checks.log`, `.bench/drop-hints-published-checks.log`);
formatting verification passes (`.bench/drop-hints-final-format.log`). These are
checks of rendered control geometry and paint properties, not a native drag capture.
Application behavior and the running canonical bundle were unchanged; only the
published checks were refreshed.

Local layout transitions explicitly abort active drag/resize drafts, release
pointer capture and show the source's cancellation message. Idle transitions and
completed gestures stay quiet. A stale mouse-up cannot commit a canceled width
or reveal its hidden side. The two upstream cases exercise resize commit, fold,
unfold, selection, interruption by shortcut, Dismiss and five-second expiry;
multiwindow cases additionally assert that the protected window has no message.
Full Release checks pass (`.bench/gesture-cancellation-final-checks.log`). The
final published suite runs the final styling and additionally asserts responsive
356px/476px notification geometry (`.bench/gesture-cancellation-published-checks.log`).
Formatting passes (`.bench/gesture-cancellation-clean-format.log`). The canonical
non-composite R2R app was republished, signed and inspected running. Its native
capture shows the current workbench, not the notification. Native toast fidelity,
including animations, hover/focus expiry pausing and swipe behavior, remains open.

Two translated upstream cases verify independent windows over the same project.
While the first window drags a tab or previews a side resize, a shortcut in the
second hides its own side without changing the first window's frame or gesture.
The original pointer release still splits the first center or commits its width;
the second window retains its independent topology and width. The harness focuses
the shortcut recipient explicitly because headless keyboard routing uses the
globally focused control. Full Release and published R2R suites pass
(`.bench/window-gestures-final-checks.log`,
`.bench/window-gestures-published-checks.log`); formatting verification passes
(`.bench/window-gestures-final-format.log`). The app code and running canonical
bundle were unchanged by these two translations; only the published checks were
refreshed. Native multiwindow gesture fidelity remains outside this evidence.

Group limits now use an explicit Save action; edits remain drafts until saved.
The translated overage case types the draft with real keyboard input, creates a
third right group, lowers the limit in another worktree, and verifies that existing
groups survive switching and reload while further creation is disabled. Preset
application preserves saved limits, raising them only to fit its topology. Layout
settings retain the reference 512px preset-save and 384px group-limit container caps.
Release checks pass (`.bench/group-limit-platform-checks.log`); the final published
suite also covers preset limit preservation
(`.bench/group-limit-upstream-published-checks.log`). Formatting passes
(`.bench/group-limit-upstream-final-format.log`). The native capture shows the
workbench; exact native Settings geometry remains unverified.

Applying a preset or resetting the frame now requires confirmation, matching the
reference interaction. The Review and local-default reset cases drive Settings
and the confirmation dialog with actual pointer input. Cancellation preserves the
exact layout; confirmation retains document/terminal tabs, installs the vertical
center split and Review proportions, and persists the default/frame across reload.
Native automation exposes the resulting horizontal resize separator. Full Release
checks pass (`.bench/layout-settings-upstream-final-checks.log`); the final published
suite includes the added separator automation assertions and final dialog text
(`.bench/layout-settings-upstream-final-published-checks.log`). Formatting passes
(`.bench/layout-settings-complete-format.log`). The native window capture does not
establish exact Settings/dialog visual fidelity. `AGENTS.md` now maps the solution
structure, ownership boundaries, state, verification and publishing paths.

Tab tooltips anchor below their trigger with a 4px gap. Actual headless hover
checks verify popup position and stability while the pointer moves across a tab.
The native window capture verifies the updated app renders; it does not capture
the tooltip popup. Tool and terminal menus now create groups at either end of
left, right and bottom regions, with disabled commands explaining group limits.
Pointer/menu checks retain the terminal surface across all six creation commands.
The translated upstream terminal side-group case covers a hidden-side drag,
stack resize, independent 27px folds, Space restoration and Files relocation.
Full Release and published R2R suites pass
(`.bench/terminal-side-tooltip-final-checks.log`,
`.bench/terminal-side-tooltip-published-checks.log`); formatting verification passes
(`.bench/terminal-side-tooltip-final-format.log`).

Two additional upstream cases exercise pointer tab reordering and center splits,
plus broad above/below side split targets around expanded and keyboard-folded
panes. Empty source groups and folded destination state survive the moves.
Auxiliary tab menus now use the reference's directional names and validate split
availability when opened. Full Release and published R2R suites pass
(`.bench/split-upstream-final-checks.log`,
`.bench/split-upstream-published-checks.log`); formatting verification passes
(`.bench/split-upstream-final-format.log`). These checks do not establish full
native drag or menu fidelity.

Pane menus now create nonfunctional terminal tabs in their own group. Terminal
tabs can move between regions alongside tool tabs, retain their body when moved
or folded, and close through the context menu. Mixed tab ordering and hidden-tool
restoration persist through workspace switches and reloads. The translated
upstream side-menu case verifies terminal placement and side-specific tool menus.
Full Release and published R2R suites pass, including mixed-order regressions
(`.bench/terminal-side-menu-final-checks.log`,
`.bench/terminal-side-menu-published-checks.log`). Formatting verification passes
(`.bench/terminal-side-menu-final-format.log`). Terminal execution remains excluded.

`COMPLETION.md` tracks the full goal and remaining verification limits.
Auxiliary stacks now follow the reference panel constraints, including folded
weights, resize propagation across folded neighbors, all-folded spacers and
narrow viewport compression. Regression checks compare reference-library
fractions and exercise draft cancellation, persistence and all three regions.
Fractional stack layout avoids cumulative row rounding overflow; rotated folded
labels retain pixel rounding for centered text. Full Release/Git and formatting
checks pass (`.bench/auxiliary-checks.log`, `.bench/auxiliary-format.log`). Native
capture `.bench/auxiliary-native.png` was inspected. Published R2R checks pass
(`.bench/auxiliary-published-checks.log`); the current package includes this change.

Tab search is now a 288px anchored dropdown with compiled XAML, a magnifying
glass shown only on overflow, title/icon rows, path-aware filtering, single-click
selection, empty results and Escape focus restoration. Its search row suppresses
the Fluent focus border and uses muted placeholder/selection styling. Specs
compact spaced em/en dashes and expose role tags on hover/focus. Markdown link
buttons supply measured text baselines; rendered checks verify alignment at 14px
and 24px. Full Release/Git, formatting, published R2R checks and strict/deep
signature verification pass (`.bench/review-fixes-checks.log`,
`.bench/review-fixes-format.log`, `.bench/review-fixes-published-checks.log`).
State moved to `~/.sharprail`; the latest profile was retained and the canonical
package was launched without a profile override. Its window-only capture
`.bench/review-fixes-published-window.png` was inspected and shows restored
workspace state, shortened Spec titles and hidden search buttons for fitting
strips. Final dropdown styling still needs a native popup capture; earlier
native review windows were closed before that capture could be verified.

Ordinary drop collisions now consider all legal targets independently and use
the reference's mean corner-distance ranking, with the hidden-bottom band
retaining its priority. A disabled self-insertion regression failed before the
fix (`.bench/collision-before.log`): it created a neighboring group instead of
falling back to the legal enclosing header append. Pointer checks now verify
that fallback and a compact pane where creation is closer than its overlapping
header, with exactly one active target. Eligibility is cached for the current
drag and discarded on cancellation/local projection replacement. Full
Release/Git, formatting, strict/deep signature verification and published R2R
checks pass (`.bench/collision-checks.log`, `.bench/collision-format.log`,
`.bench/collision-published-checks.log`). The native compact-pane capture
`.bench/collision-native.png` was inspected and agrees with the selected target.

Hidden-bottom drop geometry excludes hidden side restore rails for every bottom
alignment. The regression reproduced a stolen lower-left corner under
`center-left` before the fix (`.bench/drop-priority-before.log`). Pointer checks
now cover both rails across all four alignments and bottom-band priority over
overlapping expanded-side creation targets. Only the winning bottom destination
receives active emphasis. Full Release/Git, formatting, strict/deep signature
verification and published R2R checks pass (`.bench/drop-priority-checks.log`,
`.bench/drop-priority-format.log`, `.bench/drop-priority-published-checks.log`).
The own-window native drag capture `.bench/drop-priority-native.png` was inspected
and shows one active bottom strip with passive hints elsewhere.

Side resize now follows the reference's outer/inner panel composition rather
than pinning the opposite side before the center minimum. Pointer checks cover
both sides and all four bottom alignments, neighbor compression, live bottom
span, Escape restoration, committing only the dragged side, and viewport
compression without rewriting saved widths. Expected drag geometry was checked
against the installed reference's react-resizable-panels 2.1.9 functions
(`.bench/reference-side-projection.json`). Full Release/Git checks, format,
strict/deep signature and published R2R checks pass
(`.bench/side-projection-checks.log`, `.bench/side-projection-format.log`,
`.bench/side-projection-published-checks.log`). Native macOS resize input produced
65% left / 23.67% center / 11.33% right while saved widths remained 18% / 28%;
capture `.bench/side-projection-native.png` was inspected.

Static UI now uses compiled XAML: the application theme, main window/header,
Settings frame and all four page templates, and shared prompt/confirmation
dialog frames. C# retains host wiring, runtime docking, state-dependent rows
and event handlers. Shared mutable brushes preserve live theme changes.
Full Release checks, including Git/worktree integration, formatting,
strict/deep signature verification and published open-world/R2R checks pass
(`.bench/xaml-final-checks.log`, `.bench/xaml-final-format.log`,
`.bench/xaml-published-checks.log`). The canonical package is running.
Native window-only captures `.bench/xaml-published-window.png` and
`.bench/xaml-settings-window.png` were inspected; this verifies the migration
renders natively, not complete screenshot fidelity to the reference.

`artifacts/SharpRail.app` is the latest canonical package and is running. It
uses the reference's muted 14px Git branch asset beside plain branch text,
replacing the font-dependent symbol. Native capture:
`.bench/prototype-branch-icon-native.png`. Full Release, format, strict/deep
signature and current published R2R checks pass
(`.bench/branch-icon-published-checks.log`). Future reviews refresh this one
package, per the user's preference; no new feature-named bundles.

Latest selection fixes keep the folded bottom rail's visible/accessibility label
in sync without replacing its restore control. Move-menu destinations refresh
their selected-tab labels when opened by right-click. Interaction regressions,
full Release, format, strict/deep signature and current published R2R checks
pass (`.bench/selection-details-published-checks.log`).

Tab double-click Keep now requires the left button and excludes the inline
Close control. A regression using two right-button clicks failed before this
guard and passes afterward; ordinary active/inactive preview promotion remains
covered. Full Release, format, signature and fresh published R2R checks pass
(`.bench/preview-rightclick-published-checks.log`). This fix is in the current
canonical app.

Fold buttons now use the reference's separate collapse/expand vertical vector
paths from @remixicon/react 4.9.0, rendered as 72px masks and displayed at 16px.
The native 1352×848 review capture `.bench/prototype-fold-icons-native.png`
shows expand on the folded right group and collapse on the expanded group.
Full Release, format, signature and published R2R checks pass
(`.bench/fold-icons-published-checks.log`); the canonical app includes them.

The first expanded bottom group now owns the alignment menu, falling back to
the first folded group when necessary. Pointer checks cover all four choices,
their persisted radio state, and Hide from a folded rail. The 224px menu uses
the reference horizontal ellipsis and anchors below the trigger's right edge.
Native popup capture: `.bench/bottom-alignment-menu-native.png`; native window
positions confirm the corrected anchor. Full Release, format, strict/deep
signature and published R2R checks pass
(`.bench/bottom-alignment-published-checks.log`). The canonical app includes it.

Alignment menu styling now matches the reference's 28px rows, 4px padding,
6px outer corners, elevated background and 14px primary check on the right.
Radio semantics remain intact. A regression inspects the rendered check and
row geometry while exercising pointer selection; native capture
`.bench/alignment-style-menu-native.png` confirms the result. Full Release,
format, signature and published R2R checks pass
(`.bench/alignment-style-published-checks.log`).

Drag overlays use the reference's 4px corners, 10% inactive fill/20% border,
20% active fill and opaque accent border. Center split hit areas match the
edge rectangles in Workbench.tsx; corners outside those rectangles reject
drops, and active half previews include the group header with a 2px border.
Expanded auxiliary creation targets have 4px insets and 1px active borders.
Drops at folded-pane margins join and unfold the existing pane. Folded bottom
panes also expose inner targets that create neighboring groups. Regressions cover the rendered overlay,
corner rejection and folded-bottom placement. Native process-local drag events
and `.bench/drag-overlay-native.png` confirm the active right-half preview.
Full Release, format, signature and published R2R checks pass
(`.bench/drag-target-published-checks.log`).

The whole folded bottom frame accepts drops, including its alignment button.
Its hint is a square background tint without an extra border; hidden side
rails use square 1px/2px hints and hidden bottom uses only its 1px/2px top edge.
A pointer regression drops over the alignment button and verifies join/unfold,
plus full-frame active hint geometry. Native capture:
`.bench/folded-rail-drag-native.png`. Full Release, format, signature and latest
published R2R checks pass (`.bench/rail-target-published-checks.log`).

Hidden-side drops now append after all retained groups, independently of the
last-focused pane. A regression with two retained groups and focus on the first
fails before the correction and passes afterward. Hidden-bottom restoration
still reuses its last-focused group. Full Release, format, signature and latest
published R2R checks pass (`.bench/hidden-side-published-checks.log`).

Tab-strip drops now distinguish a solid square 2px insertion marker from the
subtle full-header append frame. The header's trailing controls/empty space
accept append drops; clipped tab content cannot select insertion behind those
controls. Overflow fades use the reference's 16px width. Pointer regressions
verify the marker, append highlight and resulting canonical order. Native
capture: `.bench/tab-insertion-native.png`. Full Release, format, signature and
published R2R checks pass (`.bench/tab-drop-published-checks.log`).

Split menu availability now refreshes on actual context-menu opening, using
the measured pane dimensions. Previously it was captured before layout, leaving
valid actions disabled. A real right-click regression checks both split actions
in a sufficiently large pane. Full Release, format, signature and published
R2R checks pass (`.bench/split-menu-published-checks.log`).

Folded-bottom creation and join targets now coexist. Their overlapping targets
use the reference's [distance-to-corners collision rule](https://github.com/clauderic/dnd-kit/blob/master/packages/core/src/utilities/algorithms/pointerWithin.ts).
Pointer regressions cover both creation directions, margin joining, and joining
when the group limit disables creation. Native capture:
`.bench/folded-creation-native.png`. Full Release, format, signature and published
R2R checks pass (`.bench/folded-creation-published-checks.log`).

Folded bottom labels now read downward and center within the available rail,
without standalone button borders or padding. A transformed label-position
regression covers centering and direction; native capture
`.bench/folded-label-native.png` confirms the result below the alignment menu.
Full Release, format, signature and published R2R checks pass
(`.bench/folded-label-published-checks.log`).

Scrolled tab insertion markers are clipped at their actual boundary instead
of being clamped into the viewport. A pointer regression with an overflowing
strip and a partly clipped first tab fails before the change and passes after;
ordinary visible insertion markers remain covered. Full Release, format,
signature and published R2R checks pass
(`.bench/clipped-marker-published-checks.log`).

Eligible tab drags now add the reference's temporary 20px append target to
the scroller. Pointer regressions verify its width, overflow extent and removal
on cancellation. Release, formatting, signature and published R2R checks pass
(`.bench/append-target-published-checks.log`). The native review capture was
taken after user interaction ended the drag; it proves ordinary rendering,
not the active append overlay.

Split menus and drag targets now share the reference's inclusive whole-pane
640px width and 360px height thresholds. A pointer regression checks both axes
at the boundary and one pixel below it. Release, formatting, strict/deep
signature and full published R2R checks pass
(`.bench/split-boundary-published-checks.log`). The latest package is running;
ordinary native workspace capture: `.bench/split-boundary-published-native.png`.

Nested center split grids now use numeric definitions instead of localized
strings. Decimal commas previously created five rows or columns instead of
three. Regressions explicitly use `de-DE`, checking equal halves, a single
separator, and both axes' direct-child resize limits. Center resize limits
now use the reference's whole-frame percentage and disable undersized handles.
Release, formatting, strict/deep signature and full published R2R checks pass
(`.bench/nested-resize-published-checks.log`). Native three-pane rendering is
inspected in `.bench/nested-split-native.png`. The latest package is running.

Empty auxiliary bodies and folded bottom labels now use the reference's
"Empty group" text. The extra body-level Add / Reveal button is removed;
restoration remains in the header. Release, formatting, strict/deep signature
and full published R2R checks pass (`.bench/empty-group-published-checks.log`).

Folded side panes now retain before/after creation targets as the reference
does. Previously the creation filter allowed only folded bottom panes.
Pointer regressions cover both sides and directions, margin joining, and
group-limit fallback joining. The creation regression fails before the fix;
Release, formatting, strict/deep signature and full published R2R checks pass
(`.bench/folded-side-published-checks.log`). The latest package is running.

Outer separators reserve 320px for the center region regardless of its nested
topology, matching the reference. Nested horizontal and vertical fixtures check
the accessible maximum. The extra 12% center rendering floor is removed so
the computed center ratio controls layout. Release, formatting, strict/deep
signature and full published R2R checks pass
(`.bench/outer-resize-published-checks.log`).

Side widths now snap to the reference's 8% minimum and collapse below its 4%
midpoint, retaining the last expanded width. Pointer regressions cover both
sides at 6%, 4% and 3% and verify that previews do not persist geometry. Ratio
rounding matches the reference's percentage precision to stabilize the boundary.
Release, formatting, strict/deep signature and full published R2R checks pass
(`.bench/side-snap-published-checks.log`). The latest package is running.

Hidden side rails now match the reference's 28px full-height strip, sidebar
background, inner border and 24px restore button with a 14px Remix layout icon.
The whole strip remains a drop target; restoration is disabled for an empty
region. Pointer checks cover dimensions, drag restoration and button input.
Native capture: `.bench/hidden-rail-native.png`. Release, formatting, strict/deep
signature and full published R2R checks pass
(`.bench/hidden-rail-published-checks.log`). The latest package is running.

Separator arrows now move by 10% of their panel group's extent, matching
the reference library; Shift and Home/End move through the full range.
Accessible step sizes reflect these increments. Stable separator identities
retain focus through layout rebuilds. Keyboard regressions verify repeated
arrows without refocusing, End maximum and Shift-arrow collapse. Release,
formatting, strict/deep signature and full published R2R checks pass
(`.bench/keyboard-resize-published-checks.log`). The latest package is running.

Bottom resizing now shares the reference's 147px minimum and midpoint collapse
rule, caps at 70%, and retains expanded height when hidden. Pointer regressions
cover minimum snapping, the exact midpoint, collapse, the maximum, and shortcut
restoration; previews remain unpersisted. Release, formatting, strict/deep
signature and full published R2R checks pass
(`.bench/bottom-snap-published-checks.log`). The latest package is running.

The preceding StableTabs build verified that selecting another tab retains
the strip controls and updates the affected pane's highlight/content. Native
inactive-preview reproduction fails before this change and keeps the tab after
it (`.bench/unselected-preview-native-trace.log`,
`.bench/unselected-preview-native-stable.log`). The regression checks control
identity and two presses without an intermediate layout pump. Full Release,
format, signature and fresh published R2R checks pass
(`.bench/stable-tabs-published-checks.log`). Capture:
`.bench/prototype-stable-tabs-native.png`.

The earlier standard build verified the
preview-tab fix: reselect advances navigation/focus without rebuilding the tab
strip. Native double-click reproduction leaves the preview flag true before
the fix and false afterward (`.bench/preview-tab-native.log`,
`.bench/preview-tab-native-after.log`). The regression also checks normal font
style after two presses without a layout pump between them. Full Release,
format, strict/deep signature and published R2R checks pass
(`.bench/preview-keep-published-checks.log`). Current capture:
`.bench/prototype-preview-keep-published.png`.

Title-bar gestures now cover the full painted header, including blank
space; button presses remain excluded. Native process-local NSEvents zoom an
isolated macOS window from 1352×848 to 2560×1410
(`.bench/titlebar-native-events.log`). Full Release and format checks pass.
The pane headers now omit the extra Arrange icon and show Add only when a tool
can be restored; empty groups expose removal with the final center protected.
Checks exercise hidden-tool restoration through that menu. Strict/deep bundle
signature verification passes. Capture: `.bench/prototype-titlebar-gestures-native.png`.
Native physical drag motion remains unmeasured; the handler invokes the platform
move operation. This window uses a separate review profile.

`artifacts/SharpRail-Settings.app` contains an earlier published application.
Its process has ended. The updated standard bundle also includes
the Settings border corrections. Strict/deep signature verification and its
full published R2R checks pass (`.bench/settings-published-checks.log`). Native
captures are `.bench/prototype-settings-published-native.png` and
`.bench/prototype-settings-updated-workspace.png`.
The current standard app includes Markdown scrolling, pane context menus, fitted preview tab
labels, split Git path colors, corrected file rows and hidden frontmatter. Strict/deep signature,
full Release checks and solution format verification pass. Current published
checks pass with `SHARPRAIL_REQUIRE_R2R=1`, including dynamic assembly loading,
runtime code generation and live settings (`.bench/final-published-checks.log`).
No benchmarks, commits or pushes were run.
Earlier live bundles were preserved when their review windows declined a normal
quit. Those sessions ended before the standard bundle was refreshed. The current
standard app uses an isolated review profile and remains running.

Native Markdown scrolling evidence is `.bench/prototype-markdown-scroll-fixed.png`;
latest Git path evidence is `.bench/prototype-change-path-native.png`.
Native Settings is captured in `.bench/prototype-settings-native-current.png`
from an isolated review driver; its rounded 832 × 679 window matches the 80%
owner-height rule. Full checks cover no-op/undersized split rejection and
empty/folded-pane focus. macOS accessibility probes confirm tab selected states,
file names and separator current/min/max values. Native accessibility actions
successfully close Settings and select Files in the isolated window. Evidence:
`.bench/current-native-accessibility.log`, `.bench/native-close-action.log`,
`.bench/native-tab-action.log` and `.bench/current-published-checks.log`.

Latest source adds the exact 370 and 500 Geist faces and uses 14 px tab icons.
Release checks verify actual glyph-face resolution (no fallback to another
weight). These additions are included in the running TabFit bundle. Native comparison
at 2× scale places the Projects text's left edge at pixel 54 in both windows;
its bright-pixel bounds are reference `(54,100,155,124)` and prototype
`(54,99,156,125)`. `.bench/typography-comparison.json` records this residual
glyph-boundary difference. Disabling hinting in the isolated driver did not
change it; grayscale antialiasing also had no measured effect. Neither setting
was introduced in production.

Latest Projects source matches the reference's 28 px header/project rows,
12 px outer padding, 14 px identity icons, 24 px worktree indentation and 4 px
row spacing. Worktrees omit the branch line when it matches their displayed
name. Pointer collapse, keyboard expansion and focus retention pass; native
accessibility collapse/expand actions also succeed. Native evidence:
`.bench/prototype-project-geometry-native.png`, `.bench/project-collapse-native.log`
and `.bench/project-expand-native.log`. These changes are included in TabFit.

| Requirement | Evidence | Scope of proof |
| --- | --- | --- |
| C# host, direct embedded calls, code-first gRPC remote | Program/ProjectChecks and host project references | Local/remote data parity, authentication, cancellation; remote host is documented as a single frontend session |
| Open-world optimized runtime | Published checks, Directory.Build.props, publish script | Native R2R headers, IL emission/JIT and independent assembly loading; no trimming or NativeAOT |
| Nullable checking | Solution compilation with warnings as errors | Enabled globally |
| Tabs | UiChecks, NavigationChecks | Pointer and accessible selection, accessible tab names/state/container, preview replacement/keep, inline close, Ctrl+W, overflow by path despite duplicate titles, keyboard reorder, delayed navigation races |
| Pane dragging | UiChecks, DockInputChecks, LayoutChecks | Pointer cancellation, capture loss, outside drops, join, center split, side/bottom before/after insertion, folded-bottom target, side-limit rejection, hidden-side/bottom restoration, refresh during drafts; generated state transitions and placement invariants |
| Pane sizing | UiChecks, DockInputChecks, LayoutChecks | Keyboard side resize, accessible canonical size/range/set action and out-of-range rejection, untouched opposite ratio, Escape cancellation, auxiliary weights/merge, semantic center minima; orientation is included in accessible name/help text |
| Pane focus/folding | UiChecks | 27 px side fold, restore focus, keyboard expansion, forward/reverse group traversal |
| Project opening/restoration | UiChecks, ProjectChecks | Project switch retains frame/documents; fresh-window profile restoration; native picker remains platform-owned |
| Markdown | UiChecks, native renderer source | Native AST, selectable content, headings/table structure, actual file/spec pointer navigation; no WebView |
| Settings | UiChecks, ProfileStore, SettingsWindow | Live theme, content font size and bounded/unbounded Markdown width updates; persisted values, fixed tab typography, modal geometry and save failure reporting |
| Git changes and diffs | ProjectChecks, GitUiChecks | Actual stage/unstage/diff through UI, unusual filenames, branch comparison, remote parity and untracked text line counts |
| Worktrees | ProjectChecks, GitUiChecks | Actual create/switch, removal cancellation/confirmation, main/dirty protection; host refuses active/locked removal |
| Excluded features | UI and host source | No Pi/AI service integration, functional terminal or editing surface; read-only text/diff viewing retained |
| Native running app | Window probe and .bench/prototype-empty.png | Published window renders at 1352 × 848 logical; current user-selected tools may differ from reference screenshot |

Logs: `.bench/prototype-checks.log`, `.bench/published-checks.log`,
`.bench/publish.log`, `.bench/format-verify.log`, `.bench/gui-final.log` and
`.bench/gui-final-errors.log`. Headless artifacts include Markdown, empty
workspace and Settings captures. Fixtures are disposable clones of existing
commits; the harness neither commits nor changes signing configuration.

## Visual comparison

The reference window was cropped from `original-workspace.png` at physical
rectangle `(68, 52, 2704, 1696)`. This matches the prototype's 1352 × 848 logical
window at 2× scale. `.bench/visual-geometry.json` records identical vertical
separator pixels `[486,487,1946,1947]` and horizontal pixels
`[78,79,142,143,1210,1211,1274,1275]` in the sampled frame spans.

Sampled RGB values also match exactly: center/header `(24,24,27)`, sidebar
`(16,16,19)` and tab strip `(9,9,11)`. These measurements prove the sampled frame
geometry/palette, not a zero-difference screenshot. Reference metadata and Git
contents have changed since its capture; excluded AI/editor/terminal actions
are intentionally absent. Native glyph rendering and control details still
need their own comparison.

## Remaining completion gates

The Changes toolbar now uses 24px scope/branch dropdowns and separate 20px
List/Tree toggles within the reference's 32px row and 12px horizontal padding.
Pointer checks verify both view switches and staged-scope filtering before the
existing Git mutation/worktree checks. The full Release suite and formatting
verification pass. Native capture `.bench/prototype-changes-toolbar-native.png`
confirms the selected toggle uses the muted fill rather than Fluent's accent.
Directory/basename styling is implemented and confirmed in the latest native
Git capture. Long basenames are constrained to preserve the stat column.
Muted tree selection styling is confirmed by
`.bench/prototype-file-selection-native.png`. File rows now have the reference's
24px height and 4px corner radius; Files/Specs content has 12px padding and the
extra Files toolbar is removed. Pointer checks inspect the selected template's
actual height and fill. Full Release checks and format verification pass.
These row/padding and Git path changes are included in `artifacts/SharpRail-Latest.app`.

Frontmatter regression verifies metadata never enters visible selectable text
and leaves no empty block. Markdig's frontmatter block inherits its code block,
which previously let metadata reach the code renderer. Full Release and format
checks pass. Latest app R2R publishing and strict/deep signature verification
pass; `.bench/prototype-latest-native.png` confirms the real spec begins with its
heading. Refreshed published R2R checks include this renderer change and pass.

Markdown headings now use the reference's strong border color; level-six
headings are muted. Blockquotes use a 40% accent border, muted body text and
left-only padding, retaining bright strong text. The alpha brush follows theme
changes through the shared palette. Full Release, format and current published
R2R checks pass. These changes are in the standard app. Native capture
`.bench/prototype-published-native.png` confirms the title-bar correction and
heading border. `.bench/markdown-border-colors.json` records its RGB `(63,63,70)`
at physical rows 234–235 in a 2704×1696 image (1352×848 logical at 2× scale).
Native blockquote fixture capture `.bench/prototype-blockquote-native.png`
confirms muted paragraphs, bright strong text and a border spanning both
paragraphs. `.bench/blockquote-colors.json` records border RGB `(70,116,48)`,
within one RGB level of the expected 40% accent blend over the background.
This verifies the palette and fixture rendering, not full screenshot equivalence.
Settings reference
inspection found frame/header/sidebar and unselected theme borders incorrectly
using the hover token. They now use `#3f3f46`; native samples confirm RGB
`(63,63,70)` for all three measured borders. The dialog measures 832×679 logical
for its 1352×848 owner. Evidence: `.bench/prototype-settings-border-native.png`
and `.bench/settings-border-colors.json`. Full Release and formatting checks
pass. Selected theme borders reuse the 40% accent palette; row hover fills now
use the reference token. These changes are published in
`artifacts/SharpRail-Settings.app`; its native Settings capture and full
published R2R checks pass.

Wrapped-list regression fails before the marker fix and passes afterward for
bullets and ordered numbers at both 14px and 24px. Markers align with the first
text line, use the document font size and 1.6em indentation; list outer margins
match the reference's 12px. Full Release and format checks pass. Native capture
`.bench/prototype-list-alignment-native.png` confirms wrapped bullets align at
the top. This change is included in the running Titlebar bundle, which also
removes the two extra title-bar project/layout icons. Project opening remains
available through Projects and the shortcut; layout commands are in the header
context menu. Full Release, formatting and strict/deep signature checks pass.

Markdown scrolling regression: a 40-paragraph preview in a 500×250 window
failed real wheel-input checks before the fix and passes after the control
selects the base ScrollViewer style key. Full Release checks and formatting
verification pass. The self-contained CoreCLR/R2R ScrollFix app published
successfully, passes strict/deep signature verification, and remains running
with SPEC.md open. Its native accessibility tree exposes a vertical scrollbar
with range 0–1821.5; capture `.bench/prototype-markdown-scroll-fixed.png` shows
the working viewport. Older running bundles retain the previous behavior.

- Compare native Markdown and the updated Settings modal against references at
  matching dimensions; quantify typography and control differences. Match
  remaining details rather than declaring them acceptable without evidence.
- Re-run required checks after further edits, republish, verify the signature,
  capture the current native build and leave it running.

iOS runtime/device validation and extension distribution policy are documented
future architectural gates in SPEC.md; this desktop prototype does not claim
to implement an iOS application or production extension loader.

## Embedded Ghostty terminal — macOS

Terminal execution is now in scope for local macOS workspaces. Ghostty 1.2.3 is
pinned to 6d2dd585a5d87fa745d48188dd096ca6e63014d0 and built with
`-Drenderer=metal`. Its [Metal implementation](https://github.com/ghostty-org/ghostty/blob/6d2dd585a5d87fa745d48188dd096ca6e63014d0/src/renderer/Metal.zig)
presents IOSurface-backed Metal textures through IOSurfaceLayer; checking for
CAMetalLayer would test the wrong presentation mechanism for this version.

Native verification on Apple M4 Pro passes live shell/cwd, ANSI output, AppKit
keyboard input, resize and retained-session checks. The presentation assertion
checks IOSurface type and nonuniform pixel contents, alongside parsed shell
output; it does not infer GPU rendering from Avalonia's backend. Evidence:
`.bench/ghostty-native-key-final.log` and `.bench/ghostty-final-native-lifecycle.log`.
The published Avalonia check also passes restored-profile startup, moving and
folding, switching between isolated workspaces, independent shell environments,
native view disposal and actual shell-process termination after tab closure.

The signed bundle runs with a real shell prompt in its docked terminal. Own-window
capture `.bench/ghostty-terminal-native.png` was inspected; only SharpRail window
114933 was captured. `.bench/ghostty-final-app-errors.log` contains one successful
Metal/IOSurface initialization and no errors. Bundle resources and terminfo live
under Contents/Resources; the native bridge lives under Contents/MacOS.
Strict/deep codesign verification and format verification pass. The native bridge
declares macOS 13.0 minimum; only this machine's macOS arm64 runtime is verified.
Remote sessions, other operating systems, Intel runtime and older macOS versions
are not verified by these checks. Restarts create fresh shells.

The final published regression suite passes with `SHARPRAIL_REQUIRE_R2R=1` and
`SHARPRAIL_TEST_GIT_SOURCE=/Users/commandertvis/IdeaProjects/thinkrail`:
`.bench/ghostty-final-regression.log`. This includes all 27 translated upstream
cases and the host, Git/worktree, docking, startup/restoration, UI and open-world
runtime checks. Final build/package log: `.bench/ghostty-final-package.log`.
Final formatting evidence: `.bench/ghostty-complete-format.log` (exit0).

Keyboard follow-up: terminal tab activation, overflow selection and creation now
transfer focus to the native terminal. Focusing its Avalonia container also
forwards focus. Previous direct `keyDown`/PTY injection checks did not establish
working responder routing. The native Avalonia regression now clicks the tab and
terminal through AppKit, verifies first-responder ownership, and executes commands
using dispatched keys, including Backspace and Ctrl+C. New-terminal input is also
verified without clicking the body. With `SHARPRAIL_CHECK_OS_INPUT=1`, key events
go through `CGEventPostToPid` targeting only the test process. The published build
passes this check and the existing Metal pixel/session lifecycle checks:
`.bench/keyboard-published-native.log`. Publish and strict signature validation
pass; formatting passes in `.bench/keyboard-final-format-2.log`.
The full Git-backed regression suite also passes in `.bench/keyboard-suite.log`.

The subsequent live typing failure was a mutable AppKit input buffer: retaining
the callback string allowed it to be cleared before forwarding to Ghostty. The
bridge now copies that text. The user confirmed typing works after this fix.
The native regression supplies a mutable buffer, clears it after insertText,
and requires the composed command to execute. Temporary keyboard diagnostics
have been removed.

Image paste now converts PNG/TIFF clipboard images to distinct PNG files under
the active profile and pastes quoted paths. The native tests use an isolated
pasteboard, verify readable saved images and text paste, and exercise a profile
path with spaces and an apostrophe. Theme tests sample the actual Metal IOSurface
pixels for dark and light UI background colors while retaining the same terminal.
Final native evidence: `.bench/terminal-paste-theme-final-native.log` (pass).
The tab activation test uses its accessibility selection action; body input and
Command-V still pass through AppKit, and ordinary keys use targeted macOS events.
Standalone native checks pass in `.bench/terminal-paste-theme-standalone.log`.
The published R2R/Git-backed full suite passes in
`.bench/terminal-paste-theme-suite.log`; formatting passes in
`.bench/terminal-paste-theme-final-format.log`. The clean signed app was rebuilt
and reopened without diagnostic logging.

## macOS Scintilla / Skia integration — 2026-09-28

Scintilla 5.6.7 now supplies the editable document engine for macOS text tabs.
A custom C++ Surface forwards drawing and measurement to SkiaSharp 3.119.4;
Avalonia replays immutable pictures. No Cocoa/CoreGraphics editor view is embedded.

- Native arm64 and x86_64 dylibs build; custom native sources pass
  `clang++ -std=c++17 -Wall -Wextra -Werror -fsyntax-only`.
- Focused Release checks pass in `.bench/scintilla-final-editor-checks.log`.
  Typing, multibyte/emoji deletion, selection replacement, macOS undo/redo,
  clipboard, read-only enforcement, viewport scrolling, rendered output and
  independent buffers exercise the real native engine.
- Local and authenticated gRPC save checks cover UTF-8/BOM/CRLF preservation,
  executable permissions, conflicts, stale workspace, traversal, symlinks,
  missing files, cancellation, authentication and temporary-file cleanup.
- Workbench checks cover preview promotion with continued typing/focus, dirty
  tab/window close protection, editor identity across reopening/tab switching,
  Cmd+S and preserving edits on an external-file conflict.
- Full suite with Git fixtures enabled stops at ChangesE2E.cs:29. An untouched
  HEAD export reproduces the identical timeout: `.bench/scintilla-checks-4.log`
  and `.bench/scintilla-baseline-checks.log`. No claim that the full Git-enabled
  suite passes. Its earlier host/Git/worktree checks pass before this UI failure.
- Full suite without `SHARPRAIL_TEST_GIT_SOURCE` passes, with Git-dependent
  scenarios explicitly skipped: `.bench/scintilla-full-no-git.log`.
- Canonical publication passes (`.bench/scintilla-final-publish.log`), as do
  strict/deep codesign verification and published focused checks with
  `SHARPRAIL_REQUIRE_R2R=1` (`.bench/scintilla-published-editor-checks.log`).
- Native packaged window 115739 / process 76979 was captured and inspected in
  `.bench/scintilla-native.png`, using only `.bench/scintilla-native-fixture`
  and `.bench/scintilla-native-profile`. ASCII, accented/Greek/Japanese text and
  emoji render in the editor; this is a rendering check, not proof of native IME
  composition or accessibility. The isolated preview remains open for review.

The port does not yet provide Lexilla syntax highlighting, completion UI, full
IME preedit, an accessibility text provider, or complex-script/bidirectional
shaping. Markdown source and Git diffs retain their read-only views. No benchmarks,
commits, pushes, agents, or external posts were performed.

## Ghostty renderer selection and library extraction — 2026-10-01

Settings → Terminal now selects Native Metal, Metal texture or Skia. The integration
lives in `src/Ghostty.Avalonia` without SharpRail project dependencies. Building
with root build and package props disabled passes with zero warnings/errors:
`.bench/ghostty-standalone-complete.log`. Its README documents reuse, native build
prerequisites and features not implemented by the Skia path.

Focused checks pass in `.bench/ghostty-skia-complete.log`: real Skia
colour pixels, wide/combining text, mouse press/release encoding, keyboard,
selection, clipboard, scrollback, grid resize and a real host PTY retaining its
PID across a view restart. Pointer selection of all three Settings choices persists
and reattaches without replacing shells; displaced clients remain detached.
Cursor-position reports verify combining and emoji clusters occupy the correct
cells after initialization and reset. `.bench/ghostty-skia.png` was inspected.
Physical macOS IME behavior and complete native/Skia visual parity are unverified.

The terminal subset passes in `.bench/ghostty-three-renderers-terminals-final.log`,
including host local/remote PTY checks, terminal/bottom-panel translations and
Skia/Settings regressions. An earlier run timed out opening a document in
`BottomPanelE2E.SquareActions`; the separate rerun passed without a test change.
The native shell/Metal probe passes in `.bench/ghostty-texture-native-probe.log`.
Native Avalonia local/remote checks pass with the Metal compositor in
`.bench/ghostty-metal-regression.log`, including keyboard routing,
image/text paste, theme pixels, resize, session retention and disposal.

The texture control imports immutable, GPU-copied IOSurfaces into Avalonia's
Skia compositor; no Ghostty NSView is attached to the window. Native checks pass
in `.bench/ghostty-texture-complete.log`: real AppKit keyboard and numeric-keypad input,
text/image paste, application shortcuts, ANSI/theme pixels, parent clipping and
an Avalonia overlay above the texture, Retina resize and detach/reattach. The
same run switches texture → Skia → texture on local and authenticated remote
sessions, preserving the shell PID and environment, interrupting a foreground
process with Control-C, and retaining its real exit status. The own-window capture
`.bench/ghostty-texture-composited.png` was inspected; its display profile is
converted to sRGB before pixel assertions. Physical IME and accessibility remain
unverified for the texture input bridge.

The full Git-backed regression run is blocked in the existing editor large-diff
fixture by `1Password: Could not connect to socket` and `failed to write commit
object`: `.bench/ghostty-renderers-full.log`. Signing was not bypassed. Full-suite
success is not claimed. Formatting verification is recorded in
`.bench/ghostty-three-renderers-format-complete.log`. The solution build succeeds
in `.bench/ghostty-three-renderers-build-complete.log`; four existing Avalonia XAML
warnings remain. Source changes have not been published
to the canonical app bundle or committed.

## Ghostty NSView versus Metal texture benchmark — 2026-10-01

Explicitly requested by the user. Reproducible harness and raw data are in
`.bench/ghostty-renderer-bench`; run `sh .bench/ghostty-renderer-bench/reproduce.sh`.
The HTML report is `measured/report.html`, with `measured/summary.json`, per-run
JSON, power/thermal metadata and source/binary fingerprints alongside it.

Five alternating fresh-process runs per mode, excluding warm-ups, on an Apple
M4 Pro with 24 GiB RAM and AC power. Both use Avalonia Metal, Ghostty 1.2.3 and a
960×540 point / 1920×1080 pixel window. The display reports a 120 Hz maximum.
The shared PTY producer draws 480 ANSI/Unicode frames at 60 Hz, then emits three
~64 MiB bursts. Capture is disabled during CPU/memory measurements. Latency uses
the producer's timestamp before its PTY write and the first matching own-window
pixel frame's ScreenCaptureKit WindowServer presentation timestamp.

| Metric | Hosted NSView | Metal texture |
| --- | ---: | ---: |
| Median presentation latency, 200 samples each | 14.3 ms | 34.7 ms |
| Presentation latency p95 | 21.5 ms | 46.3 ms |
| CPU during 60 Hz redraw, mean; 100% = one core | 8.8% | 19.7% |
| Process physical footprint during redraw, mean | 235.8 MiB | 353.0 MiB |
| Parsed output throughput, mean | 58.2 MiB/s | 56.0 MiB/s |
| CPU cost during output bursts, mean | 22.0 ms/MiB | 35.8 ms/MiB |

All initial samples are retained. One texture run had three 0.93–1.09 second
presentation stalls; their cause is unproven. An additional matched pair did
not repeat those stalls (median 12.9/33.6 ms, maximum 20.8/48.7 ms for NSView/texture);
its results are separate in `measured/confirmation.json`. Both modes reached roughly
3.2–3.3 GiB physical footprint after 192 MiB of output, which merits separate
memory investigation. Idle CPU ranges overlap. No new swap-outs or thermal/
performance warnings were recorded. This isolates one renderer control, not
the full workbench, relay or network. Latency is not keyboard-to-photon latency,
and throughput waits for terminal parsing, not every intermediate frame's
presentation. No product rendering code was changed for the benchmark.

### Texture optimization and scrollback RAM fix — 2026-10-01

Replaced the texture control's 16 ms polling with coalesced frame-ready
notifications, and per-frame export/import allocation with a two-IOSurface pool
and persistent imports. Superseded pending frames can be overwritten before
copying. Monotonic Metal shared-event completion values prevent reuse until
Avalonia's GPU snapshot finishes. Resize retires imports; disposal removes
notifications and drains the current update. The producer GPU copy and its
completion wait remain to protect Ghostty's render target.

The large memory growth in the preceding benchmark was traced with `vmmap` and
`malloc_history` to Ghostty's page allocator (5,197 allocations totaling roughly
3.15 GB in the diagnostic run). Pinned Ghostty 1.2.3 recycled enlarged scrollback
pages using a smaller standard layout, losing their allocation size and capacity.
`Native/ScrollbackMemory.patch` preserves both and restores the current logical
column width. This fixes the leak in both native Metal renderers, without reducing
the configured scrollback limit. Buffer pooling alone did not fix this growth.

Final evidence: `.bench/ghostty-texture-optimization/report.html`, `summary.json`,
`fingerprints.json`, and raw trials under `matched-two-slot` / `ram-two-slot`.
The same M4 Pro/24 GiB, 960×540 at 2×, direct-PTY harness and workload were used.
Five alternating original/updated texture pairs ran on AC power, with two
supplemental patched NSView trials. No capture runs during CPU/RAM phases.
All 200 latency samples per texture version are retained; presentation is measured
at WindowServer, not physical pixels. Means unless otherwise stated:

| Metric | Original texture | Updated texture | Patched NSView (2 trials) |
| --- | ---: | ---: | ---: |
| Median presentation latency | 37.2 ms | 29.7 ms | 13.3 ms |
| Presentation p95 | 50.1 ms | 35.8 ms | 20.5 ms |
| Redraw CPU; 100% = one core | 18.2% | 15.2% | 9.0% |
| Redraw physical footprint | 351.4 MiB | 353.2 MiB | 232.1 MiB |
| Peak footprint across 192 MiB of output | 3358.2 MiB | 363.1 MiB | 243.0 MiB |
| Parsed output throughput | 50.9 MiB/s | 91.0 MiB/s | 69.1 MiB/s |

A separate RAM harness uses identical executable code with preserved original or
updated libraries. Three alternating texture pairs keep the process alive after
the same workload, wait approximately 15 seconds, dispose the terminal, and wait
10 more seconds. Two supplemental patched NSView runs use the same harness:

| Physical footprint, mean | Original texture | Updated texture | Patched NSView |
| --- | ---: | ---: | ---: |
| After output settles | 3358.1 MiB | 367.2 MiB | 251.1 MiB |
| 10 seconds after terminal disposal | 3131.8 MiB | 130.4 MiB | 100.5 MiB |
| After diagnostic forced GC | 3129.9 MiB | 128.6 MiB | 98.9 MiB |

The product does not force GC. RAM after scrolling is about 89% lower, and RAM
retained after disposal about 96% lower. Normal redraw RAM is essentially
unchanged. Median texture latency improves about 20% and redraw CPU about 16% in
the matched batch; NSView still has lower latency and redraw cost. The benchmark
isolates one control, excluding workbench/relay/network overhead. Background
activity was not exclusively isolated. Neither final batch recorded new swap-outs
or thermal/performance warnings (four swap-ins each). Power is monitored once per
second and a temporary caffeinate assertion prevents idle sleep. Earlier
exploratory batches interrupted by a logged 233-second system sleep or a
power-source change are excluded, as are the preliminary three-buffer results.
These exclusions do not diagnose the separate one-second stalls in the older
benchmark above.

Verification passes:

- Final `--native-texture`: imported pixels, overlays/clipping, input/clipboard,
  16 repeated resizes with at most two imports, repeated repaint final pixels,
  remounting, queued disposal and retained local/remote shells
  (`two-slot-verified.log`).
- Native shell/Metal probe (`native-probe.log`) and Skia/Settings regression
  (`skia-check.log`).
- All 183 selected Zig checks, including enlarged-page recycling after a column
  resize (`page-tests-system-cpp-3.log`). The saved `run-page-tests.mjs` and
  `page-test-command.json` use SDK C++ headers/runtime for this test invocation,
  avoiding the pinned Zig test driver's libc++ INFINITY header build failure;
  no installed toolchain or SDK was edited.
- Release solution build with zero warnings/errors (`solution-build.log`),
  formatting verification (`format-final.log`) and `git diff --check`.

The earlier full-suite 1Password signing blocker remains outside these focused
checks; no signing bypass was used. No commits, pushes or app publication.

### Texture steady-memory attribution — 2026-10-01

User requested attribution of the remaining ~121 MiB texture/NSView redraw gap.
Allocation evidence is in `.bench/ghostty-memory-attribution/report.html` and
`summary.json`; the earlier latency/throughput benchmark remains unchanged.
Existing uninstrumented maps one second after the flood give a 120.0 MiB mean
gap: 84.4 MiB driver-owned graphics, 23.9 MiB IOSurfaces, 3.7 MiB other mapped
GPU memory, and approximately 8 MiB native heap/runtime/other CPU memory.
There was no saved map at the exact earlier redraw sample; these totals explain
the comparable gap, rather than reconstructing that old reading byte for byte.

Fresh steady-redraw resource traces at the same 1920×1080 physical size identify
two export surfaces (15.84 MiB), two additional Skia snapshot textures
(16.19 MiB), and typically one additional window drawable (8.09 MiB).
Imported wrappers share the export IOSurface IDs and do not add another copy.
Ghostty's original three targets and six atlas textures are common to both modes.

The largest component is 68 MiB of copy-related GPU driver working storage:
texture has 41 driver-owned 4 MiB allocations versus NSView's 24. Command
submission traces show first activation during the exporter copy. Suppressing
only that blit in a diagnostic leaves 41 blocks, moving activation to Avalonia's
snapshot. Suppressing all blit encoders leaves 24 blocks, exactly 68 MiB fewer.
These suppression runs intentionally invalidate displayed content; they are
attribution experiments, not usable optimizations. The private driver's exact
internal names/purposes for those blocks are not exposed.

A two-command-buffer export queue and sharing the active Avalonia queue both
retain the 41 blocks. Sharing Ghostty's own queue stalls completion and was
rejected. Removing only the exporter copy therefore does not guarantee recovering
the 68 MiB: Avalonia's current snapshot also requires copying. Pooling reduces
allocation churn while preserving backing storage and these GPU copy operations.

The final ordinary trace completes 948 composition updates. Profiling builds
pass with zero warnings/errors. Live-resource traces preserve retained-return
ownership and hold only weak resource references; snapshots deduplicate shared
IOSurface backing. Tracing/inspection perturb CPU/RAM, so their totals do not
replace the original benchmark. No product changes, commits, pushes or publication.

### Direct texture sampling and further RAM savings — 2026-10-01

Implemented direct Skia sampling of Ghostty's completed Metal target. Removed the
export queue, two export surfaces, snapshot imports and shared events. Native
leases retain source textures; a renderer hook waits for external readers before
encoding a new write. The compositor wraps the Metal texture without copying and
flushes/submits synchronously before returning its read lease. This preserves
GPU ownership while trading some overlap and per-draw CPU work for smaller RAM.

After unlocking, three fresh RAM lifecycle trials measured mean redraw footprint
262.0 MiB (258.4–265.8), versus saved pooled texture 356.9 MiB and NSView 236.2 MiB.
This saves 94.9 MiB / 26.6%; the NSView gap is 25.8 MiB, with a worst trial gap
29.6 MiB. Mean peak flood footprint is 275.2 MiB versus NSView 247.1 MiB; even
the highest trial gap is only 31.7 MiB. The below-60 MiB goal is met. Ten seconds
after disposal averages 118.0 MiB versus NSView 100.5 MiB. Idle settled footprint
is lower and variable (161.9–170.9 MiB); it is not the basis for active RAM claims.
The initial single pilot remains separately recorded in ram-1.

Redraw CPU averages 18.8% of one core (16.9–22.0), versus previous texture 15.2%
and NSView 9.3%. Three separate presentation trials provide 120 samples: median
24.5 ms and p95 30.5 ms, versus saved texture 29.7/35.8 ms and NSView 13.3/20.5 ms.
This measures PTY output to WindowServer timestamp, not physical pixel latency.
Existing baselines were reused, not rerun. All six trials use AC power, scale 2,
119×30 cells, 480 redraws at 60 Hz and three 67,113,264-byte output bursts.
Background machine activity was not exclusively isolated.

Final implementation resource inventory confirms 24 driver-owned 4 MiB allocations instead
of 41 (68 MiB removed), no two-surface export pool (15.84 MiB removed), and no two
Skia snapshot textures (16.19 MiB removed). It completed 498 GPU draws. This
inventory ran behind a locked desktop with foreground activation skipped; its
CPU/RAM totals are not used as comparative performance results. Rendering remains
enabled, unlike the preceding blit-suppression attribution diagnostics.

Final visible texture checks pass normally and with Metal API validation enabled:
pixels, opacity, overlays/clipping, theme, keyboard, clipboard, repeated
resize/repaint, remount/disposal and retained local/remote shells. The first
post-unlock check failed click-to-focus; both subsequent full runs passed without
code changes. Its cause is unconfirmed. Source/native/managed hashes match the
final allocation inventory, including the binaries used by both harnesses.
Solution compilation passes with four existing Avalonia XAML warnings; format
verification and diff whitespace checks pass.
The standalone native Metal probe passes with the display kept awake
(`native-probe-awake.log`): shell/cwd, ANSI, input, presentation, resize and session retention.

Evidence: `.bench/ghostty-direct-texture/final-report.html`, `final-summary.json`,
`memory/metadata.json`, `presentation/metadata.json`, `inventory/metal.json`,
`inventory/vmmap.txt`, `texture-check-unlocked-2.log`, `texture-metal-validation.log`,
`solution-build.log`, `format.log`. No commits, pushes or publication.

### Texture default, Skia fallback and stored benchmarks — 2026-10-01

SharpRail now offers texture (default) and Skia. Legacy `native` preferences
normalize to texture. The reusable NSView control remains in Ghostty.Avalonia
and its benchmark/native probe, with no NSView construction or choice in app code.
Texture creation/drawing failures switch the view to Skia on its existing host
session without changing the preference. Both local and remote fallback sessions
retain PID/variables, support Ctrl-C and report real exit status when Avalonia is
forced to software rendering (`fallback-check.log`).

Benchmark sources are stored under `benchmarks/ghostty`: presentation, memory
lifecycle, Metal/IOKit attribution, C workload, build/run/summary scripts and
the saved final measurement aggregates. All three projects compile with zero
warnings/errors; no new benchmark measurements were needed for these app changes.

Release solution/checks builds, formatting, diff whitespace, Settings/migration,
and all headless terminal checks pass. The final texture suite passes native
input/clipboard, GPU pixels, opacity, clipping, resizing, local/remote switching,
and a library-only NSView mutable-input fixture (`final-texture-presented.log`).
GPU completion alone did not ensure a current window screenshot: the opacity
check now waits for a draw and polls the expected captured pixel with a deadline.
A temporary GPU sample confirmed the expected blend; diagnostic readback/logging
was removed and renderer source still matches the measured implementation.

The broader workbench GUI rerun remains incomplete: it stops at the explicit
active-window/keyboard-focus gate (`final-workbench-texture.log`). Earlier attempts
lost portions of synthetic typing while another checkout's GUI checks were
running. Do not report that suite as passed. Headless terminal evidence is in
`final-headless-terminals.log`; all logs above are under `.bench/ghostty-direct-texture`.

### OSC 52 terminal clipboard (2026-10-02)

The Skia bridge now registers libghostty-vt's clipboard callbacks. Both renderers
allow text writes and ask before reads; invalid UTF-8 leaves the clipboard intact.
`--native-osc52` passes fragmented ST/BEL, Unicode, selectors, malformed/cancelled
data, NUL, 32 KiB and empty payloads, approved/denied reads, repeated consent and
reset checks, plus real local/remote PTY writes and read replies in both renderers
and writes after renderer switches. Tests isolate the AppKit pasteboard and script
the confirmation decisions. Cancelled Metal reads complete with empty data so
Ghostty releases their pending requests. Full libghostty's NUL-string and reply
terminator differences are documented in the control README.

Release solution build, formatting verification and the full checks runner with
`SHARPRAIL_TEST_GIT_SOURCE=/Users/commandertvis/IdeaProjects/thinkrail` pass.
The native protocol probes run saved shell scripts through atomic text input,
avoiding lost characters from long synthetic keyboard sequences. Evidence is in
`.bench/osc52-build-final.log`, `.bench/osc52-format-final.log`,
`.bench/osc52-skia.log`, `.bench/osc52-metal-final.log` and `.bench/osc52-checks.log`.

### Terminal web links (2026-10-02)

Both renderers use Command-hover/click on macOS (Ctrl elsewhere) for web URLs.
Skia detects soft-wrapped HTTP(S) URLs from native row metadata; Metal retains
Ghostty's built-in matching/open action and now propagates hover cursor state and
modifier changes through Avalonia. Skia regression checks pass for wrapped and
reflowed URLs, hard newline boundaries, punctuation, the hand cursor and visible
underline pixels on stationary modifier changes. Existing selection/input checks
also pass. Evidence: `.bench/url-skia-final.log`.

The native texture run fails the previously observed changed-theme-background
pixel assertion, before completing the suite (`.bench/url-native-texture.log`).
Actual system-browser opening and Metal link pixels have not been verified.
No live app restart, publication, commit or push was performed.

### In-process Ghostty execution (2026-10-05)

Local Metal attaches directly to the app-owned PTY service through Ghostty's
external-I/O backend. No local relay child or RPC server is configured, and the
UI no longer references Host.Remote or ASP.NET Core. An HTTP diagnostics probe
requires zero local requests across launch, input, resize, takeover/take-back,
renderer switching, clipboard and close, with a remote positive control.
Metal acceptance rejects silent fallback to Skia.

Release solution build, --terminals, --native-direct, --native-texture,
--texture-fallback and formatting verification pass. Native checks preserve shell
PID and state through renderer changes and takeover, propagate actual PTY size,
interrupt a foreground process and report exit code 7. The broader texture suite
also passes GPU import, theme/ANSI pixels, clipping, input, remount/disposal and
library NSView checks. The earlier theme-pixel failure did not recur.
Evidence: .bench/in-process/{build-final,format-verified,terminals,direct-complete,
native-texture,fallback-verified}.log. Build retains four existing XAML warnings.
The complete checks runner also passes with
`SHARPRAIL_TEST_GIT_SOURCE=/Users/commandertvis/IdeaProjects/thinkrail`;
evidence: `.bench/in-process/full.log`. Spec-graph validation passes.
