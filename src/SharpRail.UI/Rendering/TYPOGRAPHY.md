# Typography system

Upstream: apps/web/src/styles/TYPOGRAPHY.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))

`Rendering/Design/typography.json` is the single authored source of typography. Families, weights, sizes,
line heights, letter spacings and the semantic text styles all live there; the C# the kit ships
(`src/SharpRail.Plugins.UI.Kit/Generated/Typography.g.cs`) is generated from it by
`SharpRail.Checks -- --design --write`, and `-- --design` fails while it is stale. Change the type of
anything by editing the JSON, never the generated file.

## Primitives

| Group | Ids |
|---|---|
| `fontFamilies` | `interface` (Geist, `Ui.InterfaceFont`) · `code` (JetBrains Mono, `Ui.CodeFont`) |
| `fontWeights` | `light` 370 · `regular` 400 · `medium` 500 · `semibold` 600 |
| `fontSizes` | `s10` `s11` `s12` `s13` `s14` `s16` `s18` `s20` `s24` `s44` |
| `lineHeights` | `compact` 1.25 · `metadata` 1.25 · `ui` 1.3 · `code` 1.4 · `relaxed` 1.4 · `default` 1.3 · `reading` 1.45 |
| `letterSpacings` | `normal` 0 · `loose` 0.02em · `wide` 0.05em · `widest` 0.1em |

The leading scale is deliberately dense (1.25–1.4): at 14px Geist's x-height leaves an empty band between
lines taller than the letters at the former 1.5–1.6. Reading prose (`body.reading`) is the exception at 1.45.
A generated role stores its line height and tracking in pixels (size times the ratio), because Avalonia's
`LineHeight` and `LetterSpacing` are absolute.

Upstream's `brand` family (Orbitron) is not bundled; `brand.hero` uses the interface face.

## Semantic styles

A canonical style names one primitive per property: family, size, weight, line height, letter spacing and
transform. An alias carries only `$ref` to a canonical style — never to another alias. Two canonical styles
may not hold identical references; that is a missing `$ref`. The groups are the upstream ones: **brand**
(`hero`), **heading** (`xl`, `lg`, `md`, `sm`), **title** (`dialog`, `card`→dialog, `section`, `compact`,
`entity`→body.reading), **ui** (`default`, `metadata`, `eyebrow`, `labelPill`→eyebrow, `caption`,
`action`→title.compact, `emphasis`→title.compact), **body** (`reading`) and **code** (`text`, `document`,
`otp`, `textSmall`). Markdown prose systems are not yet expressed as roles.

Avalonia has no text transform: an `uppercase` role records that its text is authored in capitals.

## How controls consume it

Each style generates a `TextRole` field on `Typography` (`Typography.TitleSection`) and a style class named
as upstream (`tr-title-section`, `tr-text-ui`, `tr-text-metadata`, `tr-code-text-small`, …).
`Typography.Styles()` holds one style per class for `TextBlock` (family, size, weight, line height,
tracking) and for templated controls (family, size, weight); the app adds it once to its styles, so XAML
writes `Classes="tr-text-metadata"`. C# calls `Typography.Apply(control, role)`, which adds the class and
writes the values locally, because `Ui.Text` sets a local size and weight that would outrank a class style.

## The gate

`-- --design` validates the source (references resolve, aliases are one hop and carry nothing else, no
duplicate canonical styles) and scans the UI project's and the kit's XAML and C#:

- a `tr-…` class must name a role;
- a literal `FontSize` must be a size primitive, and a literal numeric `FontWeight` a weight primitive;
- a literal `LineHeight` is rejected unless `typography.json` lists it under `exceptions` with its file,
  value and reason. The list is closed: an exception that matches nothing fails.

A size passed as an argument (`Ui.Text(text, brush, 12)`) is not read by the gate; such call sites are
still on the size scale by convention and move to roles as they are touched.
