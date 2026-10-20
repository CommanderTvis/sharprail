using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;

namespace SharpRail.UI;

internal sealed class ChangesReviewState
{
    internal readonly HashSet<string> Viewed = [];
    internal readonly Dictionary<string, HashSet<string>> Kept = [];
    internal readonly Dictionary<string, IReadOnlyList<string>> Keys = [];
    internal readonly Dictionary<string, bool> Collapsed = [];
    internal string? ActivePath;
    internal double Offset;
    internal bool Split = true;
    internal bool IgnoreWhitespace;

    internal bool IsKept(string path, string key) => Kept.GetValueOrDefault(path)?.Contains(key) == true;
    internal void Keep(string path, string key, bool value, IReadOnlyList<string> keys)
    {
        if (!Kept.TryGetValue(path, out var kept)) Kept[path] = kept = [];
        var changed = value ? kept.Add(key) : kept.Remove(key);
        Keys[path] = keys;
        if (changed && value && keys.Count > 0 && keys.All(kept.Contains)) Viewed.Add(path);
    }
}

internal sealed partial class ChangesReviewPane : UserControl, IDisposable
{
    // A file card's chrome. Cards near the viewport hold one; offscreen cards return theirs to the pool and keep only
    // their frame at its last measured (or estimated) height. Handlers read the card's current owner.
    private sealed class Card(Grid container, Grid header, Grid body, TextBlock title, Button titleButton, TextBlock progress, CheckBox viewed)
    {
        internal readonly Grid Container = container;
        internal readonly Grid Header = header;
        internal readonly Grid Body = body;
        internal readonly TextBlock Title = title;
        internal readonly Button TitleButton = titleButton;
        internal readonly TextBlock Progress = progress;
        internal readonly CheckBox Viewed = viewed;
        internal Section? Owner;
    }

    private sealed class Section(GitChange change, Border frame, bool collapsed)
    {
        internal GitChange Change = change;
        internal readonly Border Frame = frame;
        internal readonly bool BuiltCollapsed = collapsed;
        internal Card? Card;
        internal DiffView? View;
        internal bool Loading;
        internal bool Stale;
        internal bool Failed;
        internal double Height;
        internal bool SourcePresentation;
    }

    private readonly ChangesReviewState state;
    private readonly Func<CancellationToken, Task<GitSnapshot>> read;
    private readonly Func<GitChange, bool, DiffTriage?, CancellationToken, Task<DiffView>> load;
    private readonly Action<GitChange> openFile;
    private readonly Action changed;
    private readonly Action allChanges;
    private readonly Func<bool> ownsKeyboard;
    private readonly Func<bool> single;
    private readonly Action<bool> setSingle;
    private readonly bool mutable;
    private readonly Grid body;
    private readonly TextBlock summary;
    private readonly TextBlock progress;
    private readonly Dictionary<string, Section> sections = [];
    private readonly Dictionary<string, GitChange> files = [];
    private ScrollViewer? scroll;
    private readonly Grid sticky = new() { Name = "ReviewStickyHeader", VerticalAlignment = VerticalAlignment.Top, IsVisible = false };
    private Section? pinned;
    private StackPanel? rows;
    private readonly Stack<Card> pool = new();
    private (Control Target, double Y)? scrollAnchor;
    private CancellationTokenSource? reading;
    private long generation;
    private bool disposed;
    private bool refreshPending = true;
    private bool updating;
    private bool queued;
    private bool awaitingLayout;
    private bool noticeDismissed;
    private string? reveal;
    private string? anchored;

