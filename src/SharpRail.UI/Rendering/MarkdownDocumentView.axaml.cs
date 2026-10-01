using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

using SharpRail.Host.Abstractions;
using SharpRail.UI.State;

namespace SharpRail.UI.Rendering;

internal sealed partial class MarkdownDocumentView : UserControl, IDisposable
{
    internal enum Mode { Preview, Source, Split }

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

    internal MarkdownDocumentView(FileDocument document, IProjectServices host, Preferences preferences, Action<string, string?> navigate)
    {
        AvaloniaXamlLoader.Load(this);
        preview = new(document.Text, document.Path, host, preferences, navigate);
        sourceText = document.Text;
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
        FillOutline(this.FindControl<StackPanel>("MarkdownOutlineEntries")!, preview, sourceText,
            line => { if (source?.IsVisible == true) source.Offset = new Vector(source.Offset.X, line * SourceLineHeight); });
        Paint(outlineButton, false);
        var find = new FindBar(() => body);
        Grid.SetColumnSpan(find, 4);
        body.Children.Add(find);
        find.Attach(this);
        Show(Mode.Preview);
    }

    internal Mode View => mode;

    internal void Show(Mode next)
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

    // Read from the parsed source, so each entry knows both its rendered anchor and its source line.
    // A rendered diff shares it without a source side to reveal.
    internal static void FillOutline(StackPanel entries, MarkdownPreview preview, string sourceText, Action<int>? revealSource)
    {
        if (preview.SpecTitle is { } title)
        {
            var line = Array.FindIndex(sourceText.Split('\n'), text => text.StartsWith("title:", StringComparison.Ordinal));
            entries.Children.Add(Entry(1, title.Text ?? "", Math.Max(0, line), preview, preview.ScrollToTitle, revealSource));
        }
        foreach (var (level, text, id, line) in preview.Headings)
            entries.Children.Add(Entry(level, text, line, preview, () => preview.ScrollToAnchor(id), revealSource));
        if (entries.Children.Count == 0) entries.Children.Add(Ui.Text("No headings", Ui.Hint, 12));
    }

    private static Button Entry(int level, string text, int line, MarkdownPreview preview, Action scrollPreview, Action<int>? revealSource)
    {
        var label = Ui.Text(text, Ui.Muted, 12);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        var entry = new Button
        {
            Name = "OutlineEntry",
            Content = label,
            Tag = line,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            Padding = new Thickness(6 + (level - 1) * 12, 2, 6, 2),
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4)
        };
        AutomationProperties.SetName(entry, text);
        ToolTip.SetTip(entry, text);
        // Jumps whichever sides are showing, and selects neither.
        entry.Click += (_, _) =>
        {
            if (preview.IsVisible) scrollPreview();
            revealSource?.Invoke(line);
        };
        return entry;
    }

    internal void ScrollToAnchor(string id) { if (mode == Mode.Source) Show(Mode.Preview); preview.ScrollToAnchor(id); }
    public void Dispose() => preview.Dispose();
}