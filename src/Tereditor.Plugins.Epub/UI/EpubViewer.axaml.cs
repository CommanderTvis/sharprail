using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using Markdig.Syntax;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;
using SharpRail.Plugins.UI.Kit.Markdown;

namespace SharpRail.Plugins.Epub.UI;

/// <summary>
/// One book tab. The host converts the book to Markdown; the parse runs off the UI thread and the kit's Markdown
/// renderer draws it, mounting only the blocks near the viewport. Scroll mode reads continuously beside the table of
/// contents; paged mode shows one viewport-sized page at a time, with page breaks computed in background slices.
/// </summary>
internal sealed partial class EpubViewer : UserControl
{
    internal const string Document = "book.md";
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(8);
    private readonly IPluginUIContext context;
    private readonly string workspace;
    private readonly string path;
    // One per attachment: the dock may detach a tab and attach it again, so loads restart rather than end for good.
    private CancellationTokenSource lifetime = new();
    private readonly Border host;
    private readonly Border mask;
    private readonly TextBlock status;
    private readonly TextBlock pageLabel;
    private readonly StackPanel pager;
    private readonly StackPanel tocEntries;
    private readonly Border toc;
    private readonly ToggleButton scrollMode = Ui.Segment("EpubScrollMode", "Scroll");
    private readonly ToggleButton pagedMode = Ui.Segment("EpubPagedMode", "Pages");
    private readonly FindBar find;
    private IDisposable? revision;
    private EpubBook? book;
    private MarkdownPreview? preview;
    private EpubPages? pages;
    private int loads;
    // A TOC jump or link followed before the book finished rendering, taken once it has.
    private int? waiting;
    private int page;
    // The content offset the reader is at in paged mode; pagination snaps it to the page that holds it.
    private double location;
    private bool paginating;
    private bool steering;
    private Size paginatedFor;
    private Point? swipe;
    // The block at the top of the shown page and how far into it the page starts, which survives a re-layout.
    private (int Line, double Fraction)? anchor;
    // The anchor to find again once the sweep has measured the blocks above it; estimates before that are inexact.
    private (int Line, double Fraction)? pending;
    private readonly DispatcherTimer relayout = new() { Interval = TimeSpan.FromMilliseconds(200) };

    // Compiled XAML never uses the runtime loader; this public constructor only makes Avalonia register the view (AVLN3001).
    public EpubViewer() => throw new NotSupportedException("EpubViewer needs its constructor arguments.");

