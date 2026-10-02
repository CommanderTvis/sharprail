using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit.Visualization;

namespace SharpRail.Plugins.Visualize.UI;

/// <summary>The companion pane: the terminal's drawing through the kit's card, reporting the render verdict back to the host once per shown revision.</summary>
internal sealed partial class VisualizationPane : Border
{
    private readonly IPluginUIContext context;
    private readonly VisualizeUI.Drawings drawings;
    private readonly CompanionHost host;
    private TerminalVisualization? shown;
    private int? reported;

    public VisualizationPane(IPluginUIContext context, VisualizeUI.Drawings drawings, CompanionHost host)
    {
        this.context = context; this.drawings = drawings; this.host = host;
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        drawings.Changed += Refresh;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        drawings.Changed -= Refresh;
    }

    private void Refresh()
    {
        // A publish for another tab of the workspace leaves this drawing, and its zoom, alone.
        var visualization = drawings.Get(host);
        if (visualization?.Revision == shown?.Revision) return;
        shown = visualization;
        reported = null;
        this.FindControl<ContentControl>("Drawing")!.Content = visualization is null ? null : VisualizationCard.Create(visualization.Args, interactive: true, error => Report(visualization.Revision, error));
    }

    private void Report(int revision, string? error)
    {
        if (shown?.Revision != revision || reported == revision) return;
        reported = revision;
        _ = Send();

        async Task Send()
        {
            try { await context.RequestAsync(VisualizeContract.Report, new RenderReport(host.WorkspaceId, host.TabKey, revision) { Error = error }); }
            catch (Exception failure) when (failure is PluginCallException or OperationCanceledException) { }
        }
    }
}