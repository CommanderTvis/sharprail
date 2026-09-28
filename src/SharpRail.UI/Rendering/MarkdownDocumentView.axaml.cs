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
    private readonly MarkdownPreview preview;
    private readonly ScrollViewer source;
    private readonly Button previewButton;
    private readonly Button sourceButton;
    private readonly ContentControl body;

    internal MarkdownDocumentView(FileDocument document, IProjectServices host, Preferences preferences, Action<string, string?> navigate)
    {
        AvaloniaXamlLoader.Load(this);
        preview = new(document.Text, document.Path, host, preferences, navigate);
        source = new ScrollViewer
        {
            Name = "MarkdownSource",
            Content = MarkdownPreview.Code(document.Text),
            Margin = new Thickness(20),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        previewButton = this.FindControl<Button>("MarkdownPreviewMode")!;
        sourceButton = this.FindControl<Button>("MarkdownSourceMode")!;
        body = this.FindControl<ContentControl>("MarkdownBody")!;
        previewButton.Click += (_, _) => ShowSource(false);
        sourceButton.Click += (_, _) => ShowSource(true);
        ShowSource(false);
    }

    private void ShowSource(bool show)
    {
        body.Content = show ? source : preview;
        previewButton.Background = show ? Brushes.Transparent : Ui.Hover;
        previewButton.Foreground = show ? Ui.Muted : Ui.TextBrush;
        sourceButton.Background = show ? Ui.Hover : Brushes.Transparent;
        sourceButton.Foreground = show ? Ui.TextBrush : Ui.Muted;
    }

    internal void ScrollToAnchor(string id) { ShowSource(false); preview.ScrollToAnchor(id); }
    public void Dispose() => preview.Dispose();
}
