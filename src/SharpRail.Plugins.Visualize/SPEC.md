---
id: module-plugin-visualize
type: module-design
status: active
title: Visualize — a terminal agent's live drawing surface, as a builtin plugin
parent: architecture
references: [module-plugin-api, module-plugin-ui-kit]
tags: [plugins]
---

# Visualize — a terminal agent's live drawing surface, as a builtin plugin

Upstream: packages/plugin-visualize/SPEC.md @ 0304a543e (CommanderTvis fork)

## Responsibility

Give an agent in a SharpRail terminal a place to draw. The `visualize` MCP tool takes the schema ThinkRail's pi
visualize extension also uses (a diagram with raw Mermaid, or comparison cards), records the call per terminal,
and publishes it, where it renders as a live companion pane beside that terminal. Calling again replaces that
terminal's view in place: many agents can each hold their own view at once, which is the point.

Three projects under `Contract`, `Host` and `UI`: `SharpRail.Plugins.Visualize` (the manifest and the wire contract, shared by both halves),
`SharpRail.Plugins.Visualize.Host` and `SharpRail.Plugins.Visualize.UI`. The plugin is builtin and on by default.

## Boundary

- Owns: the in-memory per-terminal store (`VisualizationStore`: record, read and per-workspace maps, keyed
  workspace and tab, `Revision` counting rewrites so a client can tell an update from an echo), persistence over
  the host context's `ReadState`/`WriteState` (`visualizations`, keyed workspace then agent session id), and the
  `visualize` tool registered through `Tool`. The runtime binds the tool to the terminal whose MCP token made the
  call and hands it as `PluginToolContext.Terminal`, so the tool never resolves its owner itself; a call from
  outside a terminal is refused. The tool validates the call's shape beyond what the parameters record captures
  (`VisualizeParams.ShapeError`: a diagram needs non-blank Mermaid, a comparison a non-empty list of named
  options) and answers with the title, the revision, and the fact that calling again updates in place, the
  sentence that teaches the agent the iteration loop. The parameters record's `[Description]` attributes become
  the schema's field descriptions.
- The renderer decides, and the agent hears it. Mermaid is parsed where it is drawn, so whether a diagram is
  valid is not something the host can answer. The tool run therefore waits (5 s) for a client to report, through
  the `report` method, what it made of that revision, and answers with the parse error as an error result when
  it failed. A refused drawing is rolled back: the last one that rendered is restored and published again, so an
  iteration's typo does not cost the user the picture they had, and the next attempt reuses the revision number
  the pane never showed. No client watching is not a failure: the wait times out and the drawing stands. A
  comparison has no Mermaid to fail on and settles the moment it is shown.
- The title names the companion once: `title`, else "Diagram" or "Comparison". The arguments travel verbatim as
  JSON: the UI half renders them with the kit's `VisualizationCard`.
- A drawing belongs to the conversation, not to the tab. The live view is keyed by terminal, but every drawing
  is also written under the agent session id the terminal's agent record carries. When `OnTerminal` delivers a
  `TerminalAgentChanged` naming a session that has one, the store re-attaches it to the tab now reporting and
  publishes it as if just drawn, so resuming that agent session finds its diagram whatever terminal it resumed
  into, and a host restart does not lose it. Re-adopting is idempotent: a tab already holding that revision is
  left alone. A tab that drew before it said which session it is is bound to that session when the event
  arrives, so the order of drawing and identifying never decides whether a resume can find it. Removing a
  workspace (`WorkspaceRemoved`) forgets both indexes.
- The store lives as long as the host half's module and an activation only connects its publisher, session
  lookup and persistence, so turning the plugin off and on keeps the live views, as the fork's module state did.
  `BuiltinPlugins.All` builds a fresh module per read, so each host runtime in a process has its own store.