    internal ChangesReviewPane(string label, ChangesReviewState state, bool mutable,
        Func<CancellationToken, Task<GitSnapshot>> read,
        Func<GitChange, bool, DiffTriage?, CancellationToken, Task<DiffView>> load,
        Action<GitChange> openFile, Action changed, Action allChanges, Func<bool> ownsKeyboard,
        Func<bool> single, Action<bool> setSingle)
    {
        this.state = state; this.mutable = mutable; this.read = read; this.load = load;
        this.openFile = openFile; this.changed = changed; this.allChanges = allChanges;
        this.ownsKeyboard = ownsKeyboard; this.single = single; this.setSingle = setSingle;
        AvaloniaXamlLoader.Load(this);
        Name = "ChangesReview";
        body = this.FindControl<Grid>("ReviewBody")!;
        summary = this.FindControl<TextBlock>("ReviewSummary")!;
        progress = this.FindControl<TextBlock>("ReviewProgress")!;
        this.FindControl<TextBlock>("ReviewScope")!.Text = label;
        this.FindControl<TextBlock>("ReviewScope")!.Foreground = Ui.TextBrush;
        summary.Foreground = progress.Foreground = Ui.Muted;
        Background = Ui.Surface;
        foreach (var name in new[] { "ReviewStacked", "ReviewSingle", "ReviewSplit", "ReviewInline", "ReviewWhitespace" })
        {
            var toggle = this.FindControl<ToggleButton>(name)!;
            foreach (var status in new[] { "Checked", "CheckedPointerOver", "CheckedPressed", "PointerOver", "Pressed" })
                toggle.Resources["ToggleButtonBackground" + status] = status.StartsWith("Checked", StringComparison.Ordinal) ? Ui.Selected : Ui.Hover;
        }
        Wire("ReviewStacked", () => Mode(false)); Wire("ReviewSingle", () => Mode(true));
        Wire("ReviewSplit", () => Presentation(true, state.IgnoreWhitespace));
        Wire("ReviewInline", () => Presentation(false, state.IgnoreWhitespace));
        Wire("ReviewWhitespace", () => Presentation(state.Split, !state.IgnoreWhitespace));
        Compact("ReviewWhitespace", "Hide whitespace changes", "collapseVertical");
        Compact("ReviewCollapse", "Collapse all files", "collapseVertical");
        Compact("ReviewExpand", "Expand all files", "expandVertical");
        Wire("ReviewCollapse", () => Collapse(true)); Wire("ReviewExpand", () => Collapse(false));
        Wire("ReviewPrevious", () => Step(-1)); Wire("ReviewNext", () => Step(1));
        Wire("ReviewNextUnviewed", NextUnviewed);
        Wire("ReviewMarkAll", () => { state.Viewed.UnionWith(files.Keys); ProgressChanged(); });
        Wire("ReviewScaleSingle", () => Mode(true));
        Wire("ReviewScaleDismiss", () => { noticeDismissed = true; UpdateProgress(); });
        KeyDown += OnKey;
        LayoutUpdated += (_, _) =>
        {
            KeepAnchor();
            if (!awaitingLayout || !IsArrangeValid) return;
            awaitingLayout = false;
            QueueVisible();
        };
        AddHandler(PointerWheelChangedEvent, (_, _) => { reveal = null; anchored = null; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, (_, _) => { reveal = null; anchored = null; }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        ShowNotice("Loading changes…");
    }

    private void Wire(string name, Action action) => this.FindControl<Button>(name)!.Click += (_, _) => action();
    private void Compact(string name, string label, string icon)
    {
        var button = this.FindControl<Button>(name)!;
        button.Content = Ui.Icon(icon); button.Width = 28;
        ToolTip.SetTip(button, label); AutomationProperties.SetName(button, label);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        awaitingLayout = true;
        if (refreshPending) _ = RefreshAsync();
        else
        {
            reading?.Dispose(); reading = new();
            QueueVisible();
        }
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (scroll is not null) state.Offset = scroll.Offset.Y;
        reading?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    internal async Task RefreshAsync()
    {
        if (disposed) return;
        refreshPending = true;
        if (VisualRoot is null) return;
        reading?.Cancel(); reading?.Dispose(); reading = new();
        var token = reading.Token;
        var stamp = ++generation;
        try
        {
            var snapshot = await read(token);
            if (disposed || token.IsCancellationRequested || stamp != generation) return;
            refreshPending = false;
            var oldPaths = files.Keys.ToArray();
            files.Clear();
            foreach (var file in snapshot.Changes) files[file.Path] = file;
            foreach (var removedPath in state.Kept.Keys.Where(path => !files.ContainsKey(path)).ToArray())
            {
                state.Kept.Remove(removedPath); state.Keys.Remove(removedPath);
            }
            summary.Text = $"{files.Count} files · +{files.Values.Sum(file => file.Added)} −{files.Values.Sum(file => file.Removed)}";
            if (!snapshot.IsRepository) { ClearSections(); ShowNotice("This project is not a git repository."); return; }
            if (files.Count == 0) { ClearSections(); ShowNotice("No changes in this scope."); UpdateProgress(); return; }
            if (state.ActivePath is null || !files.ContainsKey(state.ActivePath)) state.ActivePath = files.Keys.First();
            if (!oldPaths.SequenceEqual(files.Keys) || sections.Count == 0) Build();
            else
            {
                foreach (var section in sections.Values)
                {
                    section.Change = files[section.Change.Path];
                    section.Failed = false;
                    section.Stale = true;
                }
                QueueVisible();
            }
            UpdateProgress();
            if (reveal is { } path) Reveal(path);
        }
        catch (OperationCanceledException) { }
        catch (HostException error) when (error.Code == HostErrorCode.UnknownCommit)
        {
            if (disposed || stamp != generation) return;
            files.Clear(); ClearSections();
            ShowNotice("That commit is no longer in this branch.", allChanges, "Review all changes");
            UpdateProgress();
        }
        catch (Exception error)
        {
            if (disposed || token.IsCancellationRequested || stamp != generation) return;
            if (sections.Count == 0) ShowNotice("Could not read changes: " + error.Message, () => _ = RefreshAsync());
            else summary.Text = "Could not refresh changes: " + error.Message;
        }
    }

    private void ShowNotice(string text, Action? retry = null, string label = "Retry")
    {
        body.Children.Clear();
        var notice = new StackPanel { Margin = new Thickness(12), Spacing = 8 };
        notice.Children.Add(Ui.Text(text, Ui.Hint));
        if (retry is not null) notice.Children.Add(Ui.Button(label, retry));
        body.Children.Add(notice);
    }

    private void Build()
    {
        if (scroll is not null) state.Offset = scroll.Offset.Y;
        ClearSections(); body.Children.Clear();
        scrollAnchor = null;
        var rows = this.rows = new StackPanel { Classes = { "review-sections" } };
        foreach (var file in files.Values.Where(file => !single() || file.Path == state.ActivePath))
        {
            var frame = new Border { Name = "ReviewSection", Tag = file.Path, BorderBrush = Ui.BorderBrush, Classes = { "review-section" } };
            var section = new Section(file, frame, IsCollapsed(file));
            sections[file.Path] = section;
            if (single()) { frame.VerticalAlignment = VerticalAlignment.Stretch; Realize(section); }
            else { frame.Height = Estimate(section); rows.Children.Add(frame); }
        }
        if (single())
        {
            scroll = null;
            if (sections.Values.FirstOrDefault() is { } section) body.Children.Add(section.Frame);
        }
        else
        {
            var end = new Border { Name = "ReviewEnd", Classes = { "review-end" }, Child = Ui.Text($"End of changes · {files.Count} files", Ui.Hint, 12) };
            rows.Children.Add(end);
            scroll = new ScrollViewer { Name = "ReviewScroll", Content = rows };
            scroll.AddHandler(PointerWheelChangedEvent, (_, e) =>
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.Y == 0) return;
                // A bounded section scrolls its own viewport.
                if (e.Source is Visual source && source.FindAncestorOfType<DiffView>(includeSelf: true) is { IsBounded: true }) return;
                scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y - e.Delta.Y * 50);
                e.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            scroll.AddHandler(ScrollGestureEvent, (_, e) =>
            {
                if (e.Delta.Y == 0) return;
                scroll.Offset = new Vector(scroll.Offset.X, scroll.Offset.Y + e.Delta.Y);
                e.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            var offset = state.Offset;
            scroll.ScrollChanged += (_, e) =>
            {
                state.Offset = scroll.Offset.Y;
                // A moved offset re-anchors at once, before deferred realization changes heights above it.
                if (e.OffsetDelta.Y != 0) RecordAnchor();
                QueueVisible();
            };
            scroll.SizeChanged += (_, _) => QueueVisible();
            rows.SizeChanged += (_, _) => QueueVisible();
            body.Children.Add(scroll);
            body.Children.Add(sticky);
            Dispatcher.UIThread.Post(() => { if (!disposed && scroll is not null) { scroll.Offset = new Vector(0, offset); QueueVisible(); } }, DispatcherPriority.Loaded);
        }
        UpdateProgress(); QueueVisible();
    }

    private static readonly string[] GeneratedSuffixes = [".lock", ".min.js", ".min.css", ".snap", ".map"];

    private bool IsCollapsed(GitChange file) => !single() && state.Collapsed.GetValueOrDefault(file.Path,
        file.Added + file.Removed > 400 || GeneratedSuffixes.Any(suffix => file.Path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) ||
        Path.GetFileName(file.Path) is "package-lock.json" or "pnpm-lock.yaml" or "yarn.lock");

    private void QueueVisible()
    {
        if (queued || disposed) return;
        queued = true;
        Dispatcher.UIThread.Post(() => { queued = false; Visible(); }, DispatcherPriority.Loaded);
    }

    private void Visible()
    {
        if (disposed || VisualRoot is null || reading is null || reading.IsCancellationRequested) return;
        if (!IsArrangeValid || scroll is not null && scroll.Viewport.Height <= 0)
        {
            awaitingLayout = true;
            return;
        }
        string? active = null;
        var revealing = reveal is not null;
        Section? pin = null;
        foreach (var section in sections.Values)
        {
            var top = scroll is null ? 0 : section.Frame.TranslatePoint(default, scroll)?.Y ?? double.MaxValue;
            if (top <= 32) active = section.Change.Path;
            if (scroll is not null && top < 0 && top + section.Frame.Bounds.Height > 32) pin = section;
            var height = section.Frame.Bounds.Height;
            var visible = !IsCollapsed(section.Change) && (scroll is null ||
                top < scroll.Bounds.Height * 2 && top + height > -scroll.Bounds.Height);
            var near = scroll is null || top < scroll.Bounds.Height * 3 && top + height > -scroll.Bounds.Height * 2;
            if (near && section.Card is null) Realize(section);
            if (visible && (section.View is null || section.Stale) && !section.Loading && !section.Failed) _ = LoadAsync(section, generation, reading.Token);
            else if (!visible && section.View is not null) Unmount(section);
            if (!near && section.Card is not null && section.View is null && !ReferenceEquals(section, pin) && !ReferenceEquals(section, pinned)) Release(section);
        }
        Pin(pin);
        PlaceReveal();
        RecordAnchor();
        if (!revealing && anchored is null && active is not null && active != state.ActivePath) { state.ActivePath = active; changed(); }
    }

    private async Task LoadAsync(Section section, long stamp, CancellationToken token)
    {
        section.Loading = true;
        var retained = section.View;
        if (retained is null && section.Card is { } loading)
        {
            loading.Body.Children.Clear();
            loading.Body.Children.Add(Ui.Text("Loading diff…", Ui.Hint));
        }
        try
        {
            DiffTriage? triage = mutable ? new(key => state.IsKept(section.Change.Path, key), (key, value, keys) =>
            {
                state.Keep(section.Change.Path, key, value, keys); ProgressChanged();
            }, keys =>
            {
                state.Keys[section.Change.Path] = keys;
                state.Kept.GetValueOrDefault(section.Change.Path)?.IntersectWith(keys);
                UpdateProgress();
            }) : null;
            var view = await load(section.Change, !single(), triage, token);
            if (disposed || token.IsCancellationRequested || stamp != generation || !ReferenceEquals(sections.GetValueOrDefault(section.Change.Path), section) ||
                section.Card is not { } card)
            {
                if (!ReferenceEquals(view, retained)) view.Dispose();
                return;
            }
            section.Stale = false;
            section.View = view;
            if (!ReferenceEquals(view, retained)) view.ViewChanged += () =>
            {
                section.SourcePresentation = view.HasSourcePresentation;
                UpdatePresentationControls();
            };
            view.SetPresentation(state.Split, state.IgnoreWhitespace);
            section.SourcePresentation = view.HasSourcePresentation;
            UpdatePresentationControls();
            if (!ReferenceEquals(view, retained))
            {
                retained?.Dispose();
                card.Body.MinHeight = 0;
                card.Body.Children.Clear(); card.Body.Children.Add(view);
            }
            QueueVisible();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (disposed || token.IsCancellationRequested || stamp != generation) return;
            section.Failed = true;
            section.View?.Dispose(); section.View = null;
            if (section.Card is not { } failed) return;
            failed.Body.MinHeight = 80;
            var notice = new StackPanel { Spacing = 4, Margin = new Thickness(12) };
            notice.Children.Add(Ui.Text("Could not read diff: " + error.Message, Ui.Danger));
            notice.Children.Add(Ui.Button("Retry", () => { section.Failed = false; _ = LoadAsync(section, generation, reading!.Token); }));
            failed.Body.Children.Clear(); failed.Body.Children.Add(notice);
        }
        finally { section.Loading = false; if (token.IsCancellationRequested) QueueVisible(); }
    }

    internal bool Contains(DiffView view) => sections.Values.Any(section => ReferenceEquals(section.View, view));
    internal DiffView? DiffFor(string path) => sections.GetValueOrDefault(path)?.View;
    internal void SetCanRevert(bool value)
    {
        foreach (var section in sections.Values)
            if (section.View is { } view) view.CanRevert = value;
    }
    internal void UpdateMode() { Build(); Reveal(state.ActivePath ?? ""); }
    private void Unmount(Section section)
    {
        section.View?.Dispose(); section.View = null;
        if (section.Card is not { } card) return;
        section.Height = card.Body.Bounds.Height;
        card.Body.Children.Clear();
        card.Body.MinHeight = IsCollapsed(section.Change) ? 0 : section.Height;
    }
    private void ClearSections()
    {
        Pin(null);
        foreach (var section in sections.Values)
        {
            section.View?.Dispose(); section.View = null;
            if (section.Card is not null) Release(section);
        }
        sections.Clear();
    }

    // Until a card's body is measured, its height is estimated from the file's changed lines.
    private double Estimate(Section section) => 2 + 32 + (section.BuiltCollapsed ? 0
        : section.Height > 0 ? section.Height : Math.Min(20000, 232 + 40 * (section.Change.Added + section.Change.Removed)));

    private Card NewCard()
    {
        Card? card = null;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), MinHeight = 32, Classes = { "review-header" } };
        var text = new TextBlock { TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, FontSize = 12 };
        var title = Ui.Button("", () =>
        {
            if (card?.Owner is not { } owner) return;
            state.ActivePath = owner.Change.Path;
            state.Collapsed[owner.Change.Path] = !IsCollapsed(owner.Change);
            Build(); changed();
        });
        title.Name = "ReviewFile";
        title.Classes.Add("review-file");
        title.ClearValue(PaddingProperty);
        title.ClearValue(BorderThicknessProperty);
        title.Content = text;
        title.ClipToBounds = true;
        title.HorizontalAlignment = HorizontalAlignment.Stretch;
        title.HorizontalContentAlignment = HorizontalAlignment.Left;
        var kept = Ui.Text("", Ui.Hint, 12);
        var open = new Button { Name = "ReviewOpenFile", Content = "Open as tab" };
        open.Classes.Add("review-action");
        open.Click += (_, _) => { if (card?.Owner is { } owner) openFile(owner.Change); };
        var viewed = new CheckBox { Name = "ReviewViewed", Content = "Viewed", Margin = new Thickness(8, 0), FontSize = 12, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
        viewed.IsCheckedChanged += (_, _) =>
        {
            if (updating || card?.Owner is not { } owner) return;
            if (viewed.IsChecked == true) state.Viewed.Add(owner.Change.Path); else state.Viewed.Remove(owner.Change.Path);
            ProgressChanged();
        };
        Ui.Place(header, title); Ui.Place(header, kept, 0, 1); Ui.Place(header, open, 0, 2); Ui.Place(header, viewed, 0, 3);
        var content = new Grid();
        var container = new Grid { RowDefinitions = new RowDefinitions("32,*") };
        Ui.Place(container, header); Ui.Place(container, content, 1);
        return card = new Card(container, header, content, text, title, kept, viewed);
    }

    // Binds a pooled (or new) card to the section and puts it in the frame.
    private void Realize(Section section)
    {
        var card = pool.TryPop(out var pooled) ? pooled : NewCard();
        var file = section.Change;
        card.Owner = section; section.Card = card;
        card.Title.Text = file.Path + $"   +{file.Added} −{file.Removed}";
        card.TitleButton.Tag = file.Path;
        ToolTip.SetTip(card.TitleButton, file.Path);
        card.Progress.Tag = file.Path;
        card.Viewed.Tag = file.Path;
        AutomationProperties.SetName(card.Viewed, "Viewed " + file.Path);
        updating = true;
        try
        {
            card.Viewed.IsChecked = state.Viewed.Contains(file.Path);
            var keys = state.Keys.GetValueOrDefault(file.Path) ?? [];
            card.Progress.Text = mutable && keys.Count > 0 ? $"{keys.Count(key => state.IsKept(file.Path, key))}/{keys.Count} kept" : "";
        }
        finally { updating = false; }
        card.Body.Children.Clear();
        card.Body.MinHeight = single() || section.BuiltCollapsed ? 0 : section.Height > 0 ? section.Height : Estimate(section) - 34;
        section.Frame.Child = card.Container;
        section.Frame.Height = double.NaN;
    }

    // Returns the card's chrome to the pool; the frame keeps the height the card had.
    private void Release(Section section)
    {
        if (section.Card is not { } card) return;
        if (ReferenceEquals(section, pinned)) Pin(null);
        var height = section.Frame.Bounds.Height;
        if (!section.BuiltCollapsed && card.Body.Bounds.Height > 0) section.Height = card.Body.Bounds.Height;
        section.Frame.Child = null;
        section.Frame.Height = height > 0 ? height : Estimate(section);
        card.Body.Children.Clear();
        card.Owner = null; section.Card = null;
        pool.Push(card);
    }

    // The scroll anchor is the topmost section, or hunk inside it, that crosses the viewport's top edge. When a card or
    // hunk above it realizes or releases with a different height, the offset moves by the same amount.
    private void RecordAnchor()
    {
        scrollAnchor = null;
        if (scroll is null || rows is null || reveal is not null || scroll.Offset.Y <= 0) return;
        var top = scroll.Offset.Y;
        foreach (var section in sections.Values)
        {
            if (section.Frame.TranslatePoint(default, rows) is not { } point) continue;
            if (point.Y > top || point.Y + section.Frame.Bounds.Height <= top) continue;
            Control target = section.Frame;
            foreach (var candidate in section.View?.AnchorCandidates ?? [])
                if (candidate.TranslatePoint(default, rows) is { } inner && inner.Y <= top && inner.Y + candidate.Bounds.Height > top) { target = candidate; break; }
            if (target.TranslatePoint(default, rows) is { } anchor) scrollAnchor = (target, anchor.Y);
            return;
        }
    }

    private void KeepAnchor()
    {
        if (scrollAnchor is not var (target, y) || scroll is null || rows is null || reveal is not null) return;
        if (target.TranslatePoint(default, rows) is not { } point) { scrollAnchor = null; return; }
        var delta = point.Y - y;
        if (Math.Abs(delta) <= .5) return;
        scrollAnchor = (target, point.Y);
        scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + delta));
    }

    private void Pin(Section? section)
    {
        if (ReferenceEquals(section, pinned)) return;
        if (pinned?.Card is { } previous)
        {
            sticky.Children.Remove(previous.Header);
            previous.Container.Children.Add(previous.Header);
        }
        pinned = section?.Card is null ? null : section;
        if (pinned?.Card is { } card)
        {
            card.Container.Children.Remove(card.Header);
            sticky.Children.Add(card.Header);
        }
        sticky.IsVisible = pinned is not null;
    }

    internal void Reveal(string path)
    {
        reveal = anchored = path;
        if (!files.ContainsKey(path)) return;
        state.ActivePath = path; state.Collapsed[path] = false;
        if (single() || !sections.TryGetValue(path, out var section) || section.BuiltCollapsed) Build();
        Dispatcher.UIThread.Post(() =>
        {
            if (disposed || reveal != path) return;
            PlaceReveal(); QueueVisible(); changed();
        }, DispatcherPriority.Loaded);
    }

    private void PlaceReveal()
    {
        if (reveal is not { } path || sections.GetValueOrDefault(path) is not { } target) return;
        if (scroll is not null && target.Frame.TranslatePoint(default, scroll) is { } point)
        {
            var offset = Math.Clamp(scroll.Offset.Y + point.Y, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            if (Math.Abs(offset - scroll.Offset.Y) > .5)
            {
                scroll.Offset = new Vector(0, offset);
                QueueVisible();
                return;
            }
        }
        if (target.View is null || target.View.Bounds.Height <= 0) return;
        target.Card?.Header.GetLogicalDescendants().OfType<Button>().FirstOrDefault()?.Focus();
        reveal = null;
    }

    private void ProgressChanged() { UpdateProgress(); changed(); }
    private void UpdateProgress()
    {
        updating = true;
        try
        {
            progress.Text = $"{files.Keys.Count(state.Viewed.Contains)} of {files.Count} reviewed";
            foreach (var section in sections.Values)
            {
                if (section.Card is not { } card) continue;
                card.Viewed.IsChecked = state.Viewed.Contains(section.Change.Path);
                var keys = state.Keys.GetValueOrDefault(section.Change.Path) ?? [];
                card.Progress.Text = mutable && keys.Count > 0 ? $"{keys.Count(key => state.IsKept(section.Change.Path, key))}/{keys.Count} kept" : "";
            }
            this.FindControl<Button>("ReviewPrevious")!.IsVisible = single();
            this.FindControl<Button>("ReviewNext")!.IsVisible = single();
            this.FindControl<Button>("ReviewCollapse")!.IsVisible = !single();
            this.FindControl<Button>("ReviewExpand")!.IsVisible = !single();
            this.FindControl<ToggleButton>("ReviewStacked")!.IsChecked = !single();
            this.FindControl<ToggleButton>("ReviewSingle")!.IsChecked = single();
            this.FindControl<ToggleButton>("ReviewSplit")!.IsChecked = state.Split;
            this.FindControl<ToggleButton>("ReviewInline")!.IsChecked = !state.Split;
            this.FindControl<ToggleButton>("ReviewWhitespace")!.IsChecked = state.IgnoreWhitespace;
            UpdatePresentationControls();
            this.FindControl<WrapPanel>("ReviewScaleNotice")!.IsVisible = !single() && files.Count > 50 && !noticeDismissed;
        }
        finally { updating = false; }
    }
    private void UpdatePresentationControls()
    {
        var source = sections.Values.Any(section => section.SourcePresentation && !IsCollapsed(section.Change));
        foreach (var name in new[] { "ReviewSplit", "ReviewInline", "ReviewWhitespace" })
            this.FindControl<ToggleButton>(name)!.IsVisible = source;
    }
    private void Mode(bool value) { setSingle(value); Build(); Reveal(state.ActivePath ?? ""); }
    private void Presentation(bool split, bool whitespace)
    {
        state.Split = split; state.IgnoreWhitespace = whitespace;
        foreach (var section in sections.Values) section.View?.SetPresentation(split, whitespace);
        UpdateProgress();
    }
    private void Collapse(bool value)
    {
        foreach (var file in files.Keys) state.Collapsed[file] = value;
        Build();
    }
    private void Step(int delta)
    {
        var paths = files.Keys.ToArray();
        var index = Array.IndexOf(paths, state.ActivePath);
        if (index + delta >= 0 && index + delta < paths.Length) Reveal(paths[index + delta]);
    }
    private void NextUnviewed()
    {
        var paths = files.Keys.ToArray();
        var index = Array.IndexOf(paths, state.ActivePath);
        var next = paths.Skip(index + 1).Concat(paths.Take(index + 1)).FirstOrDefault(path => !state.Viewed.Contains(path));
        if (next is not null) Reveal(next);
    }
    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !ownsKeyboard() || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox or SharpRail.Scintilla.ScintillaEditor) return;
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Down or Key.Up) Step(e.Key == Key.Down ? 1 : -1);
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.J) NextUnviewed();
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.V && state.ActivePath is { } path)
        {
            if (!state.Viewed.Remove(path)) { state.Viewed.Add(path); if (single()) Step(1); }
            ProgressChanged();
        }
        else return;
        e.Handled = true;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; reading?.Cancel(); reading?.Dispose(); reading = null; ClearSections();
    }
}