---
id: submodule-ui-themes
type: submodule-design
status: active
title: Themes
---

# Themes: bundled manifest catalogue and application

Upstream: apps/web/src/themes/SPEC.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))

## Responsibility

`Themes.cs` loads and validates the bundled declarative theme manifests at startup, owns the resulting
fixed catalogue, and resolves the active manifest for fixed or system mode. `Ui.Apply` then writes that
manifest onto the shared brushes. The host owns only the opaque theme id, the fixed/system mode and the
optional light/dark pair (`IHostStateService` settings `theme`, `theme-mode`, `system-light`,
`system-dark`); this module owns what those values mean on this app, including operating-system appearance
and same-appearance fallback. Adding a theme means adding one `Assets/Themes/*.theme.json` file and
rebuilding: no code, contract, style or check changes.

## Boundary

- Owns the manifest contract (`ThemeManifest`), the bundled set under `Assets/Themes`, catalogue
  construction, fixed/system resolution (`ThemeResolution`), pair derivation (`Themes.DerivePair`) and
  same-appearance fallback (`Themes.Fallback`).
- `Ui` owns application: `Ui.Apply` writes every brush, then the Fluent accent and the nullable
  selected-text foreground resource, then raises `Ui.ThemeChanged`. That event is the one way to observe a
  completed swap; Mermaid diagrams, the Scintilla editor and Ghostty terminals rebuild their baked colours
  from it rather than each watching the window's theme variant.
  The subscription lives in the shared `Ui` layer independently of catalogue and preference resolution,
  matching upstream's shared UI theme-observation seam; theme selection and application stay app-owned.
- Forbidden: host state writes, UI state, runtime theme registration or discovery, executable theme code,
  layout or arbitrary style supplied by a manifest.

## Manifest contract

A theme is exactly one `*.theme.json`. Schema version 2 is strict and self-contained: id, label and order,
light or dark appearance, normal or high contrast, a complete semantic UI palette, all 16 ANSI colours and a
syntax palette. Colours are canonical six- or eight-digit hex (`#rrggbbaa`; Avalonia's own parser reads eight
digits as `#aarrggbb`, so `Themes.Hex` decodes them itself). The two selected-text foregrounds
(`selectionForeground`, `editorSelectionForeground`) may be `null` to keep the consumer's native foreground.
There is no inheritance or partial overlay. Typography, spacing, radii, fonts and motion are product values,
not theme values.

A manifest supplies the palette, not the roles. It answers which colour; what each colour is for is the
brush layer in `Ui` (`Sidebar`, `Elevated`, `Hint`, `PrimarySubtle`), the only layer controls name. See
[COLOR.md](COLOR.md).

One key per role that themes may vary independently. `header` is separate from `content` even though
every bundled manifest ships them equal: a role can only vary between themes if the manifest has a key for
it. The accent is a triple for the same reason: `accent` (and `accentSolid`, the primary button fill),
`accentHover` as the primary button's hover fill, and `onAccent` as the label on both. Pinning the primary
button to a constant outside the palette would make it the one control a theme cannot restyle.

Bundled files are enumerated from the `Assets/Themes` avares folder rather than named in code, and validated
all-or-nothing at first use of `Themes.All`. They are our own files, so an invalid or duplicate manifest, a
missing default (`dark`), or a catalogue without at least one light and one dark theme fails loudly. The
catalogue orders by `order`, then label, then id.

## Runtime contract

The catalogue is fixed for the process lifetime. Fixed mode resolves `theme`; an unknown id falls back to
the default and reports `Fallback`. System mode reads the application's actual theme variant, which follows
the operating system (an unreported appearance reads as light), and resolves that appearance's slot.

A configured slot is usable only when its manifest exists and has the required appearance. Otherwise
resolution keeps the host value intact and picks the lowest-order normal-contrast manifest of that
appearance, then the lowest-order manifest of it. On first enable the resolved fixed theme fills its own slot
and the opposite slot picks the lowest-order theme with matching contrast, then the same fallback. The
Appearance settings page sends mode and the derived pair atomically; a persisted pair is reused later, and
the fallback is disclosed rather than written back.

`WorkbenchWindow.ApplyTheme` runs on startup, on every host settings broadcast and on
`ActualThemeVariantChanged`. In system mode the window requests the default variant so the OS decides; in
fixed mode it pins the manifest's variant. Application is atomic from consumers' perspective: all brushes
and resources are written before `ThemeChanged` fires. The listener is per window and never writes host
state, so one device's appearance never affects another client.

Coverage lives in `tests/SharpRail.Checks` (`E2E/ThemeE2E.cs`, `StateChecks.cs`): catalogue order and ids,
high-contrast metadata, pair derivation, and host round-tripping of mode and pair.

## Non-goals

Users cannot add themes except through a source change. Runtime registration, extension loading, hot
discovery, external theme formats, OS contrast following and schedule UI are out of scope; if runtime themes
ever arrive, the seam is a validated registration path in front of the same catalogue.

## Not yet ported

- A JSON schema file for manifests (`$schema` points at `../theme.schema.json`, which is not bundled) and a
  load-time check that validation and schema agree.
- The contrast gates: WCAG AA on resting surfaces, 3.0 on `hover` and for `hint`, `onAccent` AA on both
  accent fills, hover-versus-surface distinguishability of at least 1.15, and AAA resting / AA hover for
  `contrast: "high"` manifests.
- Consumption of the manifest's `syntax` palette; `Themes.Parse` ignores it and code surfaces pick their
  own colours. Upstream keeps one syntax scope map for both Markdown code blocks and the file editor: the
  editor resolves it to concrete colours after each swap, and a colour it cannot resolve is omitted so the
  editor's own default stays usable rather than painting a wrong one.