    public EpubViewer(IPluginUIContext context, FileViewerProps props)
    {
        AvaloniaXamlLoader.Load(this);
        this.context = context; workspace = props.WorkspaceId; path = props.Path;
        Name = "EpubViewer";
        Background = Ui.Surface;
        host = this.FindControl<Border>("EpubDocument")!;
        mask = this.FindControl<Border>("EpubPageMask")!;
        mask.Background = Ui.Surface;
        status = this.FindControl<TextBlock>("EpubStatus")!;
        status.Foreground = Ui.Muted;
        pageLabel = this.FindControl<TextBlock>("EpubPage")!;
        pageLabel.Foreground = Ui.Muted;
        pager = this.FindControl<StackPanel>("EpubPager")!;
        tocEntries = this.FindControl<StackPanel>("EpubTocEntries")!;
        toc = this.FindControl<Border>("EpubToc")!;
        toc.BorderBrush = Ui.BorderBrush;
        toc.Background = Ui.Sidebar;
        this.FindControl<Border>("Toolbar")!.BorderBrush = Ui.BorderBrush;
        this.FindControl<TextBlock>("EpubTitle")!.Foreground = Ui.TextBrush;
        this.FindControl<Button>("EpubTocToggle")!.Click += (_, _) => toc.IsVisible = !toc.IsVisible;
        this.FindControl<Button>("EpubPrevious")!.Click += (_, _) => Turn(-1);
        this.FindControl<Button>("EpubNext")!.Click += (_, _) => Turn(1);
        var modes = this.FindControl<StackPanel>("EpubModes")!;
        modes.Children.Add(scrollMode); modes.Children.Add(pagedMode);
        scrollMode.Click += (_, _) => SetPaged(false);
        pagedMode.Click += (_, _) => SetPaged(true);
        ShowMode(false);
        var area = this.FindControl<Panel>("EpubPageArea")!;
        find = new FindBar(() => preview);
        area.Children.Add(find);
        find.Attach(this);
        area.PropertyChanged += (_, e) => { if (e.Property == BoundsProperty) Repaginate(); };
        AddHandler(KeyDownEvent, KeyPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        area.AddHandler(PointerWheelChangedEvent, Wheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        area.AddHandler(PointerPressedEvent, (_, e) => swipe = e.Pointer.Type == PointerType.Touch && Paged ? e.GetPosition(this) : null, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        area.AddHandler(PointerReleasedEvent, Swiped, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        relayout.Tick += (_, _) => { relayout.Stop(); Repaginate(force: true); };
    }

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        lifetime = new CancellationTokenSource();
        Ui.ThemeChanged += ThemeChanged;
        revision = context.ObserveFileRevision(workspace, path, _ => Load());
        if (preview is null) Load();
    }

    internal bool Paged { get; private set; }
    internal MarkdownPreview? Preview => preview;
    internal EpubBook? Book => book;
    internal int Page => page;
    internal int PageCount => pages?.Tops.Count ?? 0;
    internal bool PaginationComplete => pages?.Complete == true;
    internal IReadOnlyList<double> PageTops => pages?.Tops ?? [];

    internal void GoToPage(int index)
    {
        if (pages is null || index < 0 || index >= pages.Tops.Count) return;
        pending = null;
        location = pages.Tops[index];
        ShowPage();
    }

    protected override void OnDetachedFromLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromLogicalTree(e);
        Ui.ThemeChanged -= ThemeChanged;
        revision?.Dispose();
        relayout.Stop();
        lifetime.Cancel();
    }

    private async void Load()
    {
        var token = lifetime.Token;
        var generation = ++loads;
        try
        {
            var opened = await Task.Run(async () => await context.RequestAsync(EpubContract.Open, new(workspace, path), token), token);
            if (opened.Error is { } error) { Fail("This EPUB could not be read: " + error); return; }
            var parsed = await Task.Run(() => MarkdownPreview.Parse(opened.Markdown), token);
            if (token.IsCancellationRequested || generation != loads) return;
            Show(opened, parsed);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested && generation == loads) Fail("This EPUB could not be opened: " + error.Message);
        }
    }

    private void Fail(string message)
    {
        status.Text = message;
        status.Foreground = Ui.Danger;
        status.IsVisible = true;
    }

    private void Show(EpubBook opened, MarkdownDocument parsed)
    {
        var settings = context.Host().AppSettings;
        var keep = preview is null ? (double?)null : Paged ? location : preview.Offset.Y;
        preview?.Dispose();
        book = opened;
        preview = new MarkdownPreview(parsed, Document, new(ReadImage, _ => null, Navigate, Ui.FontSize, settings.MarkdownLineWidth, settings.MarkdownLineWidthBounded), renderDiagrams: false, progressive: true)
        { Name = "EpubPreview" };
        preview.ScrollChanged += Scrolled;
        var shown = preview;
        preview.Rendered += () => { if (preview == shown) Rendered(keep); };
        host.Child = preview;
        status.IsVisible = false;
        this.FindControl<TextBlock>("EpubTitle")!.Text = opened.Authors.Length > 0 ? opened.Title + " — " + opened.Authors : opened.Title;
        tocEntries.Children.Clear();
        foreach (var entry in opened.Toc)
        {
            var label = Ui.Text(entry.Title.Length > 0 ? entry.Title : "Untitled", Ui.TextBrush, 13);
            label.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            var button = new Button
            {
                Name = "EpubTocEntry",
                Content = label,
                Background = Avalonia.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8 + 12 * Math.Min(entry.Depth, 4), 4, 8, 4),
                Tag = entry.Line
            };
            button.Click += (_, _) => GoToLine(entry.Line);
            tocEntries.Children.Add(button);
        }
        pages = null;
        location = keep ?? 0;
        if (preview.RenderComplete) Rendered(keep);
    }

