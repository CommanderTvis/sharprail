using Avalonia.Controls;
using Avalonia.Layout;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>
/// The one way a directory is printed in a pane: a muted 12 px folder icon, then the path in the code font, a long path losing its middle
/// (never its identifying end) and the full path as tooltip. The Claude Code and Code Intelligence panes share it.
/// </summary>
public sealed class PathLine : StackPanel
{
    private const int LabelMax = 40;
    private readonly TextBlock text = Ui.Text("…", Ui.Muted, 12);

    public PathLine()
    {
        Orientation = Orientation.Horizontal;
        Name = "PathLine";
        text.FontFamily = Ui.CodeFont;
        text.Name = "PathLineText";
        text.VerticalAlignment = VerticalAlignment.Center;
        var folder = Ui.Icon("folder", Ui.Muted, 12);
        folder.Margin = new Avalonia.Thickness(0, 0, 4, 0);
        folder.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(folder);
        Children.Add(text);
    }

    /// <summary>The path shown, or null while it is still being read.</summary>
    public string? Path
    {
        get => field;
        set { field = value; text.Text = Label(value) ?? "…"; ToolTip.SetTip(this, value ?? "Reading the directory…"); }
    }

    /// <summary>The text printed for a path: in full up to 40 characters, else first segment, an ellipsis and the last two segments.</summary>
    public static string? Label(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (path.Length <= LabelMax) return path;
        var segments = path.Split('/');
        return $"{segments[0]}/…/{string.Join('/', segments[^2..])}";
    }
}