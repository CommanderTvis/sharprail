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
visualize extension also uses (a diagram with raw Mermaid, or comparison cards; SharpRail adds a
`svg` type for hand-written SVG, which only widens the `type` enum), records the call per terminal,
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
  (`VisualizeParams.ShapeError`: a diagram needs non-blank Mermaid, an `svg` call a non-blank `svg`, a `markdown` call a non-blank `markdown`, a comparison a non-empty list of named
  options) and answers with the title, the revision, and the fact that calling again updates in place, the
  sentence that teaches the agent the iteration loop. The parameters record's `[Description]` attributes become
  the schema's field descriptions.
- `visualize_read` (no arguments, bound to the calling terminal the same way, refused outside one) answers JSON
  for that terminal's view only: `shown`; when shown, `revision`, effective `title`, the stored arguments field for
  field (`type`, `requestedTitle` when one was given, `mermaid`, `svg` or `markdown` as sent, or `options`), `render` (`ok`, `error` with
  `renderError`, or `pending` while no client has reported this revision), and `selection` (the text the user has
  selected in the pane for this revision, null when none); `rejected` (`revision`, `error`) when the latest call
  was refused and rolled back. It is a separate tool rather than an action on `visualize` because `visualize`'s
  schema, shared with the fork, requires `type`. Verdicts, refusals and selections are in-memory beside the store,
  forgotten with the workspace; an adopted drawing reads `pending` until a pane reports it.
- The renderer decides, and the agent hears it. Mermaid is parsed where it is drawn, so whether a diagram is
  valid is not something the host can answer. The tool run therefore waits (5 s) for a client to report, through
  the `report` method, what it made of that revision, and answers with the parse error as an error result when
  it failed. A refused drawing is rolled back: the last one that rendered is restored and published again, so an
  iteration's typo does not cost the user the picture they had, and the next attempt reuses the revision number
  the pane never showed. No client watching is not a failure: the wait times out and the drawing stands. A
  comparison has no Mermaid to fail on and settles the moment it is shown. Hand-written SVG gets the same verdict:
  the kit's `SvgSanitizer` refuses it with a reason, which is the render error.
- Hand-written SVG (`type: svg`, or an option's `svg` beside `mermaid`, which wins) is sanitized before it is
  parsed and draws through the same Svg.Skia path and pan/zoom view as Mermaid output. Refused: over 2 MB (UTF-8)
  or 10000 elements, malformed XML, DTDs and entities, processing instructions, a root other than `<svg>`,
  elements outside the SVG namespace, `script`, `foreignObject`, `iframe`/`object`/`embed`/`audio`/`video`,
  animation (`set`, `animate*`, `discard`), `handler`/`listener`, any `on*` attribute, `href`/`xlink:href`
  other than `#fragment` (or, on `<image>`, a `data:` PNG/JPEG/GIF/WebP/BMP), CSS `url()` other than `#id`,
  `@import`, `expression(` and `javascript:`. The XML reader has no resolver, so nothing is fetched from the
  network or filesystem. Theme: the root gets the pane foreground as `color` unless it sets one, and
  `currentColor`, `var(--fg)`, `var(--bg)`, `var(--accent)`, `var(--muted)` and `var(--border)` in attributes and
  `<style>` become the pane's text, surface, accent, muted and border colours (an unknown variable takes its
  fallback), re-applied on theme change. A missing `xmlns` is supplied.
- Markdown (`type: markdown`) draws `markdown` with the kit's `MarkdownPreview`, the same renderer as the Markdown
  preview (tables, HTML tables, code, fenced Mermaid, theme colours), with no workspace: images and links resolve to
  nothing. It cannot fail to render, so it settles when shown. It is a top-level type only, not a comparison field.
- The title names the companion once: `title`, else "Diagram", "SVG", "Markdown" or "Comparison". The arguments travel verbatim as
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
  entry, since the channel's key is workspace-only), method `select` (the pane's selected text for a shown revision;
  one for another revision is dropped). Every mutation publishes the whole workspace map on
  `changed`; the UI half keeps its own per-workspace map built from that.
