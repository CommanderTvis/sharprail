# Spacing system

Upstream: apps/web/src/styles/SPACING.md @ 4a65ed7f

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
workbench are 4 (row and icon gaps), 8 (control groups, settings stacks), 12 (panel insets such as the
Projects, Files and Specs trees and the Changes toolbar) and 16/24 (settings pages and dialogs).

## Sizing is not rhythm

Box sizes share the same unit but are layout constraints, not spacing steps: the 28px tree and rail rows,
the 32px panel header row, 14px row icons, 16px chevrons, 20–32px icon buttons. They are free-form pixel
values chosen for the box, and are not held to the scale. Corner radii (4 on rows and buttons, 6 on menus,
8 on the Settings frame) and border thicknesses are geometry too.

Spacing stays independent of typography, colour and radius: a type-size or theme change never moves layout.
The interface font size preference scales text only.

## Where the values live

Static layouts put spacing in compiled `.axaml` (`Spacing="8"`, `Margin="12"`); dynamic panels built in C#
write the same numbers inline. Both use the scale directly rather than named resources, so the number at the
call site is the step.

## Not yet ported

- A single authored source for the scale (upstream `spacing.json` and its generated tokens) and a guard
  rejecting off-scale rhythm values. The C# panels still use some off-scale rhythm, notably 6 (icon/label
  gaps in `Ui.Row` and a few stacks), 10 (button padding, some settings stacks) and 20 (dialog margins), which
  a guard would move onto existing steps.
- The documented escape hatches (measured geometry such as a close-button reserve or an icon-aligned indent)
  as a closed, recorded list.
