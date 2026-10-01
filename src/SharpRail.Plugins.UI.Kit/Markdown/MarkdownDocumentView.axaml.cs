using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

using SharpRail.Plugins.UI.Kit.Editor;
namespace SharpRail.Plugins.UI.Kit.Markdown;

/// <summary>A Markdown file with Preview, Source and Split views, an outline column and find.</summary>
public sealed partial class MarkdownDocumentView : UserControl, IDisposable
{
    public enum Mode { Preview, Source, Split }

    private const double SourceLineHeight = 21;
    private MarkdownPreview preview;
    private readonly string path;
    private readonly MarkdownContext context;
    private Control? source;
    private string sourceText;
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
        this.path = path; this.context = context;
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
            RevealSource);
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
            if (OperatingSystem.IsMacOS())
            {
                var frame = new EditorFrame(sourceText, "MarkdownSource");
                frame.Editor.IsReadOnly = true;
                frame.Editor.WrapWidth = context.SourceWrapWidth;
                source = frame;
            }
            else source = new ScrollViewer
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

    /// <summary>Reloads disk content while preserving the selected view and its controls.</summary>
    public void Reload(string text)
    {
        var previewOffset = preview.Offset;
        var sourceOffset = (source as ScrollViewer)?.Offset ?? default;
        var sourceLine = (source as EditorFrame)?.Editor.FirstVisibleLine ?? 0;
        preview.Dispose();
        body.Children.Remove(preview);
        preview = new(text, path, context) { Offset = previewOffset };
        Grid.SetColumn(preview, 3);
        body.Children.Add(preview);
        sourceText = text;
        if (source is EditorFrame frame)
        {
            frame.Editor.IsReadOnly = false;
            try { frame.Editor.Text = sourceText; }
            finally { frame.Editor.IsReadOnly = true; }
            frame.Editor.ScrollToLine(sourceLine);
        }
        else if (source is ScrollViewer scroll)
        {
            scroll.Content = MarkdownPreview.Code(sourceText);
            scroll.Offset = sourceOffset;
        }
        var entries = this.FindControl<StackPanel>("MarkdownOutlineEntries")!;
        entries.Children.Clear();
        Outline.Fill(entries, preview, sourceText,
            RevealSource);
        Show(mode);
    }

    private void RevealSource(int line)
    {
        if (source?.IsVisible != true) return;
        if (source is EditorFrame frame) frame.Editor.ScrollToLine(line);
        else if (source is ScrollViewer scroll) scroll.Offset = new Vector(scroll.Offset.X, line * SourceLineHeight);
    }

    private static void Paint(Button button, bool active)
    {
        button.Background = active ? Ui.Hover : Brushes.Transparent;
        button.Foreground = active ? Ui.TextBrush : Ui.Muted;
    }

    public void ScrollToAnchor(string id) { if (mode == Mode.Source) Show(Mode.Preview); preview.ScrollToAnchor(id); }
    public void Dispose()
    {
        preview.Dispose();
        (source as EditorFrame)?.Editor.Dispose();
    }
}