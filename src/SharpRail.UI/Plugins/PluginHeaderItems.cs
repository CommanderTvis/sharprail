using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;

namespace SharpRail.UI.Plugins;

/// <summary>
/// The trailing end of a window's header where active plugins' items sit (W24). It follows the registry while shown,
/// so enabling or disabling a plugin adds or removes its item, and an item is rebuilt only when the set of contributing
/// registrations changes.
/// </summary>
internal sealed class PluginHeaderItems : StackPanel
{
    private readonly PluginRegistry registry;
    private IReadOnlyList<PluginRow<HeaderItemRegistration>> shown = [];

    internal PluginHeaderItems(PluginRegistry registry)
    {
        this.registry = registry;
        Name = "PluginHeaderItems";
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        AttachedToVisualTree += (_, _) => { registry.Changed += Changed; Sync(); };
        DetachedFromVisualTree += (_, _) => { registry.Changed -= Changed; Children.Clear(); shown = []; };
    }

    private void Changed(PluginTables tables)
    {
        if ((tables & (PluginTables.HeaderItems | PluginTables.Active | PluginTables.Roster)) == 0) return;
        if (Dispatcher.UIThread.CheckAccess()) Sync(); else Dispatcher.UIThread.Post(Sync);
    }

    private void Sync()
    {
        var next = registry.ActiveHeaderItems();
        if (next.SequenceEqual(shown)) return;
        shown = next;
        Children.Clear();
        foreach (var row in next)
        {
            var item = WorkbenchWindow.Mount(row.PluginId, row.Value.Create);
            item.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(item);
        }
    }
}