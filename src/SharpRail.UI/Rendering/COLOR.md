# Colour system

Upstream: apps/web/src/styles/COLOR.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))

Colour arrives in two layers, and a control may only ever name the second one.

```
Assets/Themes/*.theme.json          the PALETTE: one file per theme, the only place a theme's hex lives
Rendering/Themes.cs                 loads and validates the palette (see Themes.SPEC.md)
Rendering/Design/colors.json        the SEMANTIC layer, authored: each role's palette key and alpha step
Rendering/Generated/Ui.Colors.g.cs  generated from it: one static brush per role and the code that writes them
Rendering/Ui.cs                     Ui.Apply, the alpha and compositing helpers, the gradient fades
App.axaml                           application styles that bind those brushes with {x:Static rendering:Ui.*}
```

`colors.json` is the single source. A role there is keyed by its brush name and records the upstream role it
translates (`role`), the palette key it reads (`from`) and, for a tint, its step on the alpha scale. A role
whose consumer bakes colours instead of binding a brush is `"kind": "color"` and becomes a `Color` property
(nullable when its palette key is). `effects` holds the per-appearance values that no palette supplies
(the modal scrim and the two shadow strengths). The checks executable writes the generated file with
`-- --design --write`; the file is committed, and `-- --design` fails while it differs from its source.

A palette entry answers which colour (`hint`, `elevated`, `borderStrong`). A brush answers what for
(`Ui.Hint`, `Ui.Elevated`, `Ui.BorderBrush`). Controls and styles name roles; the manifest dictionary
is read only by the generated `Ui.ApplyRoles`.

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
| Primary | `Accent` · `PrimarySubtle` · `PrimarySoft` · `PrimaryMuted` | accent @ 10%, 20% and 40%: the selected Settings choice's fill and border; dock drop hints rest at subtle fill with a soft border and activate to a soft fill with an accent border |
| Feedback | `Info` · `Success` · `Warning` · `Danger` + `*Wash` | a wash is the solid colour @ 12%, the feedback-surface fill |
| Selection | `TextSelection` | Inputs and the Markdown preview share `selection`. Upstream mixes the preview at 40% because browsers wash whole line boxes; Avalonia highlights only glyph runs, and the mix left dark selections nearly invisible. The nullable selected-text foreground is the `ThemeSelectionForeground` dynamic resource |
| Selection colours | `SelectionText` · `EditorSelection` · `EditorSelectionText` | colour roles for the baking consumers below; the two foregrounds are null when the theme keeps the native one |
| Effects | `Overlay` · `PopoverShadow` · `DialogShadow` · `FadeFromElevated` · `FadeToElevated` | the modal scrim; shadow alpha differs per appearance (upstream `shadow-md` and `shadow-lg`); the fades are the tab strip's overflow edges, derived from `Elevated` in `Ui.Apply` |

## Transparency: one form only

A tint is a role on the alpha scale (`scale` in `colors.json`), computed by `Ui.Alpha` as a percentage of
the colour's own alpha:

```
subtle 10%   ·   wash 12%   ·   soft 20%   ·   muted 40%
```

`wash` sits one step above `subtle` so primary fills stay at 10% while feedback backgrounds read at 12%.
Control-level `Opacity` is not a colour tool: it dims nested content and bypasses role ownership. It is used
only to hide and reveal affordances (the workspace kebab, the change-row menu button), so the guard accepts
only 0 and 1. A new tint is a new role on the scale, never a one-off alpha at the call site.

## Non-brush consumers

Surfaces that bake colours cannot bind a brush. They read the same roles, as colours, and rebuild on
`Ui.ThemeChanged`. Scintilla and Ghostty.Avalonia are independent libraries that take their colours as
input and reference no SharpRail project, so the app's adapters to them are where the contract applies:

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
2. A role should point somewhere else: change its `from` in `colors.json`.
3. A new role: add it to `colors.json`.
4. A new tint: an alpha step from the scale; a new step is a design decision made once.
5. A role two themes must differ on needs its own manifest key: add it to `Themes.ColorKeys`, the schema and
   every manifest, then point a role at it. Loading fails while a manifest lacks a listed key.

After 2 to 5, run the checks with `-- --design --write` and rebuild.

Never a raw hex or `Color.Parse` in a control, and never a second name for a value that already has one.

## The guard

`-- --design` (also part of the full run) reads the UI project's XAML and C# sources, wherever in the project
a control lives, and fails on:

- generated output that differs from what `colors.json` produces;
- a role naming an unknown palette key or alpha step, and a palette key that no role claims unless
  `colors.json` lists it under `unclaimed` with the reason;
- a role no control uses;
- a raw colour: a hex literal, `Color.Parse`/`FromArgb`/`FromRgb`, a named `Colors.*` or `Brushes.*` other
  than `Transparent`, a brush constructed at the call site, or a literal colour in a XAML brush attribute or
  setter;
- a palette key read outside `Ui`;
- `Opacity` other than 0 or 1.

`Ui.cs` and `Themes.cs` are the only exempt files: one computes tints, the other decodes the palette.

## Not yet ported

- Separate disabled roles (`control-disabled-*`: the enabled colour at a 60% step) and the stronger
  `control-border-active` for pressed, open and focused controls; Fluent's defaults paint those states.
- The editor chrome roles derived from existing palette keys: secondary matches of the selection
  (`editorSelection` @ 20%), the active find match (`warning` @ 40%) and widget shadows (`text` @ 20%), with
  the wider set upstream now paints from roles (current line, indent guides, bracket match, inactive line
  numbers, fold and whitespace marks). The Scintilla editor keeps its own defaults for all of these.
- The transparency checkerboard behind image previews (`text` @ 20% tiles, so a dark logo on a transparent
  image never reads as no image); image tabs and Markdown images sit directly on the surface.
- Distinct `workspace` and `terminal` container roles and a separate `control-bg-selected`; SharpRail
  reuses `Surface`, `Sidebar` and `Hover`.
