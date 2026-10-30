using Avalonia.Controls;
using Avalonia.Media;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>What a menu action means. A semantic has exactly one icon (<see cref="ActionIcons"/>); see ActionIcons/SPEC.md.</summary>
public enum ActionIcon
{
    Delete, NewFile, NewFolder, CopyPath, Add, Close, OpenFolder, Rename, Home, Branch, Check, Continue, Resume, EnterPath, Clone, QuickFix, CopyAbsolutePath
}

/// <summary>The single registry from a menu action's meaning to its icon asset, so no menu picks its own variant.</summary>
public static class ActionIcons
{
    private static readonly (ActionIcon Icon, string Asset)[] Table =
    [
        (ActionIcon.Delete, "trash"),
        (ActionIcon.NewFile, "fileAdd"),
        (ActionIcon.NewFolder, "folderAdd"),
        (ActionIcon.CopyPath, "route"),
        (ActionIcon.Add, "add"),
        (ActionIcon.Close, "close"),
        (ActionIcon.OpenFolder, "folderOpen"),
        (ActionIcon.Rename, "pencil"),
        (ActionIcon.Home, "homeFill"),
        (ActionIcon.Branch, "gitBranch"),
        (ActionIcon.Check, "check"),
        (ActionIcon.Continue, "play"),
        (ActionIcon.Resume, "history"),
        (ActionIcon.EnterPath, "inputCursor"),
        (ActionIcon.Clone, "gitFork"),
        (ActionIcon.QuickFix, "alertTip"),
        (ActionIcon.CopyAbsolutePath, "mapPin")
    ];

    /// <summary>Every semantic with its asset name, for checks and documentation.</summary>
    public static IReadOnlyList<(ActionIcon Icon, string Asset)> All => Table;

    public static string Asset(ActionIcon icon) => Table.First(entry => entry.Icon == icon).Asset;

    /// <summary>The icon a menu item shows for <paramref name="icon"/>, at the menu icon size.</summary>
    public static Control Create(ActionIcon icon, IBrush? color = null, double size = 14) => Ui.Icon(Asset(icon), color, size);
}