- The wire (`VisualizeContract`): method `report` (the render verdict), method `get` (the snapshot for the
  `changed` state channel, keyed `workspaceId`; its result is the whole per-workspace map, not one terminal's
  entry, since the channel's key is workspace-only). Every mutation publishes the whole workspace map on
  `changed`; the UI half keeps its own per-workspace map built from that.
- UI half: a companion of kind `visualization` whose availability predicate subscribes to `changed` for the
  workspace it is asked about and answers whether that terminal has an entry; a new or updated revision calls
  `FocusCompanion`, so the drawing surfaces even if the pane was closed, and `Invalidate`, so the companion's
  title follows the drawing. The pane renders the card interactively (the diagram fills the pane and pans and
  zooms in place) and reports the verdict back through `report` once per shown revision. A publish for another
  terminal of the same workspace leaves an open pane, and its zoom, alone.
- Public surface: `VisualizeContract` (manifest, contract, methods, channel) and its records
  (`TerminalVisualization`, `VisualizationsChanged`, `VisualizationsQuery`, `RenderReport`, `Ack`);
  `VisualizeHost` and `VisualizeUI`, the two modules.
- Allowed deps: the plugin API's root (contract), host (host half) and UI (UI half) entries, the UI kit
  (`Visualization`) and Avalonia in the UI half.
- Forbidden: any other plugin, any `SharpRail.Host.*` or `SharpRail.UI` project.

## Not ported

- ThinkRail's chat renders the same visualize call inline through the kit card for pi's own visualize tool;
  SharpRail has no chat, so the card's chat states (running, error result) are not part of the kit's card.
- The fork names the product in the tool's description and answer; SharpRail names itself.

## Framework adaptations

- The state channel and render report cross the existing plugin gRPC adapter remotely and remain typed
  calls in embedded mode. Register the pending render verdict before publication: an embedded client
  may report synchronously where the fork sends a WebSocket message.
- The kit renders Mermaid off the UI thread through the pinned Merman native library instead of browser
  Mermaid. Pan and zoom use the same gesture math as PDF Preview (25–600%, toolbar factor 1.15).
  Comparison, diagram, error, navigation and companion frames are compiled Avalonia XAML.
- Theme rerenders update the existing diagram navigation control, retaining its zoom and scroll.
  No chat or Pi runtime dependency is introduced.

## Remaining verification

Terminal ownership acceptance uses the HTTP MCP URL emitted by a real host PTY, then calls tools/list
and tools/call through that token. Unknown tokens are refused. Updates reopen the owning terminal's
companion while another window is active and leave unrelated terminals without a drawing.

The focused checks cover shape/schema fields, revisions, whole-workspace snapshots and streams, timeout,
cancellation, immediate embedded reports, refusal/rollback and revision reuse, session binding/adoption,
workspace removal and host restart. Headless local and gRPC windows cover diagram/comparison companion
mounting, title, toolbar/wheel zoom bounds, theme navigation retention, close/reopen and rollback.
The gRPC fixture's terminals now run on that remote host's PTYs: their emitted HTTP MCP URL lists
and calls visualize, refuses unknown tokens and returns renderer refusals. Both local and gRPC
cases reopen the owning terminal's closed drawing while another window stays active; the unrelated
window does not receive the drawing.

The local and gRPC companion checks also cover every comparison option's description, pros, cons and
single recommended badge; one versus two columns across the 640 boundary as the companion width changes;
drag panning of a zoomed diagram; a second terminal of the same workspace receiving only its own drawing;
and disabling then re-enabling the plugin, which unmounts every chip and pane and resubscribes from scratch.
The UI activation owns its per-workspace subscriptions and releases them in its disposer, since a companion
availability predicate has no unmount of its own.

Remaining before marking the port complete: deterministic remount render cancellation (the view cancels its
render on detach, but no check holds a render in flight), a terminal shown in two windows at once, native
appearance, published output and the full suite. The companion opens at a fixed 360 px rather than the fork's
45% share of the host.
