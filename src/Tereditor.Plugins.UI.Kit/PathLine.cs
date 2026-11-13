using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tereditor.Plugins.UI.Kit;

/// <summary>
/// The one way a directory is printed in a pane: a muted 12 px folder icon, then the path in the code font, a path too wide for the pane
/// losing whole middle segments (never its identifying end) and the full path as tooltip. The Claude Code and Code Intelligence panes share it.
/// </summary>
public sealed class PathLine : DockPanel
{
    private const int LabelMax = 40;
    private readonly TextBlock text = Ui.Text("…", Ui.Muted, 12);

    public PathLine()
    {
        Name = "PathLine";
        text.FontFamily = Ui.CodeFont;
        text.Name = "PathLineText";
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextTrimming = TextTrimming.PathSegmentEllipsis;
        var folder = Ui.Icon("folder", Ui.Muted, 12);
        folder.Margin = new Avalonia.Thickness(0, 0, 4, 0);
        folder.VerticalAlignment = VerticalAlignment.Center;
        SetDock(folder, Dock.Left);
        Children.Add(folder);
        Children.Add(text);
    }

    /// <summary>The path shown, or null while it is still being read.</summary>
    public string? Path
    {
        get => field;
        set { field = value; text.Text = string.IsNullOrEmpty(value) ? "…" : value; ToolTip.SetTip(this, value ?? "Reading the directory…"); }
    }

    /// <summary>
    /// A path shortened for a one-line fact where no width is known: in full up to 40 characters, else first segment, an ellipsis and the
    /// last two segments. <see cref="PathLine"/> itself trims to its width instead.
    /// </summary>
    public static string? Label(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.Length <= LabelMax) return path;
        var segments = path.Split('/');
        return $"{segments[0]}/…/{string.Join('/', segments[^2..])}";
    }
}