    // The preview builds a large book's blocks in slices; offsets, TOC jumps and pages wait for the whole book.
    private void Rendered(double? keep)
    {
        if (keep is { } offset) Dispatcher.UIThread.Post(() => { if (preview is not null && !Paged) preview.Offset = new Vector(0, offset); }, DispatcherPriority.Loaded);
        if (Paged) Repaginate(force: true);
        if (waiting is { } line) { waiting = null; GoToLine(line); }
    }

    private async ValueTask<byte[]?> ReadImage(string relative, CancellationToken token)
    {
        if (!relative.StartsWith(EpubContract.ResourcePrefix, StringComparison.Ordinal)) return null;
        var href = relative[EpubContract.ResourcePrefix.Length..];
        var resource = await Task.Run(async () => await context.RequestAsync(EpubContract.Resource, new(workspace, path, href), token), token);
        return resource.Data.Length == 0 ? null : resource.Data;
    }

    private void Navigate(string target, string? anchor)
    {
        if (target.StartsWith(EpubContract.LinePrefix, StringComparison.Ordinal)
            && int.TryParse(target.AsSpan(EpubContract.LinePrefix.Length), System.Globalization.CultureInfo.InvariantCulture, out var line))
            GoToLine(line);
    }

    /// <summary>Jumps to the block holding a one-based Markdown line: a table-of-contents entry or an in-book link.</summary>
    internal void GoToLine(int line)
    {
        if (preview is null) return;
        if (!preview.RenderComplete) { waiting = line; return; }
        steering = true;
        try
        {
            if (preview.LineOffset(line) is not { } offset) return;
            if (Paged) { pending = null; location = offset; ShowPage(); }
            else preview.Offset = new Vector(0, offset);
        }
        finally { steering = false; }
    }

    internal void SetPaged(bool paged)
    {
        if (preview is null || paged == Paged) { ShowMode(paged); return; }
        location = paged ? preview.Offset.Y : pages is { } known && page < known.Tops.Count ? known.Tops[page] : location;
        ShowMode(paged);
        if (paged)
        {
            anchor = preview.SourceAt(location);
            Repaginate(force: true);
        }
        else
        {
            mask.IsVisible = false;
            preview.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            steering = true;
            preview.Offset = new Vector(0, location);
            steering = false;
        }
    }

    private void ShowMode(bool paged)
    {
        Paged = paged;
        scrollMode.IsChecked = !paged; pagedMode.IsChecked = paged;
        scrollMode.Foreground = paged ? Ui.Muted : Ui.TextBrush; pagedMode.Foreground = paged ? Ui.TextBrush : Ui.Muted;
        scrollMode.Background = paged ? Avalonia.Media.Brushes.Transparent : Ui.Selected; pagedMode.Background = paged ? Ui.Selected : Avalonia.Media.Brushes.Transparent;
        pager.IsVisible = paged;
        if (preview is not null) preview.VerticalScrollBarVisibility = paged ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
    }

    private void ThemeChanged() => Repaginate(force: true);

    /// <summary>Starts the page sweep again, keeping the reader on the page that holds their current location.</summary>
    internal void Repaginate(bool force = false)
    {
        if (!Paged || preview is null || !preview.RenderComplete) return;
        var size = preview.Bounds.Size;
        if (!force && size == paginatedFor && pages is not null) return;
        paginatedFor = size;
        // Offsets move when the text reflows; the block the page started in does not.
        if (pages is not null && anchor is { } kept)
        {
            preview.UpdateLayout();
            if (preview.LineOffset(kept.Line, fraction: kept.Fraction) is { } offset) location = offset;
            pending = kept;
        }
        pages = new EpubPages(preview, book?.Chapters.Select(chapter => chapter.Line).ToList() ?? []);
        if (!paginating) { paginating = true; Dispatcher.UIThread.Post(Sweep, DispatcherPriority.Background); }
        ShowPage();
    }

