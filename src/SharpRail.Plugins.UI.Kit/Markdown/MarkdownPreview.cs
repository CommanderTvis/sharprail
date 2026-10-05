using System.Diagnostics;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Svg.Skia;

using Markdig;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using SharpRail.Plugins.UI.Kit.Visualization;

namespace SharpRail.Plugins.UI.Kit.Markdown;

public sealed partial class MarkdownPreview : ScrollViewer, IDisposable
{
    protected override Type StyleKeyOverride => typeof(ScrollViewer);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().UseYamlFrontMatter().Build();
    private static readonly HttpClient Images = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly Dictionary<string, Control> anchors = [];
    private readonly MarkdownContext context;
    private readonly string path;
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<IDisposable> loadedImages = [];
    private readonly List<(Border Holder, string Text)> diagrams = [];
    private ThemeManifest? diagramTheme;
    private readonly bool renderDiagrams;
    private string? diffMark;
    public MarkdownDocument Document { get; }

    public MarkdownPreview(string text, string path, MarkdownContext context)
        : this(Parse(text), path, context, frontmatter: Frontmatter.Parse(text)) { }

    /// <summary>
    /// Renders an already parsed document, so callers can parse large sources off the UI thread. The frontmatter
    /// renders as a properties block; a rendered diff passes the original side's as <paramref name="previousFrontmatter"/>
    /// so changed properties carry the same marks as the prose.
    /// </summary>
    public MarkdownPreview(MarkdownDocument document, string path, MarkdownContext context,
        bool renderDiagrams = true, DiffFocus? focus = null, FrontmatterBlock? frontmatter = null, FrontmatterBlock? previousFrontmatter = null)
    {
        this.context = context; this.path = path; this.renderDiagrams = renderDiagrams;
        Name = "MarkdownPreview";
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        Document = document;
        var body = new StackPanel
        {
            Spacing = 0,
            Margin = new Thickness(24, 16),
            MaxWidth = LineWidths.Markdown(context.LineWidth, context.LineWidthBounded, context.FontSize),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Spec = Frontmatter.Spec(frontmatter);
        if (Spec?.Title is { } specTitle)
        {
            SpecTitle = Ui.Text(specTitle, Ui.TextBrush, 24);
            SpecTitle.Name = "SpecTitle";
            SpecTitle.FontWeight = FontWeight.SemiBold;
            SpecTitle.TextWrapping = TextWrapping.Wrap;
            SpecTitle.Margin = new Thickness(0, 0, 0, 12);
            body.Children.Add(SpecTitle);
        }
        if (frontmatter is not null || previousFrontmatter is not null) body.Children.Add(Properties(frontmatter, previousFrontmatter));
        if (focus is null) body.Children.AddRange(RenderBlocks(Document));
        else RenderFocused(body, focus);
        RefreshSpecLinks();
        CollapseMargins(body);
        Content = body;
        WireSelection();
        foreach (var text in body.GetLogicalDescendants().OfType<SelectableTextBlock>()) text.PropertyChanged += SelectionMoved;
    }

    /// <summary>Raised with the selected text whenever a selection in the rendered document changes; empty when cleared.</summary>
    public event Action<string>? SelectionChanged;

    private void SelectionMoved(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != SelectableTextBlock.SelectionEndProperty || sender is not SelectableTextBlock text || text is DocumentText { Restoring: true }) return;
        SelectionChanged?.Invoke(Words(text));
    }

    /// <summary>Parses Markdown; inside a spec, <c>[[id]]</c> links become spec links. Ordinary Markdown keeps them as text.</summary>
    public static MarkdownDocument Parse(string text) =>
        Markdig.Markdown.Parse(Frontmatter.Spec(Frontmatter.Parse(text)) is null ? text : Frontmatter.LinkifyWikiLinks(text), Pipeline);

    public SpecIdentity? Spec { get; }

    /// <summary>A spec's frontmatter title, drawn above its properties; an element, not a heading in the source.</summary>
    public TextBlock? SpecTitle { get; }

