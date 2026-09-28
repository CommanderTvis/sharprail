# Prototype validation

Status on 2026-09-28: thirty-seven translated upstream E2E cases pass in the full
Release and published-runtime suites with Git/worktree integration enabled. Formatting and
`git diff --check` pass. The canonical `artifacts/SharpRail.app` was republished
with non-composite ReadyToRun and passes strict/deep signature verification.
The full published-runtime suite also passes, including open-world loading and
Git/worktree integration (`.bench/theme-reload-published-checks.log`).

Native display availability recovered: CoreGraphics reports two active displays
and CoreVideo display-link creation succeeds. The canonical package is running
(PID 15312); its 1352x848 window was captured and inspected in
`.bench/mounted-workbench-native.png`. Earlier launch failures with RenderTimer
`-6661` occurred while no displays were active, matching
[Avalonia issue 18895](https://github.com/AvaloniaUI/Avalonia/issues/18895).
Earlier captures and running-app statements below are historical evidence.
The complete visual/docking fidelity objective and upstream suite translation
remain active; the evidence below does not prove the full reference contract.

## Current evidence

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
