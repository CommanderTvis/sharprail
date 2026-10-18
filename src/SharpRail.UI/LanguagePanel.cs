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
        }, Workbench.State, Workbench.PluginRegistry);
    }
}