using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

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
    private string selected = "";
    private string? reportedPath;
    private readonly string? emptyText;
    private bool started;

    /// <param name="emptyText">What a document tab says while the store holds no drawing for its key; a companion never shows without one.</param>
    public VisualizationPane(IPluginUIContext context, VisualizeUI.Drawings drawings, CompanionHost host, string? emptyText = null)
    {
        this.context = context; this.drawings = drawings; this.host = host; this.emptyText = emptyText;
        AvaloniaXamlLoader.Load(this);
        // A selection ends with a pointer release or a key (Select All); read it once the card has handled either.
        AddHandler(PointerReleasedEvent, (_, _) => Dispatcher.UIThread.Post(ReportSelection), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(KeyUpEvent, (_, _) => Dispatcher.UIThread.Post(ReportSelection), RoutingStrategies.Bubble, handledEventsToo: true);
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
        if (started && visualization?.Revision == shown?.Revision) return;
        started = true;
        shown = visualization;
        reported = null;
        if (selected.Length > 0) ReportEditorSelection(null);
        selected = "";
        this.FindControl<ContentControl>("Drawing")!.Content = visualization is null ? Placeholder() : VisualizationCard.Create(visualization.Args, interactive: true, error => Report(visualization.Revision, error));
    }

    private TextBlock? Placeholder() => emptyText is null ? null : new TextBlock { Name = "VisualizationEmpty", Text = emptyText, Opacity = 0.6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };

    /// <summary>Tells the host what the user has selected, so the agent's visualize_read can act on "this node".</summary>
    internal void ReportSelection()
    {
        if (shown is not { } visualization || this.FindControl<ContentControl>("Drawing")!.Content is not Control card) return;
        var text = VisualizationCard.SelectedText(card);
        if (text == selected) return;
        selected = text;
        ReportEditorSelection(text.Length == 0 ? null : card);
        _ = Send();

        async Task Send()
        {
            try { await context.RequestAsync(VisualizeContract.Select, new SelectionReport(host.WorkspaceId, host.TabKey, visualization.Revision) { Text = text.Length == 0 ? null : text }); }
            catch (Exception failure) when (failure is PluginCallException or OperationCanceledException) { }
        }
    }

    // A Markdown drawing is backed by a file on the host, so its selection is an editor selection of that file, mapped to source lines
    // like the file preview's. Other kinds have no file and report only to visualize_read.
    private void ReportEditorSelection(Control? card)
    {
        if (reportedPath is null && card is null) return;
        var path = card is null ? reportedPath : shown?.SourcePath;
        if (path is null) return;
        var editor = new EditorRef("visualize:" + host.WorkspaceId + ":" + host.TabKey, host.WorkspaceId, path, EditorKind.File, false);
        if (card is null) { context.Editors.ReportSelection(editor, null); reportedPath = null; return; }
        var text = VisualizationCard.SelectedText(card);
        context.Editors.ReportSelection(editor, VisualizationCard.SelectedLines(card) is { } lines
            ? new EditorSelection(lines.Start, 1, lines.End + 1, 1, text) : new EditorSelection(1, 1, 1, 1, text) { Mapped = false });
        reportedPath = path;
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