- UI half: a companion of kind `visualization` whose availability predicate subscribes to `changed` for the
  workspace it is asked about and answers whether that terminal has an entry; a new or updated revision calls
  `FocusCompanion`, so the drawing surfaces even if the pane was closed, and `Invalidate`, so the companion's
  title follows the drawing. The pane renders the card interactively (the diagram fills the pane and pans and
  zooms in place) and reports the verdict back through `report` once per shown revision, and the selected text (the kit's
  `VisualizationCard.SelectedText`: diagram labels or card text) through `select` after each pointer release or key
  when it changed. A publish for another
  terminal of the same workspace leaves an open pane, and its zoom, alone.
- The plugin contract carries terminal visualizations, workspace updates and render reports
  between its host and UI halves.
- Allowed deps: the plugin API's root (contract), host (host half) and UI (UI half) entries, the UI kit
  (`Visualization`) and Avalonia in the UI half.
- Forbidden: any other plugin, any `SharpRail.Host.*` or `SharpRail.UI` project.

The host half contributes the `visualize` item to the agent instructions (H20, order 10) on activation, so a disabled plugin leaves it out.

## Not ported

- ThinkRail's chat renders the same visualize call inline through the kit card for pi's own visualize tool;
  SharpRail has no chat, so the card's chat states (running, error result) are not part of the kit's card.
- The fork names the product in the tool's description and answer; SharpRail names itself.

## Framework adaptations

- The state channel and render report cross the existing plugin WebSocket RPC adapter remotely and remain typed
  calls in embedded mode. Register the pending render verdict before publication: an embedded client
  may report synchronously where the fork sends a WebSocket message.
- The kit renders Mermaid off the UI thread through the pinned Merman native library (macOS, Android; the
  browser client uses its WASM build) instead of browser Mermaid. Pan and zoom use the same gesture math as PDF Preview (25–600%, toolbar factor 1.15).
  Comparison, diagram, error, navigation and companion frames are compiled Avalonia XAML.
- Diagram text and comparison cards are selectable and copyable in the pane (see the UI Kit spec).
- Theme rerenders update the existing diagram navigation control, retaining its zoom and scroll.
  No chat or Pi runtime dependency is introduced.

## Remaining verification

Terminal ownership acceptance uses the HTTP MCP URL emitted by a real host PTY, then calls tools/list
and tools/call through that token. Unknown tokens are refused. Updates reopen the owning terminal's
companion while another window is active and leave unrelated terminals without a drawing.

The focused checks cover `visualize_read` (nothing shown, diagram, comparison, failed parse, selection, another
terminal's and no terminal's call, over the runtime's MCP table), shape/schema fields, revisions, whole-workspace snapshots and streams, timeout,
cancellation, immediate embedded reports, refusal/rollback and revision reuse, session binding/adoption,
workspace removal and host restart. Headless local and WebSocket RPC windows cover diagram/comparison companion
mounting, title, toolbar/wheel zoom bounds, theme navigation retention, close/reopen and rollback.
The WebSocket RPC fixture's terminals now run on that remote host's PTYs: their emitted HTTP MCP URL lists
and calls visualize, refuses unknown tokens and returns renderer refusals. Both local and WebSocket RPC
cases reopen the owning terminal's closed drawing while another window stays active; the unrelated
window does not receive the drawing.

The local and WebSocket RPC companion checks also cover every comparison option's description, pros, cons and
single recommended badge; one versus two columns across the 640 boundary as the companion width changes;
drag panning of a zoomed diagram; splitter resizing that expands the diagram viewport, with width restored
after close/reopen; a second terminal of the same workspace receiving only its own drawing;
and disabling then re-enabling the plugin, which unmounts every chip and pane and resubscribes from scratch.
The UI activation owns its per-workspace subscriptions and releases them in its disposer, since a companion
availability predicate has no unmount of its own.

Remaining before marking the port complete: deterministic remount render cancellation (the view cancels its
render on detach, but no check holds a render in flight), a terminal shown in two windows at once, native
appearance, published output and the full suite.
