# Spacing system

Upstream: apps/web/src/styles/SPACING.md (revision: [UPSTREAM.md](../../../UPSTREAM.md))

Spacing is one numeric vocabulary. When a design says "spacing 8", the implementation writes `8` as a
`Spacing`, `Margin` or `Padding` value and it lays out as 8 device-independent pixels: one value, one name.
Avalonia lengths are already logical pixels, so the upstream `--spacing: 1px` base needs no translation; a
number is its own step.

## The scale

The canonical rhythm steps are `0 · 2 · 4 · 8 · 12 · 16 · 24 · 32 · 40 · 64`. The step name is its pixel
value. The scale is a defined primitive set, not an inventory of current use: a step may exist ahead of any
consumer, and adding a step (a `6`, say) is a design decision rather than a mechanical one.

Rhythm is the gap between things: `StackPanel.Spacing`, `Grid` gutter columns and rows (`ColumnDefinitions("14,4,*")`
reads as icon, 4px gap, label), and `Margin`/`Padding` on panels, rows and toolbars. The common values in the
workbench are 4 (row and icon gaps, label-over-field stacks), 8 (control groups, settings stacks, dialog
fields), 12 (panel insets such as the Projects, Files and Specs trees, the Changes toolbar and code frames)
and 16/24 (settings pages, dialogs and document placeholders). A text button pads 12 by 4.

## Sizing is not rhythm

Box sizes share the same unit but are layout constraints, not spacing steps: the 28px tree and rail rows,
the 32px panel header row, 14px row icons, 16px chevrons, 20–32px icon buttons. They are free-form pixel
values chosen for the box, and are not held to the scale. Corner radii (4 on rows and buttons, 6 on menus,
8 on the Settings frame) and border thicknesses are geometry too.

Spacing stays independent of typography, colour and radius: a type-size or theme change never moves layout.
The interface font size preference scales text only.

## Where the values live

`Rendering/Design/spacing.json` is the single authored source of the scale. Static layouts put spacing in
compiled `.axaml` (`Spacing="8"`, `Margin="12"`); dynamic panels built in C# write the same numbers inline.
Both use the scale directly rather than named resources, so the number at the call site is the step and
nothing is generated from the source: the guard is what ties call sites to it.

## The guard

`-- --design` reads every literal `Spacing`, `RowSpacing`, `ColumnSpacing`, `Margin` and `Padding` in the UI
project's XAML (attributes and style setters) and C# (assignments and object initialisers), wherever the
control lives, and fails on a component that is not a step. A negative step is the same step pulled the other
way. A value computed at run time is measured, not rhythm, and is not checked.

An icon button's box is sizing: `Ui.IconButton` and the fixed-size icon buttons centre their icon in the box
with no padding rather than deriving one from the two sizes.

## Escape hatches

Measured geometry that has to be written as a margin or padding is recorded in `spacing.json` under
`exceptions`, each with its file, literal value and reason. The list is closed: the guard accepts exactly
those values in those files and fails on an entry that no longer matches anything.

- `Docking/DockGroups.cs` `0, 0, 3, 0`: room for an italic preview title's glyph overhang inside the measured
  label box.
- `Docking/DockGroups.cs` `6, 0, 0, 0`: the close-button and modified-dot reserve in fixed-height tab chrome.

## Not yet ported

- Grid gutter columns and rows written inside `ColumnDefinitions`/`RowDefinitions` strings mix rhythm with
  sizing in one literal and are not read by the guard.
- A thickness passed as an argument rather than assigned is not read by the guard either: the Projects rail's
  rename box takes `20, 0, 32, 0` that way, aligned with the workspace row's icon indent and kebab reserve.
