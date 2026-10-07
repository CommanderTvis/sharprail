# Colour system

Upstream: apps/web/src/styles/COLOR.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))

Colour arrives in two layers, and a control may only ever name the second one.

```
Assets/Themes/*.theme.json   the PALETTE: one file per theme, the only place a hex lives
Rendering/Themes.cs          loads and validates the palette (see Themes.SPEC.md)
Rendering/Ui.cs              the SEMANTIC layer: one static brush per role, written by Ui.Apply
App.axaml                    application styles that bind those brushes with {x:Static rendering:Ui.*}
```

A palette entry answers which colour (`hint`, `elevated`, `borderStrong`). A brush answers what for
(`Ui.Hint`, `Ui.Elevated`, `Ui.BorderBrush`). Controls and styles name brushes; the manifest dictionary
(`Ui.Theme[...]`) is read only by `Ui.Apply` and by the non-brush consumers below.

```csharp
new Border { Background = Ui.Elevated, BorderBrush = Ui.BorderBrush }   // yes
new Border { Background = new SolidColorBrush(Color.Parse("#27272a")) } // no
```

## Why the split

A theme changes which colour a role resolves to without touching a control, and a role can be re-pointed
once in `Ui.Apply` instead of at every call site. The brushes are long-lived `SolidColorBrush` instances whose
`Color` is rewritten in place, so every control and every `{x:Static}` style setter repaints on a swap
without rebuilding the tree.

## The roles

| Family | Brushes | Notes |
| --- | --- | --- |
| Text | `TextBrush` · `Muted` · `Hint` | `Hint` (the `hint` key) is the quiet metadata tier: branch lines, spec roles, counts, empty states |
| Container | `Sidebar` · `Surface` · `Header` · `Elevated` | `Surface` is the `content` canvas behind documents and diffs; `Elevated` is every raised surface: menus, dialogs, inactive tab chrome, resting buttons |
| Control | `Hover` · `PrimaryFill` · `PrimaryFillHover` · `OnPrimary` | `Hover` is both pointer hover and the persistent selected/active fill (active project row, active change row, selected tab, active toggle segment); the primary trio is the solid primary button, its hover step and its label |
| Border | `BorderBrush` | from `borderStrong` |
| Primary | `Accent` · `PrimarySubtle` · `PrimaryMuted` | accent @ 10% and 40%: the selected Settings choice's fill and border |
| Feedback | `Info` · `Success` · `Warning` · `Danger` + `*Wash` | a wash is the solid colour @ 12%, the feedback-surface fill |
| Selection | `TextSelection` | Inputs and the Markdown preview share `selection`. Upstream mixes the preview at 40% because browsers wash whole line boxes; Avalonia highlights only glyph runs, and the mix left dark selections nearly invisible. The nullable selected-text foreground is the `ThemeSelectionForeground` dynamic resource |
| Effects | `DialogShadow` · `FadeFromElevated` · `FadeToElevated` | shadow alpha differs per appearance; the fades are the tab strip's overflow edges |

## Transparency: one form only

A tint is a brush on the alpha scale, computed by `Ui.Alpha` as a percentage of the colour's own alpha:

```
subtle 10%   ·   wash 12%   ·   muted 40%
```

`wash` sits one step above `subtle` so primary fills stay at 10% while feedback backgrounds read at 12%.
Control-level `Opacity` is not a colour tool: it dims nested content and bypasses role ownership. It is used
only to hide and reveal affordances (the workspace kebab, the change-row menu button). A new tint is a new
brush on the scale, never a one-off alpha at the call site.

## Non-brush consumers

Surfaces that bake colours cannot bind a brush. They read the same roles (or the specific manifest keys
only they need) and rebuild on `Ui.ThemeChanged`:

| Consumer | We set | The rest comes from |
| --- | --- | --- |
| Scintilla editor | text, `Surface` background, muted gutter, `editorSelection` composited over the surface (`Ui.Over`), the nullable editor selection foreground | the control's own defaults |
| Ghostty terminal | `Surface` background, text foreground, accent cursor, composited editor selection and its foreground, all 16 ANSI colours | Ghostty's configuration shim |
| Mermaid | the base theme variables derived in `MermaidRenderer` | Merman's base theme |
| Fluent | the palette accent for the current variant | Fluent's own palette |

This is a bounded, accepted gap. If a theme looks wrong in one of these, add that specific value rather than
adopting the library's whole surface.

## Adding or changing a colour

1. A theme should look different: edit its `Assets/Themes/<theme>.theme.json`.
2. A role should point somewhere else: change its source key in `Ui.Apply`.
3. A new role: add a static brush to `Ui` and write it in `Ui.Apply`.
4. A new tint: an alpha step from the scale; a new step is a design decision made once.
5. A role two themes must differ on needs its own manifest key: add it to `Themes.ColorKeys` and every
   manifest, then point a brush at it. Loading fails while a manifest lacks a listed key.

Never a raw hex or `Color.Parse` in a control, and never a second name for a value that already has one.

## Not yet ported

- A generated single source (`colors.json`) and a usage guard failing on raw colours, unknown roles, unused
  roles or stale generated output. The guard must cover shared primitives as well as app controls, so moving
  a control across module boundaries never exempts it from the colour contract.
- The contrast and distinguishability floors, listed in [Themes.SPEC.md](Themes.SPEC.md).
- Separate disabled roles (`control-disabled-*`: the enabled colour at a 60% step) and the stronger
  `control-border-active` for pressed, open and focused controls; Fluent's defaults paint those states.
- The editor chrome roles derived from existing palette keys: secondary matches of the selection
  (`editorSelection` @ 20%), the active find match (`warning` @ 40%) and widget shadows (`text` @ 20%), with
  the wider set upstream now paints from roles (current line, indent guides, bracket match, inactive line
  numbers, fold and whitespace marks). The Scintilla editor keeps its own defaults for all of these.
- The transparency checkerboard behind image previews (`text` @ 20% tiles, so a dark logo on a transparent
  image never reads as no image); image tabs and Markdown images sit directly on the surface. Both this and
  the editor roles need a 20% step, which the alpha scale lacks.
- Distinct `workspace` and `terminal` container roles and a separate `control-bg-selected`; SharpRail
  reuses `Surface`, `Sidebar` and `Hover`.
