using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

using SharpRail.Host.Abstractions;
using SharpRail.UI.State;

namespace SharpRail.UI.Rendering;

internal sealed partial class MarkdownDocumentView : UserControl, IDisposable
{
    private MarkdownPreview preview;
    private readonly IProjectServices host;
    private readonly Preferences preferences;
    private readonly Action<string, string?> navigate;
    private ScrollViewer? source;
    private string sourceText;
    private readonly Button previewButton;
    private readonly Button sourceButton;
    private readonly ContentControl body;

    internal MarkdownDocumentView(FileDocument document, IProjectServices host, Preferences preferences, Action<string, string?> navigate)
    {
        AvaloniaXamlLoader.Load(this);
        this.host = host; this.preferences = preferences; this.navigate = navigate;
        preview = new(document.Text, document.Path, host, preferences, navigate);
        sourceText = document.Text;
        previewButton = this.FindControl<Button>("MarkdownPreviewMode")!;
        sourceButton = this.FindControl<Button>("MarkdownSourceMode")!;
        body = this.FindControl<ContentControl>("MarkdownBody")!;
        previewButton.Click += (_, _) => ShowSource(false);
        sourceButton.Click += (_, _) => ShowSource(true);
        ShowSource(false);
    }

    private void ShowSource(bool show)
    {
        if (show && source is null)
            source = new ScrollViewer
            {
                Name = "MarkdownSource",
                Content = MarkdownPreview.Code(sourceText),
                Margin = new Thickness(20),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
        body.Content = show ? source : preview;
        previewButton.Background = show ? Brushes.Transparent : Ui.Hover;
        previewButton.Foreground = show ? Ui.Muted : Ui.TextBrush;
        sourceButton.Background = show ? Ui.Hover : Brushes.Transparent;
        sourceButton.Foreground = show ? Ui.TextBrush : Ui.Muted;
    }

    internal void Reload(FileDocument document)
    {
        var showSource = source is not null && ReferenceEquals(body.Content, source);
        var previewOffset = preview.Offset;
        var sourceOffset = source?.Offset ?? default;
        preview.Dispose();
        preview = new(document.Text, document.Path, host, preferences, navigate) { Offset = previewOffset };
        sourceText = document.Text;
        source = null;
        ShowSource(showSource);
        if (source is not null) source.Offset = sourceOffset;
    }

    internal void ScrollToAnchor(string id) { ShowSource(false); preview.ScrollToAnchor(id); }
    public void Dispose() => preview.Dispose();
}