    /// <summary>The document's headings in order, with the anchor each renders under and its zero-based source line.</summary>
    public IEnumerable<(int Level, string Text, string Id, int Line)> Headings =>
        Document.Descendants<HeadingBlock>().Select(heading => (heading.Level, Plain(heading.Inline ?? new ContainerInline()), heading.GetAttributes().Id ?? "", heading.Line));

    public void ScrollToTitle() => SpecTitle?.BringIntoView();

    public void ScrollToAnchor(string? id)
    {
        if (id is not null && anchors.TryGetValue(Uri.UnescapeDataString(id.TrimStart('#')), out var control))
            control.BringIntoView();
    }

    private Control Render(Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = Paragraph(heading.Inline, heading.Level switch { 1 => 24, 2 => 20, 3 => 18, 4 => 16, 5 => 14, _ => 12 }, FontWeight.SemiBold);
                title.LineHeight = title.FontSize * 1.25;
                title.FontWeight = FontWeight.SemiBold; title.Foreground = heading.Level == 6 ? Ui.Muted : Ui.TextBrush;
                title.Margin = new Thickness(0, heading.Level == 1 ? 0 : heading.Level == 2 ? 24 : heading.Level <= 4 ? 16 : 12,
                    0, heading.Level <= 2 ? 12 : heading.Level <= 4 ? 8 : 4);
                var anchor = heading.GetAttributes().Id ?? "";
                if (anchor.Length > 0) anchors[anchor] = title;
                if (heading.Level > 2) return title;
                var margin = title.Margin;
                title.Margin = new(0);
                return new Border
                {
                    Child = title,
                    BorderBrush = Ui.BorderBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 0, 0, 4),
                    Margin = margin
                };
            case ParagraphBlock paragraph:
                var content = Paragraph(paragraph.Inline, context.FontSize);
                content.Margin = new Thickness(0, 12);
                return content;
            case FencedCodeBlock fence when renderDiagrams && string.Equals(fence.Info?.Trim(), "mermaid", StringComparison.OrdinalIgnoreCase):
                return Mermaid(fence.Lines.ToString());
            case CodeBlock code:
                return CodeFrame(code.Lines.ToString());
            case ListBlock list:
                var items = new StackPanel { Spacing = 4, Margin = new Thickness(0, 12) };
                foreach (var row in ListRows(list)) items.Children.Add(row());
                return items;
            case Table table:
                var grid = new Grid { Margin = new Thickness(0, 12) };
                var rows = table.OfType<TableRow>().ToArray();
                var columns = rows.Select(row => row.Count).DefaultIfEmpty(0).Max();
                for (var column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new(GridLength.Auto));
                for (var row = 0; row < rows.Length; row++)
                {
                    grid.RowDefinitions.Add(new(GridLength.Auto));
                    for (var column = 0; column < rows[row].Count; column++)
                    {
                        var cell = (TableCell)rows[row][column];
                        var contents = Container(cell);
                        foreach (var child in contents.Children)
                        {
                            child.Margin = new Thickness(0);
                            if (rows[row].IsHeader && child is SelectableTextBlock cellText) cellText.FontWeight = FontWeight.SemiBold;
                        }
                        var border = new Border
                        {
                            Child = contents,
                            BorderBrush = Ui.BorderBrush,
                            BorderThickness = new Thickness(1),
                            Padding = new Thickness(8, 4),
                            Background = rows[row].IsHeader ? Ui.Elevated : (row % 2 == 0 ? Ui.Surface : Ui.Sidebar),
                            MinWidth = 100,
                            MaxWidth = 360
                        };
                        Ui.Place(grid, border, row, column);
                    }
                }
                return new ScrollViewer
                {
                    Content = grid,
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
                };
            case AlertBlock alert:
                var kind = alert.Kind.ToString().ToLowerInvariant();
                var color = kind switch { "note" => Ui.Info, "tip" => Ui.Success, "warning" => Ui.Warning, "caution" => Ui.Danger, _ => Ui.Accent };
                var iconName = kind switch { "note" => "alertInfo", "tip" => "alertTip", "warning" => "alertWarning", "caution" => "alertCaution", _ => "alertImportant" };
                var callout = Container(alert);
                foreach (var paragraph in callout.Children) paragraph.Margin = new Thickness(0, 4);
                var alertTitle = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 0, 4) };
                alertTitle.Children.Add(Ui.Icon(iconName, color, 16));
                var alertLabel = Ui.Text(char.ToUpperInvariant(kind[0]) + kind[1..], color, 14);
                alertLabel.FontWeight = FontWeight.SemiBold;
                alertLabel.LineHeight = 17.5;
                alertTitle.Children.Add(alertLabel);
                callout.Children.Insert(0, alertTitle);
                callout.Children[^1].Margin = new Thickness(0, 4, 0, 0);
                return new Border
                {
                    Child = callout,
                    Name = "MarkdownAlert",
                    Tag = kind,
                    BorderBrush = color,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Background = kind switch { "note" => Ui.InfoWash, "tip" => Ui.SuccessWash, "warning" => Ui.WarningWash, "caution" => Ui.DangerWash, _ => Ui.PrimarySubtle },
                    CornerRadius = new CornerRadius(0, 4, 4, 0),
                    Padding = new Thickness(12, 8),
                    Margin = new Thickness(0, 12)
                };
            case QuoteBlock quote:
                var contentsQuote = Container(quote);
                foreach (var paragraph in contentsQuote.GetLogicalDescendants().OfType<SelectableTextBlock>().Where(text => text.FontWeight != FontWeight.SemiBold))
                {
                    paragraph.Foreground = Ui.Muted;
                    foreach (var strong in paragraph.Inlines?.OfType<Run>().Where(run => run.FontWeight == FontWeight.Medium) ?? [])
                        strong.Foreground = Ui.TextBrush;
                }
                return new Border
                {
                    Child = contentsQuote,
                    BorderBrush = Ui.PrimaryMuted,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    Padding = new Thickness(12, 0, 0, 0),
                    Margin = new Thickness(0, 12)
                };
            case HtmlBlock html:
                return Html(html.Lines.ToString());
            case ThematicBreakBlock:
                return new Border { Height = 1, Background = Ui.BorderBrush, Margin = new Thickness(0, 24) };
            case ContainerBlock container:
                return Container(container);
            default:
                return new Border();
        }
    }

    // One builder per item, each with the number it has in the whole list.
    private List<Func<Control>> ListRows(ListBlock list)
    {
        var rows = new List<Func<Control>>();
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var task = item.OfType<ParagraphBlock>().FirstOrDefault()?.Inline?.FirstChild as TaskList;
            var label = task is not null ? null : list.IsOrdered ? $"{number++}." : "•";
            rows.Add(() =>
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition(context.FontSize * 1.6, GridUnitType.Pixel));
                row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
                if (label is not null)
                {
                    var marker = Ui.Text(label, Ui.TextBrush, context.FontSize);
                    marker.VerticalAlignment = VerticalAlignment.Top;
                    marker.LineHeight = context.FontSize * 1.6;
                    marker.Margin = new Thickness(0, 2, 0, 0);
                    Ui.Place(row, marker);
                }
                var contents = Container(item); contents.Margin = new Thickness(0);
                foreach (var child in contents.Children) child.Margin = new Thickness(0, 2);
                Ui.Place(row, contents, 0, label is not null ? 1 : 0);
                if (label is null) Grid.SetColumnSpan(contents, 2);
                return row;
            });
        }
        return rows;
    }

    private StackPanel Container(ContainerBlock block)
    {
        var panel = new StackPanel();
        panel.Children.AddRange(RenderBlocks(block));
        CollapseMargins(panel);
        return panel;
    }

    private static void CollapseMargins(StackPanel panel)
    {
        var previousBottom = 0d;
        for (var index = 0; index < panel.Children.Count; index++)
        {
            var child = panel.Children[index];
            var margin = child.Margin;
            child.Margin = new(margin.Left, index == 0 ? 0 : Math.Max(0, margin.Top - previousBottom), margin.Right,
                index == panel.Children.Count - 1 ? 0 : margin.Bottom);
            previousBottom = margin.Bottom;
        }
    }

    private SelectableTextBlock Paragraph(ContainerInline? inline, double size, FontWeight? weight = null)
    {
        var text = new DocumentText
        {
            Foreground = Ui.TextBrush,
            FontSize = size,
            FontWeight = weight ?? Ui.InterfaceWeight,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = size * 1.6
        };
        diffMark = null;
        if (inline is not null)
        {
            if (inline.Descendants().OfType<LinkInline>().Any(link => link.IsImage)) text.ClearValue(TextBlock.LineHeightProperty);
            AddInline(text.Inlines!, inline, weight ?? Ui.InterfaceWeight, FontStyle.Normal, false);
        }
        return text;
    }

    private void AddInline(InlineCollection target, ContainerInline container, FontWeight weight, FontStyle style, bool strike)
    {
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(Mark(new Run(literal.Content.ToString())
                    {
                        FontWeight = weight,
                        FontStyle = style,
                        TextDecorations = strike ? TextDecorations.Strikethrough : null
                    }));
                    break;
                case HtmlInline html when DiffTag().Match(html.Tag) is { Success: true } tag:
                    diffMark = tag.Groups[1].Success ? null : tag.Groups[2].Value;
                    break;
                case HtmlInline html when HtmlTokens(html.Tag) is [{ Tag: "img", Closing: false } picture] && HtmlImage(picture, HorizontalAlignment.Left) is { } inlinePicture:
                    target.Add(new InlineUIContainer(inlinePicture));
                    break;
                case HtmlInline html when HtmlTokens(html.Tag) is [{ Tag: "br" }]:
                    target.Add(new Run("\n"));
                    break;
                case EmphasisInline emphasis:
                    AddInline(target, emphasis,
                        emphasis.DelimiterCount >= 2 && emphasis.DelimiterChar != '~' ? FontWeight.Medium : weight,
                        emphasis.DelimiterCount == 1 ? FontStyle.Italic : style, emphasis.DelimiterChar == '~' || strike);
                    break;
                case CodeInline code:
                    target.Add(Mark(new Run(code.Content) { FontFamily = Ui.CodeFont, Foreground = Ui.TextBrush, FontSize = context.FontSize - 1 }));
                    break;
                case LineBreakInline line:
                    target.Add(new Run(line.IsHard ? "\n" : " "));
                    break;
                case LinkInline link when link.IsImage:
                    var holder = new Border { Child = Ui.Text(Plain(link)), Margin = new Thickness(0, 8), MaxWidth = 800 };
                    target.Add(new InlineUIContainer(holder));
                    _ = LoadImageAsync(holder, link.Url ?? "");
                    break;
                case LinkInline link when link.Url?.StartsWith(Frontmatter.SpecScheme, StringComparison.Ordinal) == true:
                    target.Add(new InlineUIContainer(MarkLink(SpecLink(Uri.UnescapeDataString(link.Url[Frontmatter.SpecScheme.Length..]), Plain(link), weight, style), strike)));
                    break;
                case LinkInline link when ResolveLink(path, link.Url ?? "") is null:
                    // A target outside the worktree, or one that does not decode, is shown as the text it is.
                    AddInline(target, link, weight, style, strike);
                    break;
                case LinkInline link:
                    var url = link.Url ?? "";
                    var button = LinkButton(Plain(link), weight, style);
                    ToolTip.SetTip(button, url);
                    button.Click += (_, _) => Follow(url);
                    target.Add(new InlineUIContainer(MarkLink(button, strike)));
                    break;
                case TaskList task:
                    target.Add(new Run(task.Checked ? "☑ " : "☐ "));
                    break;
                case ContainerInline nested:
                    AddInline(target, nested, weight, style, strike);
                    break;
                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;
            }
        }
    }

    private MarkdownLink MarkLink(MarkdownLink link, bool strike)
    {
        var text = (TextBlock)link.Content!;
        if (strike || diffMark == "del") text.TextDecorations = TextDecorations.Strikethrough;
        if (diffMark is { } mark)
        {
            var color = mark == "ins" ? Ui.Success : Ui.Danger;
            text.Classes.Add(mark);
            text.Foreground = color;
            text.Background = mark == "ins" ? Ui.SuccessWash : Ui.DangerWash;
        }
        return link;
    }

    // Rendered diffs mark merged insertions and deletions the way the reference styles <ins> and <del>.
    private Run Mark(Run run)
    {
        if (diffMark is null) return run;
        run.Classes.Add(diffMark);
        run.Foreground = diffMark == "ins" ? Ui.Success : Ui.Danger;
        run.Background = diffMark == "ins" ? Ui.SuccessWash : Ui.DangerWash;
        if (diffMark == "del") run.TextDecorations = TextDecorations.Strikethrough;
        return run;
    }

    [GeneratedRegex(@"^<(/)?(ins|del)>$", RegexOptions.IgnoreCase)]
    private static partial Regex DiffTag();

    private static string Plain(ContainerInline inline)
    {
        var text = new System.Text.StringBuilder();
        for (var child = inline.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is LiteralInline literal) text.Append(literal.Content);
            else if (child is CodeInline code) text.Append(code.Content);
            else if (child is ContainerInline nested) text.Append(Plain(nested));
        }
        return text.ToString();
    }

    private void Follow(string url)
    {
        if (url.StartsWith('#')) { ScrollToAnchor(url); return; }
        // A leading slash is the worktree root, not a file URI.
        if (!url.StartsWith('/') && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "https" or "http" or "mailto") Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return;
        }
        if (ResolveLink(path, url) is { } target) context.Navigate(target, url.Split('#', 2) is [_, var anchor] ? anchor : null);
    }

    /// <summary>
    /// The worktree-relative file a relative link in <paramref name="document"/> names, or the link itself when
    /// it is an anchor or an absolute URI. Null means the link cannot be followed: it climbs out of the worktree
    /// or its percent-encoding is malformed.
    /// </summary>
    public static string? ResolveLink(string document, string url)
    {
        if (url.StartsWith('#') || !url.StartsWith('/') && Uri.TryCreate(url, UriKind.Absolute, out _)) return url;
        var file = url.Split('#', 2)[0];
        if (MalformedEscape().IsMatch(file)) return null;
        file = Uri.UnescapeDataString(file).Replace('\\', '/');
        if (file.Contains('\0')) return null;
        var segments = new List<string>();
        if (!file.StartsWith('/')) segments.AddRange((Path.GetDirectoryName(document) ?? "").Split('/', '\\').Where(part => part.Length > 0));
        foreach (var part in file.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part != "..") segments.Add(part);
            else if (segments.Count == 0) return null;
            else segments.RemoveAt(segments.Count - 1);
        }
        return string.Join('/', segments);
    }

    [GeneratedRegex("%(?![0-9A-Fa-f]{2})")]
    private static partial Regex MalformedEscape();

    private Border CodeFrame(string text)
    {
        var source = Code(text);
        var codeBody = new StackPanel { Spacing = 8 };
        var copy = Ui.Button("Copy", () => _ = CopyAsync(text), "check");
        copy.HorizontalAlignment = HorizontalAlignment.Right;
        codeBody.Children.Add(copy);
        codeBody.Children.Add(new ScrollViewer { Content = source, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        return new Border
        {
            Child = codeBody,
            Background = Ui.Elevated,
            Padding = new Thickness(12),
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new(4),
            Margin = new Thickness(0, 12)
        };
    }

    private Control Mermaid(string text)
    {
        var holder = new Border { Margin = new Thickness(0, 12), Child = Ui.Text("Rendering diagram…", Ui.Muted, 12) };
        diagrams.Add((holder, text));
        _ = LoadMermaidAsync(holder, text);
        return holder;
    }

    private async Task LoadMermaidAsync(Border holder, string text)
    {
        var theme = diagramTheme = Ui.Theme;
        var options = MermaidRenderer.Options();
        MermaidRenderer.Result result;
        SvgSource? svg = null;
        try
        {
            result = await Task.Run(() =>
            {
                var rendered = MermaidRenderer.Render(text, options);
                if (rendered.Svg is not null) svg = SvgSource.LoadFromSvg(rendered.Svg);
                return rendered;
            }, lifetime.Token);
        }
        catch (OperationCanceledException) { return; }
        if (lifetime.IsCancellationRequested || theme != diagramTheme) { svg?.Dispose(); return; }
        if (result.Error is { } error || svg?.Picture is not { } picture)
        {
            var failure = new StackPanel { Name = "MermaidError", Spacing = 4 };
            svg?.Dispose();
            var message = Ui.Text("Diagram failed to render: " + (result.Error ?? "the SVG could not be read."), Ui.Danger, 12);
            message.TextWrapping = TextWrapping.Wrap;
            var source = CodeFrame(text);
            source.Margin = new Thickness(0);
            failure.Children.Add(message); failure.Children.Add(source);
            holder.Child = failure;
            return;
        }
        loadedImages.Add(svg);
        var fullscreen = new Button
        {
            Name = "MermaidFullscreen",
            Content = Ui.Icon("fullscreen", size: 14),
            Padding = new Thickness(4),
            Margin = new Thickness(4),
            Background = Ui.Elevated,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetName(fullscreen, "View diagram full screen");
        ToolTip.SetTip(fullscreen, "Full screen");
        fullscreen.Click += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner) MermaidDialog.Show(owner, svg);
        };
        var diagram = MermaidDialog.Inline(svg, new Size(picture.CullRect.Width, picture.CullRect.Height), fullscreen);
        holder.Child = diagram;
    }

    private async Task LoadImageAsync(Border holder, string url)
    {
        try
        {
            byte[] data;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme is not ("https" or "http")) return;
                using var response = await Images.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new IOException("Image too large.");
                using var stream = await response.Content.ReadAsStreamAsync(lifetime.Token);
                using var memory = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(buffer, lifetime.Token)) > 0)
                {
                    if (memory.Length + count > 8 * 1024 * 1024) throw new IOException("Image too large.");
                    memory.Write(buffer, 0, count);
                }
                data = memory.ToArray();
            }
            else
            {
                var relative = Path.GetFullPath(Path.Combine("/", Path.GetDirectoryName(path) ?? "", Uri.UnescapeDataString(url))).TrimStart('/');
                data = await context.ReadImage(relative, lifetime.Token) ?? throw new IOException("Not an image.");
            }
            lifetime.Token.ThrowIfCancellationRequested();
            using var bytes = new MemoryStream(data);
            var bitmap = new Bitmap(bytes);
            loadedImages.Add(bitmap);
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 800 };
            image.Bind(Image.MaxWidthProperty, new Binding("Bounds.Width")
            { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(SelectableTextBlock) } });
            holder.Child = image;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or ArgumentException)
        {
            if (!lifetime.IsCancellationRequested) ToolTip.SetTip(holder, error.Message);
        }
    }

    private async Task CopyAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(text);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Ui.ThemeChanged += RethemeDiagrams;
        if (diagramTheme is not null && diagramTheme != Ui.Theme) RethemeDiagrams();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Ui.ThemeChanged -= RethemeDiagrams;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Mermaid bakes its colours into the SVG, so a theme swap renders each diagram again.</summary>
    private void RethemeDiagrams()
    {
        foreach (var (holder, text) in diagrams) _ = LoadMermaidAsync(holder, text);
    }

    public void Dispose()
    {
        Ui.ThemeChanged -= RethemeDiagrams;
        lifetime.Cancel();
        foreach (var image in loadedImages) image.Dispose();
        loadedImages.Clear();
    }

    public static SelectableTextBlock Code(string text)
    {
        var block = new DocumentText
        {
            FontFamily = Ui.CodeFont,
            FontSize = 13,
            Foreground = Ui.TextBrush,
            TextWrapping = TextWrapping.NoWrap,
            LineHeight = 21
        };
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var cursor = 0;
            foreach (Match match in Regex.Matches(line, @"(""[^""]*""|'[^']*'|//.*$|\b(?:class|public|private|return|var|using|const|function|async|await|if|else|true|false|null)\b|\b\d+\b)"))
            {
                block.Inlines!.Add(new Run(line[cursor..match.Index]));
                block.Inlines.Add(new Run(match.Value)
                {
                    Foreground = match.Value.StartsWith("//", StringComparison.Ordinal) ? Ui.Hint :
                    match.Value.StartsWith('"') || match.Value.StartsWith('\'') ? Ui.Success : Ui.Accent
                });
                cursor = match.Index + match.Length;
            }
            block.Inlines!.Add(new Run(index < lines.Length - 1 ? line[cursor..] + "\n" : line[cursor..]));
        }
        return block;
    }
}