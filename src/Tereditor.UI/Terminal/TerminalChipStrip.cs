using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.UI.Terminal;

/// <summary>
/// The chip strip under a terminal: the accessory rows (first child) and the gear (second child), which sits right after the last
/// visible chip, on that chip's line, as one more chip of the flow. When the line has no room left it wraps to a line of its own at
/// the row's start; with no chip showing it takes the first chip's place.
/// </summary>
internal sealed class TerminalChipStrip : Panel
{
    // The rows' own inset, where the first chip of a row sits.
    private const double RowInset = 8;

    // Whether the gear took a line of its own at the last arrange; only then does the strip measure a line for it.
    private bool ownLine;

    private Control? Rows => Children.Count > 0 ? Children[0] : null;
    private Control? Gear => Children.Count > 1 ? Children[1] : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Rows is not { } rows) return default;
        rows.Measure(availableSize);
        var size = rows.DesiredSize;
        if (Gear is not { } gear) return size;
        gear.Measure(Size.Infinity);
        // The gear sits in a slot: a hidden gear leaves it empty.
        if (gear.DesiredSize.Width <= 0) return size;
        var height = ownLine || size.Height < 0.5 ? size.Height + gear.DesiredSize.Height : size.Height;
        return new Size(Math.Max(size.Width, RowInset + gear.DesiredSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Rows is not { } rows) return finalSize;
        rows.Arrange(new Rect(0, 0, finalSize.Width, rows.DesiredSize.Height));
        if (Gear is not { DesiredSize.Width: > 0 } gear)
        {
            Gear?.Arrange(default);
            return finalSize;
        }
        var width = gear.DesiredSize.Width;
        var height = gear.DesiredSize.Height;
        Rect place;
        var wraps = true;
        if (LastChip(rows) is { } last && Offset(last.Parent) is { } line)
        {
            // The right end of the last chip's line: any untagged chip after it on that line counts too.
            var right = last.Parent.Children.Where(child => child.IsVisible && Overlaps(child, last.Chip))
                .Max(child => child.Bounds.Right + child.Margin.Right);
            var top = last.Chip.Bounds.Top - last.Chip.Margin.Top;
            if (line.X + right + width <= finalSize.Width - RowInset + 0.5)
            {
                place = new Rect(line.X + right, line.Y + top, width, height);
                wraps = false;
            }
            else place = new Rect(line.X, rows.DesiredSize.Height, width, height);
        }
        else place = new Rect(RowInset, rows.DesiredSize.Height, width, height);
        gear.Arrange(place);
        if (wraps != ownLine)
        {
            ownLine = wraps;
            InvalidateMeasure();
        }
        return finalSize;
    }

    // The last visible chip a row tagged: the lowest line, then the rightmost on it.
    private (Panel Parent, Control Chip)? LastChip(Control rows)
    {
        (Panel Parent, Control Chip)? last = null;
        var bottom = double.NegativeInfinity;
        var right = double.NegativeInfinity;
        foreach (var chip in rows.GetVisualDescendants().OfType<Control>())
        {
            if (chip.GetValue(TerminalChips.IdProperty) is null || !chip.IsEffectivelyVisible || chip.Bounds.Width <= 0 || chip.GetVisualParent() is not Panel parent) continue;
            if (Offset(parent) is not { } origin) continue;
            var chipBottom = origin.Y + chip.Bounds.Bottom;
            var chipRight = origin.X + chip.Bounds.Right;
            if (chipBottom > bottom + 0.5 || (Math.Abs(chipBottom - bottom) <= 0.5 && chipRight > right))
            {
                last = (parent, chip);
                bottom = chipBottom;
                right = chipRight;
            }
        }
        return last;
    }

    private static bool Overlaps(Control a, Control b) => a.Bounds.Top < b.Bounds.Bottom && b.Bounds.Top < a.Bounds.Bottom;

    // Where a descendant's own box starts in the strip's coordinates.
    private Point? Offset(Visual visual)
    {
        var at = new Point();
        for (var current = visual; current != this; current = current.GetVisualParent())
        {
            if (current is null) return null;
            at += current.Bounds.Position;
        }
        return at;
    }
}