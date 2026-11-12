using Avalonia;
using Avalonia.Controls;

namespace Tereditor.UI;

/// <summary>What a child of a <see cref="MarkedRow"/> is.</summary>
internal enum RowRole
{
    /// <summary>The icon column at the row's start.</summary>
    Leading,
    /// <summary>The name: it shrinks first, so the marks stay in view after its ellipsis.</summary>
    Title,
    /// <summary>A second line under the title, as wide as the row.</summary>
    Below,
    /// <summary>A status indicator: it sits right after the title, never at the row's far edge.</summary>
    Mark
}

/// <summary>
/// The shared layout of a rail row: icon, title, an optional second line, and indicators pressed against the title's end.
/// Workspace rows, tab rows and nested rows all use it, so an indicator behaves the same in each.
/// </summary>
internal sealed class MarkedRow : Panel
{
    /// <summary>Width of the icon column.</summary>
    internal const double LeadingWidth = 14;
    /// <summary>Space after the icon column, and between the title and each indicator.</summary>
    internal const double Gap = 4;

    internal static readonly AttachedProperty<RowRole> RoleProperty = AvaloniaProperty.RegisterAttached<MarkedRow, Control, RowRole>("Role", RowRole.Title);

    internal static void SetRole(Control control, RowRole role) => control.SetValue(RoleProperty, role);

    /// <summary>Adds a child in its role.</summary>
    internal void Add(Control child, RowRole role)
    {
        SetRole(child, role);
        Children.Add(child);
    }

    private IEnumerable<Control> Of(RowRole role) => Children.Where(child => child.GetValue(RoleProperty) == role);

    private double Indent => Of(RowRole.Leading).Any() ? LeadingWidth + Gap : 0;

    private double MarksWidth() => Of(RowRole.Mark).Where(mark => mark.IsVisible).Sum(mark => mark.DesiredSize.Width + Gap);

    protected override Size MeasureOverride(Size available)
    {
        var height = 0.0;
        foreach (var leading in Of(RowRole.Leading)) { leading.Measure(new Size(LeadingWidth, double.PositiveInfinity)); height = Math.Max(height, leading.DesiredSize.Height); }
        var markHeight = 0.0;
        foreach (var mark in Of(RowRole.Mark)) { mark.Measure(Size.Infinity); if (mark.IsVisible) markHeight = Math.Max(markHeight, mark.DesiredSize.Height); }
        var inner = Math.Max(0, available.Width - Indent);
        var titleWidth = 0.0; var block = 0.0;
        foreach (var title in Of(RowRole.Title)) { title.Measure(new Size(Math.Max(0, inner - MarksWidth()), double.PositiveInfinity)); titleWidth = title.DesiredSize.Width; block += title.DesiredSize.Height; }
        var belowWidth = 0.0;
        foreach (var below in Of(RowRole.Below)) { below.Measure(new Size(inner, double.PositiveInfinity)); belowWidth = below.DesiredSize.Width; block += below.DesiredSize.Height; }
        var width = Indent + Math.Max(titleWidth + MarksWidth(), belowWidth);
        return new Size(double.IsInfinity(available.Width) ? width : Math.Min(width, available.Width), Math.Max(height, Math.Max(block, markHeight)));
    }

    /// <summary>The height of a title's first line including its top padding: indicators centre on it, however many lines follow.</summary>
    private static double FirstLineHeight(Control title)
    {
        if (title is not TextBlock text) return title.DesiredSize.Height;
        var line = text.LineHeight > 0 ? text.LineHeight : text.TextLayout.TextLines.FirstOrDefault()?.Height ?? text.DesiredSize.Height;
        return text.Padding.Top + line;
    }

    protected override Size ArrangeOverride(Size final)
    {
        foreach (var leading in Of(RowRole.Leading)) leading.Arrange(new Rect(0, 0, LeadingWidth, final.Height));
        var inner = Math.Max(0, final.Width - Indent);
        var titles = Of(RowRole.Title).ToArray();
        var belows = Of(RowRole.Below).ToArray();
        var block = titles.Concat(belows).Sum(child => child.DesiredSize.Height);
        var y = (final.Height - block) / 2;
        var titleWidth = 0.0;
        var markCentre = titles.Length > 0 ? y + FirstLineHeight(titles[0]) / 2 : final.Height / 2;
        foreach (var title in titles)
        {
            titleWidth = Math.Min(title.DesiredSize.Width, Math.Max(0, inner - MarksWidth()));
            title.Arrange(new Rect(Indent, y, titleWidth, title.DesiredSize.Height));
            y += title.DesiredSize.Height;
        }
        foreach (var below in belows)
        {
            below.Arrange(new Rect(Indent, y, inner, below.DesiredSize.Height));
            y += below.DesiredSize.Height;
        }
        var x = Indent + titleWidth;
        foreach (var mark in Of(RowRole.Mark))
        {
            if (!mark.IsVisible) { mark.Arrange(default); continue; }
            x += Gap;
            mark.Arrange(new Rect(x, markCentre - mark.DesiredSize.Height / 2, mark.DesiredSize.Width, mark.DesiredSize.Height));
            x += mark.DesiredSize.Width;
        }
        return final;
    }
}