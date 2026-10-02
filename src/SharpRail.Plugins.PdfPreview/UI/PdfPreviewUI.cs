using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.PdfPreview.UI;

/// <summary>The UI half: one file viewer for <c>.pdf</c>, whose extension and read strategy the manifest declares.</summary>
public sealed class PdfPreviewUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.FileViewer(new(props => new PdfPreview(context, props)));
        return null;
    }
}