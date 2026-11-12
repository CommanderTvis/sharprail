using Avalonia.Controls;
using Avalonia.Media;

using SharpRail.Plugins.Api.UI;
using SharpRail.UI.Plugins;
using SharpRail.UI.Rendering;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private (string PluginId, string Icon)? FileIconAnswer(string path, bool directory = false)
    {
        if (Plugins.FileIcon(path, directory ? FileIconKind.Directory : FileIconKind.File) is { } custom) return custom;
        if (!state.Current.Settings.FileTypeIconsEnabled) return null;
        var key = FileTypeIcons.Key(state.Current.Settings.FileTypeIconStyle, path, directory);
        return key.Length > 0 ? ("", "file-type:" + key) : null;
    }

    private Control ResolveFileIcon((string PluginId, string Icon) icon, IBrush? color = null, double size = 14)
    {
        if (icon.PluginId.Length > 0) return PluginIcons.Resolve(icon.Icon, Plugins.Entry(icon.PluginId), color, size, "file");
        return CoreFileIcon(icon.Icon["file-type:".Length..], color, size);
    }

    internal static Control CoreFileIcon(string name, IBrush? color = null, double size = 14)
    {
        var control = new AssetIcon("file-type:" + name, "file", color, size) { Name = "FileTypeIcon" };
        var (bytes, tint) = FileTypeIcons.Resolve(name);
        if (bytes is not null) control.Show(bytes, color ?? tint ?? Ui.TextBrush, size);
        return control;
    }
}