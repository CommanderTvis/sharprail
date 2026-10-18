using Avalonia.Controls;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private Control LanguagePanel()
    {
        var workspace = workspaceRoot;
        return new Panels.LanguagePanel(host, workspace, async location =>
        {
            if (workspaceRoot == workspace && WorkspaceMounted)
                await OpenLanguageDocumentAsync(location, true);
        }, Workbench.State, async command =>
        {
            if (workspaceRoot != workspace || !WorkspaceMounted) return;
            var key = await OpenPluginTerminalAsync(workspace, new() { Command = command });
            await ShareAsync(SharpRail.Host.Abstractions.HostStateChange.TitleTerminal(new SharpRail.Plugins.Api.TerminalRef(workspace, key), "Air Context sign-in"));
        });
    }
}