using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Processes.UI;

/// <summary>The UI half: the Processes side tool. Its panel polls the host only while it is on screen.</summary>
public sealed class ProcessesUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        var view = new ProcessesView { AllProjects = context.Settings<ProcessesSettings>().ShowAllProjects, Persist = value => _ = SaveAsync(context, value) };
        context.SideTool(new(ProcessesPlugin.Tool, workspace => new ProcessesPanel(context, workspace, view)));
        context.SettingsSection(new("Processes", ProcessesPlugin.Manifest.Icon, () => new ProcessesSettingsView(context)));
        context.KeymapAction(new("OpenProcesses", "Open Processes", "Processes", () => Reveal(context, ProcessesPlugin.Tool))
        {
            Mac = ["Mod+Alt+Shift+P"],
            Other = ["Mod+Alt+Shift+P"]
        });
        return null;
    }

    private static void Reveal(IPluginUIContext context, string tool)
    {
        if (context.Host().ActiveWorkspaceId is { Length: > 0 } workspace) context.RevealTool(workspace, PluginIdentity.ToolId(context.Id, tool));
    }

    private static async Task SaveAsync(IPluginUIContext context, bool value)
    {
        try { await context.UpdateSettingsAsync(context.Settings<ProcessesSettings>() with { ShowAllProjects = value }); }
        catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't save the Processes pane's choice"); }
    }
}

/// <summary>The pane's view choice, shared by every Processes pane of this window set for the session: false shows only the pane's own project.</summary>
internal sealed class ProcessesView
{
    internal bool AllProjects { get; set; }

    /// <summary>Called with the new value when the user changes the choice, so the plugin can keep it for the next run.</summary>
    internal Action<bool>? Persist { get; init; }
}