using Avalonia.Controls;
using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;
using SharpRail.UI.Plugins;

namespace SharpRail.UI.Plugins;

/// <summary>
/// A named place in one of core's pages (W23): the sections active plugins contribute to it, each under a rule. It
/// follows the registry while shown, so enabling or disabling a plugin adds or removes its section without a
/// placeholder, and a section is rebuilt only when the set of contributing registrations changes.
/// </summary>
internal sealed class PluginSlot : StackPanel
{
    private readonly PluginRegistry registry;
    private readonly string slot;
    private readonly string? workspace;
    private IReadOnlyList<PluginRow<SlotSectionRegistration>> shown = [];

    internal PluginSlot(PluginRegistry registry, string slot, string? workspace)
    {
        this.registry = registry; this.slot = slot; this.workspace = workspace;
        Name = "PluginSlot";
        AttachedToVisualTree += (_, _) => { registry.Changed += Changed; Sync(); };
        DetachedFromVisualTree += (_, _) => { registry.Changed -= Changed; Children.Clear(); shown = []; };
    }

    private void Changed(PluginTables tables)
    {
        if ((tables & (PluginTables.SlotSections | PluginTables.Active | PluginTables.Roster)) == 0) return;
        if (Dispatcher.UIThread.CheckAccess()) Sync(); else Dispatcher.UIThread.Post(Sync);
    }

    private void Sync()
    {
        var next = registry.SlotSectionsFor(slot);
        if (next.SequenceEqual(shown)) return;
        shown = next;
        Children.Clear();
        foreach (var row in next)
            Children.Add(new Border
            {
                Name = "PluginSlotSection_" + row.PluginId,
                BorderBrush = SharpRail.Plugins.UI.Kit.Ui.BorderBrush,
                BorderThickness = new(0, 1, 0, 0),
                Margin = new(0, 16, 0, 0),
                Padding = new(0, 16, 0, 0),
                Child = WorkbenchWindow.Mount(row.PluginId, () => row.Value.Create(workspace))
            });
    }
}