    private void Sweep()
    {
        if (preview is null || pages is null || !Paged) { paginating = false; return; }
        steering = true;
        try
        {
            pages.Advance(Slice);
            // Once the sweep is done every block above the anchor is measured, so revealing it is exact.
            if (pending is { } wanted)
            {
                var found = preview.LineOffset(wanted.Line, reveal: pages.Complete, fraction: wanted.Fraction);
                if (found is { } at && (at < pages.Tops[^1] || pages.Complete)) { location = at; pending = null; }
                else if (pages.Complete) pending = null;
            }
            ShowPage();
        }
        finally { steering = false; }
        if (pages.Complete) paginating = false;
        else Dispatcher.UIThread.Post(Sweep, DispatcherPriority.Background);
    }

    /// <summary>Shows the page holding <see cref="location"/>, or the location itself while the sweep has not reached it.</summary>
    private void ShowPage()
    {
        if (preview is null || pages is null) return;
        var height = preview.Viewport.Height;
        var steered = steering;
        steering = true;
        try
        {
            if (pending is null && pages.PageAt(location) is { } index)
            {
                page = index;
                var top = pages.Tops[index];
                preview.Offset = new Vector(0, top);
                anchor = preview.SourceAt(top);
                var visible = pages.Bottom(index, height) - top;
                mask.IsVisible = visible < height - 0.5;
                mask.Margin = new Thickness(0, Math.Max(0, visible), 0, 0);
                mask.Height = Math.Max(0, height - visible);
                pageLabel.Text = $"Page {index + 1} of {pages.Tops.Count}{(pages.Complete ? "" : "…")}";
            }
            else
            {
                preview.Offset = new Vector(0, location);
                mask.IsVisible = false;
                pageLabel.Text = "Paginating…";
            }
        }
        finally { steering = steered; }
    }

    /// <summary>Turns the page; past a chapter's last page the next chapter's first page follows.</summary>
    internal void Turn(int direction)
    {
        if (preview is null || pages is null) return;
        if (pages.PageAt(location) is not { } index) return;
        var next = index + direction;
        if (next < 0 || next >= pages.Tops.Count) return;
        pending = null;
        location = pages.Tops[next];
        ShowPage();
    }

    private void Scrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (steering || !Paged || preview is null || pages is null) return;
        if (e.ExtentDelta != default || e.ViewportDelta != default)
        {
            // A re-layout, not a reader's scroll: an image arrived, a block was measured or the viewer resized, so
            // the breaks moved. Re-paginate once it settles, from the block the page started in.
            if (pages.Complete) { relayout.Stop(); relayout.Start(); }
            return;
        }
        var shown = pages.PageAt(location) is { } index ? pages.Tops[index] : location;
        if (e.OffsetDelta.Y == 0 || Math.Abs(preview.Offset.Y - shown) < 0.5) return;
        // Something else scrolled the page, such as Find revealing a match: move to the page that shows where it
        // looked, the bottom edge when it scrolled down and the top edge when it scrolled up.
        location = e.OffsetDelta.Y > 0 ? preview.Offset.Y + Math.Max(0, preview.Viewport.Height - 32) : preview.Offset.Y + 16;
        Dispatcher.UIThread.Post(ShowPage, DispatcherPriority.Render);
    }

    private void KeyPressed(object? sender, KeyEventArgs e)
    {
        if (!Paged || e.KeyModifiers != KeyModifiers.None || find.IsKeyboardFocusWithin) return;
        var direction = e.Key switch
        {
            Key.Right or Key.PageDown or Key.Space or Key.Down => 1,
            Key.Left or Key.PageUp or Key.Up => -1,
            _ => 0
        };
        if (direction == 0) return;
        Turn(direction);
        e.Handled = true;
    }

    private void Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (!Paged) return;
        if (Math.Abs(e.Delta.Y) > 0.01) Turn(e.Delta.Y < 0 ? 1 : -1);
        e.Handled = true;
    }

    private void Swiped(object? sender, PointerReleasedEventArgs e)
    {
        if (swipe is not { } start || !Paged) return;
        swipe = null;
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < 48 || Math.Abs(delta.X) < Math.Abs(delta.Y)) return;
        Turn(delta.X < 0 ? 1 : -1);
        e.Handled = true;
    }
}