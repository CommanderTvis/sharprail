using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
namespace SharpRail.Plugins.UI.Kit.Markdown;

/// <summary>A Markdown file with Preview, Source and Split views, an outline column and find.</summary>
public sealed partial class MarkdownDocumentView : UserControl, IDisposable
{
    public enum Mode { Preview, Source, Split }

    private const double SourceLineHeight = 21;
    private readonly MarkdownPreview preview;
    private ScrollViewer? source;
    private readonly string sourceText;
    private readonly Button previewButton;
    private readonly Button sourceButton;
    private readonly Button splitButton;
    private readonly Button outlineButton;
    private readonly Grid body;
    private readonly Border outline;
    private readonly GridSplitter splitter;
    private Mode mode;

    public MarkdownDocumentView(string text, string path, MarkdownContext context)
    {
        AvaloniaXamlLoader.Load(this);
        preview = new(text, path, context);
        sourceText = text;
        previewButton = this.FindControl<Button>("MarkdownPreviewMode")!;
        sourceButton = this.FindControl<Button>("MarkdownSourceMode")!;
        splitButton = this.FindControl<Button>("MarkdownSplitMode")!;
        outlineButton = this.FindControl<Button>("MarkdownOutlineToggle")!;
        body = this.FindControl<Grid>("MarkdownBody")!;
        outline = this.FindControl<Border>("MarkdownOutline")!;
        splitter = this.FindControl<GridSplitter>("MarkdownSplitter")!;
        Grid.SetColumn(preview, 3);
        body.Children.Add(preview);
        previewButton.Click += (_, _) => Show(Mode.Preview);
        sourceButton.Click += (_, _) => Show(Mode.Source);
        splitButton.Click += (_, _) => Show(Mode.Split);
        outlineButton.Click += (_, _) => { outline.IsVisible = !outline.IsVisible; Paint(outlineButton, outline.IsVisible); };
        Outline.Fill(this.FindControl<StackPanel>("MarkdownOutlineEntries")!, preview, sourceText,
            line => { if (source?.IsVisible == true) source.Offset = new Vector(source.Offset.X, line * SourceLineHeight); });
        Paint(outlineButton, false);
        var find = new FindBar(() => body);
        Grid.SetColumnSpan(find, 4);
        body.Children.Add(find);
        find.Attach(this);
        Show(Mode.Preview);
    }

    public Mode View => mode;
    public MarkdownPreview Preview => preview;

    public void Show(Mode next)
    {
        mode = next;
        if (next != Mode.Preview && source is null)
        {
            source = new ScrollViewer
            {
                Name = "MarkdownSource",
                Content = MarkdownPreview.Code(sourceText),
                Margin = new Thickness(20),
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            Grid.SetColumn(source, 1);
            body.Children.Add(source);
        }
        var showSource = next != Mode.Preview;
        var showPreview = next != Mode.Source;
        body.ColumnDefinitions[1].Width = showSource ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        body.ColumnDefinitions[3].Width = showPreview ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (source is not null) source.IsVisible = showSource;
        preview.IsVisible = showPreview;
        splitter.IsVisible = next == Mode.Split;
        Paint(previewButton, next == Mode.Preview);
        Paint(sourceButton, next == Mode.Source);
        Paint(splitButton, next == Mode.Split);
    }

    private static void Paint(Button button, bool active)
    {
        button.Background = active ? Ui.Hover : Brushes.Transparent;
        button.Foreground = active ? Ui.TextBrush : Ui.Muted;
    }

    public void ScrollToAnchor(string id) { if (mode == Mode.Source) Show(Mode.Preview); preview.ScrollToAnchor(id); }
    public void Dispose() => preview.Dispose();
}