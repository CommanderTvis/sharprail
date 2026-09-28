# SharpRail prototype contract

Reference: Thinkrail's apps/web/src/shell/layout/SPEC.md and its Balanced
workspace, original-workspace.png, SettingsDialog, MarkdownPreview and project
dialogs. The current Balanced layout is the visual target. AI controls are
excluded. Editor and terminal execution remain nonfunctional; tab, toolbar and
pane chrome needed for visual and docking fidelity remains in scope.
Keep the same C# host architecture and optimized open-world CoreCLR/R2R package.

## Runtime and managed extensions

Open-world managed extensibility is a product requirement, including on iOS:
extensions may contain C# assemblies and dependencies unknown when the app is
built or published. A predefined extension catalog or requiring every extension
to be compiled into the app does not satisfy this requirement. Extensions are
not limited to a small scripting language. Substantial interpretation overhead
is acceptable when required by the platform.

Desktop keeps CoreCLR, optimized ReadyToRun, tiered compilation/PGO and runtime
assembly loading. Keep nullable reference checking enabled. Packaging must
preserve the metadata and managed API surface required by extensions; closed-world
NativeAOT cannot be the sole execution runtime for this extension model.

Publish the latest reviewed build to the single canonical macOS package
`artifacts/SharpRail.app`. Do not create feature-named or versioned review
bundles; the user wants one latest package.

On iOS, run bundled code precompiled where supported and interpret newly loaded
managed code without JIT or dynamically generated executable native memory.
Extensions have no direct access to iOS APIs. Their supported integration surface
is the platform-neutral C# host contract; the host remains independent of Pi and
AI. This API boundary is a design requirement, not a claim that arbitrary managed
code in the same process is automatically sandboxed.

Avalonia remains the UI stack. CoreCLR remains the preferred runtime; no migration
to Mono is authorized by this specification update. Runtime selection can differ
by platform without replacing the shared C# host or Avalonia UI. Mono AOT with
interpretation is an iOS fallback candidate if the preferred CoreCLR path cannot
meet the requirements. The current desktop prototype does not establish iOS
compatibility or implement an extension loader.

Before committing to the iOS runtime, a Release build must run on a physical
iPhone with JIT unavailable, load an assembly absent from the published app,
resolve its managed dependencies and invoke it through the host contract.
Exercise reflection, generic types and asynchronous calls across that boundary.
Verify the exact Avalonia/runtime/toolchain combination; simulator execution or
MAUI support alone is insufficient evidence. No benchmark is required for this
acceptance check.

App Store distribution is intended. Interpretation and the absence of direct
iOS API access do not by themselves establish App Store approval. Evaluate the
extension distribution model against Apple's current rules separately from the
technical device check; do not promise approval or silently remove open-world
extensibility to obtain it.

Technical references checked on 2026-09-28:

