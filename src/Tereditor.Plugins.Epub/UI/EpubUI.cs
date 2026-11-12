using Tereditor.Plugins.Api.UI;

namespace Tereditor.Plugins.Epub.UI;

/// <summary>Registers the book viewer for the <c>.epub</c> files the manifest claims.</summary>
public sealed class EpubUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.FileViewer(new(props => new EpubViewer(context, props))
        {
            Matches = path => path.EndsWith(".epub", StringComparison.OrdinalIgnoreCase)
        });
        return null;
    }
}