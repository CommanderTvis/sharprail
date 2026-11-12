using Avalonia.Controls;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private Control LanguagePanel()
    {
        Panels.LanguagePanel? panel = null;
        return panel = new Panels.LanguagePanel(host, workspaceRoot, async location =>
        {
            if (workspaceRoot == panel!.WorkspaceRoot && WorkspaceMounted)
                await OpenLanguageDocumentAsync(location, true);
        }, Workbench.State, Workbench.PluginRegistry);
    }
}