- [Avalonia iOS support](https://docs.avaloniaui.net/docs/platform-specific-guides/ios).
- [Microsoft runtime and compilation guidance](https://learn.microsoft.com/en-us/dotnet/maui/deployment/runtimes-compilation?view=net-maui-10.0): .NET 10 Mono and .NET 11 CoreCLR iOS execution models; MAUI guidance is not proof of Avalonia integration.
- [Mono interpreter configuration](https://learn.microsoft.com/en-us/dotnet/maui/macios/interpreter?view=net-maui-10.0).
- [Apple App Store Review Guidelines](https://developer.apple.com/app-store/review/guidelines/), particularly 2.5.2 and 4.7.

## Scope

Use compiled Avalonia XAML for static window layouts, reusable styles and
templates where it fits. Keep runtime docking construction, host wiring,
interaction logic and genuinely dynamic content in C#. Preserve observable
layout, shared theme-brush updates, accessibility and persistence during this
migration; C# as the implementation language does not require C#-only UI markup.

Functional document and tool tabs: select, close/hide, preview/keep, reorder,
overflow search, middle-click close, context actions, keyboard navigation and
focus. A filesystem tree opens Markdown as a native selectable preview and
other text as a read-only document, without an editor or terminal implementation.

Projects: native directory picker and explicit host-path dialog (remote);
recent/open project navigation; per-project workspace/worktree selection;
persist restored selection. Git: working/staged/untracked/renamed files,
branch comparison, read-only diffs, refresh and worktree listing/switching.
Stage/unstage and create/remove worktrees are supported. Removal requires an
explicit confirmation and Git refuses dirty, active, main or locked worktrees.
No commits, pushes, Pi or AI integration.

Settings: Thinkrail dialog proportions and styling, live dark/light/system
appearance, Markdown/document line width, layout presets and limits,
custom layout capture/reset, bottom alignment, and project preferences.
No inactive controls pretending to configure out-of-scope providers or chat.

## Layout contract

One persisted frontend frame owns center topology, auxiliary groups, tool
placement, side/bottom ratios, visibility/folds and restore targets. Workspace
views own canonical document membership, order, preview slots and selection.
Switching workspaces retains geometry. Removing/merging groups remaps every
retained workspace; closing the final tab preserves empty groups.

Balanced: left 18%, right 28% with Specs/Files above Changes/Review (1.25:1);
bottom 30% below center. Center splits are binary, maximum four leaves,
320px minimum width/180px minimum height for interactive split/resize.
Auxiliary groups default to six per side and three bottom, configurable 1–32.
Auxiliary panel minimum extent is 120px; folded constraints use 27px divided
by the whole stack extent, with separator space removed during projection. Expanded
side regions have an 8% width minimum and snap closed below the 4% midpoint,
retaining their last expanded width. Bottom minimum height includes the 120px
body and 27px reference header allowance; resize snaps closed below half that
minimum and preserves the expanded height for restoration. Bottom height
maximum 70%, center/center+left/center+right/full alignment. Side resize projects
through the alignment's outer and inner panel groups: center minimum can push
neighboring panes in the same group, while a side outside the active inner group
stays fixed. Bottom spans follow that live projection. Only the dragged side's
ratio is committed, capped at 70% and below the opposite saved ratio's remaining
space; untouched compression and viewport constraint adjustment remain local.

Pointer drag drafts do not mutate persistent state. Drop performs one legal
move: strip insertion/join, center half split, side before/after group, bottom
before/after group or hidden-region restore. Tools are auxiliary-only;
documents are center-only. Escape/lost capture/outside drop cancel; no duplicate
resources, illegal previews or partial changes. Valid targets show accent hints
and the hovered target shows the actual destination. Existing groups may stay empty.
Drop selection considers every legal tab insertion half, strip append, pane
creation/join and restore target independently. Overlapping targets use the
reference's mean distance from the pointer to their four corners; only the
winner is active. A disabled insertion target does not disable a legal enclosing
header append. Drag eligibility is cached only for the current projection epoch;
local changes and cancellation discard it.
Resize hides sides below minimum and preserves restore size. Hidden bottom
reserves no space; while dragging an eligible tool it exposes a 24px drop zone.
That zone follows bottom alignment and excludes hidden side restore rails.
Within its band it wins overlapping side targets and is the only active drop
highlight; hidden side rails retain their lower corners.

Keyboard/context menus provide selection, keep, close/hide, movement, split,
new/remove/merge group, fold, visibility, alignment and resizing. Ctrl+F6 cycles
visible groups. Mod+Shift+J toggles bottom. Tab headers are 32px, with active
accent underline, bounded widths, hidden horizontal scrollbars, active reveal
and searchable overflow. Search appears only when the strip overflows, as a
288px dropdown aligned to the magnifying-glass button, with title/icon rows,
path-aware filtering, keyboard navigation, single-click selection and Escape
dismissal. Singleton tools have no inline close glyph. Specs compact spaced
em/en dashes to middle dots and reveal the spec type on hover or keyboard focus.

Application state lives in `~/.sharprail`, independent of the opened project.
The default profile preserves settings, projects and workspace layouts there;
`SHARPRAIL_PROFILE` remains an explicit override for isolated checks.

## Markdown

CommonMark/GFM headings/anchors, paragraphs, emphasis, links, inline/fenced code,
lists/tasks, blockquotes/callouts, tables, rules and images. Selectable text,
relative document navigation, safe external link launch, local image loading,
frontmatter removal and bounded configurable line width. No WebView or HTML
application runtime. Read-only source/diff viewing is not an editor.

## Verification

Meaningful pure layout transition/invariant tests, pointer cancellation/drop
tests with headless Avalonia input, local/remote host parity, git integration in
isolated fixture repositories, settings persistence, project switching, and
Markdown structure tests. No benchmarks unless explicitly requested.
Build/format checks, R2R header + runtime assembly loading/code emission checks,
published GUI smoke and reference screenshots at matching logical dimensions.
Native typography/compositing differences must be measured and reported;
do not declare pixel-perfect equivalence without comparison evidence.
