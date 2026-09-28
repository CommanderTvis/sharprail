# Prototype completion audit

The goal remains active. Passing functional checks does not prove the complete
visual and docking contract. This ledger preserves the original scope and
identifies the evidence still needed before declaring completion.

The authoritative upstream checkout is `/Users/commandertvis/IdeaProjects/thinkrail`.
The `riirn` worktree is not upstream. Reference authorities are `SPEC.md`, Thinkrail's
`apps/web/src/shell/layout/SPEC.md`, its actual `Workbench.tsx`/`model.ts`,
`SettingsDialog`, `MarkdownPreview`, project views, and
`prototypes/ThinkRailNative/screenshots/original-workspace.png`. When prose and
the current renderer disagree, record the distinction rather than silently
substituting an easier behavior.

| Requirement | Current evidence | Completion limit |
| --- | --- | --- |
| C# host independent of Pi/AI; direct local calls and code-first gRPC remote | Host projects, `ProjectChecks.cs`, published local/remote parity, cancellation and Git parity checks | Functional prototype evidence; no production remote deployment claimed |
| Optimized open-world CoreCLR/R2R, nullable checking, single latest package | `Directory.Build.props`, `scripts/publish.sh`, published checks require R2R and exercise dynamic assembly loading/JIT | Preserve these checks after every implementation change; iOS execution/distribution remains the documented future gate |
| Functional tabs, preview/keep, close/context actions, reorder/overflow/focus and deferred navigation | `UiChecks.cs`, `NavigationChecks.cs`, `DockInputChecks.cs`; native double-click and drag captures | Final contract audit must cover the combined interaction surface, including all drop collisions and viewport changes |
| Persistent settings and XAML where it fits | Five compiled XAML files; settings persistence/theme/font/width/preset checks and native Settings capture | Menu/control typography comparison against the reference is still incomplete |
| Translated upstream E2E tests | Eight preview-tab cases, two Markdown link/image/anchor cases, one independent preview-slot case; real pointer input and delayed host responses | Full suite translation remains active; per-case inventory in `E2E.md` |
| Native Markdown preview with actual scrolling and selectable content | Markdig renderer checks; wheel/anchor/navigation tests; native scroll and rendering captures | Reference typography, spacing and complete representative-document comparison still need final visual review |
| Opening projects, workspace/window restoration, Git changes/diffs and worktree workflows | `ProjectChecks.cs`, `GitUiChecks.cs`, isolated Git fixtures, restoration/navigation checks | Functional behavior is covered by current checks; visual fidelity of the corresponding panels/dialogs still needs final review |
| Exact frame geometry, docking grammar, panes, alignment, fold/show/restore and resize behavior | `LayoutChecks.cs`, pointer/menu/keyboard checks, source-verified side projection and collision regressions | Auxiliary stack projection with multiple folded/expanded groups and narrow viewports needs a source comparison; verify any resulting gaps |
| Screenshot-perfect layout and details | Matched sampled geometry/palette, original screenshot and native own-window captures recorded in `VALIDATION.md` | Sampled matches are insufficient: native glyph/control/menu differences remain unresolved or unverified |
| Publish and show the finished app; no Pi/AI, terminal/editor functionality or benchmarks | Canonical `artifacts/SharpRail.app`, published runtime checks, own-window native captures; implementation scope in `SPEC.md` | The current package is a working prototype, not yet proof that all preceding completion limits are resolved |

Next work is bounded by these remaining gates:

1. Auxiliary stack sizing, folded weights, spacers and viewport compression have
   source-library regression and native rendering evidence in `VALIDATION.md`.
   Preserve those checks during the remaining audit.
2. Finish the requirement-by-requirement docking and keyboard/menu audit; add
   observable regressions for concrete gaps rather than counting existing tests.
3. Compare representative native panels, dialogs, Markdown and menus at matching
   dimensions/content; resolve the recorded typography and control differences.
4. Re-run the applicable checks, publish the canonical package, verify its
   signature/runtime and inspect it running after the final changes. Only then
   evaluate the full goal for completion.
