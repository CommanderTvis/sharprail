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
    private sealed class Section(GitChange change, Border frame, Grid container, Grid header, Grid body, TextBlock progress, CheckBox viewed)
    {
        internal GitChange Change = change;
        internal readonly Border Frame = frame;
        internal readonly Grid Container = container;
        internal readonly Grid Header = header;
        internal readonly Grid Body = body;
        internal readonly TextBlock Progress = progress;
        internal readonly CheckBox Viewed = viewed;
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
        var rows = new StackPanel { Classes = { "review-sections" } };
        foreach (var file in files.Values.Where(file => !single() || file.Path == state.ActivePath))
        {
            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), MinHeight = 32, Classes = { "review-header" } };
            var title = Ui.Button(file.Path + $"   +{file.Added} −{file.Removed}", () =>
            {
                state.ActivePath = file.Path;
                state.Collapsed[file.Path] = !IsCollapsed(file);
                Build(); changed();
            });
            title.Name = "ReviewFile"; title.Tag = file.Path;
            title.Classes.Add("review-file");
            title.ClearValue(PaddingProperty);
            title.ClearValue(BorderThicknessProperty);
            title.Content = new TextBlock { Text = file.Path + $"   +{file.Added} −{file.Removed}", TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, FontSize = 12 };
            title.ClipToBounds = true;
            ToolTip.SetTip(title, file.Path);
            title.HorizontalAlignment = HorizontalAlignment.Stretch;
            title.HorizontalContentAlignment = HorizontalAlignment.Left;
            var kept = Ui.Text("", Ui.Hint, 12); kept.Tag = file.Path;
            var open = new Button { Name = "ReviewOpenFile", Content = "Open as tab" };
            open.Classes.Add("review-action");
            open.Click += (_, _) => openFile(file);
            var viewed = new CheckBox { Name = "ReviewViewed", Tag = file.Path, Content = "Viewed", IsChecked = state.Viewed.Contains(file.Path), Margin = new Thickness(8, 0), FontSize = 12, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(viewed, "Viewed " + file.Path);
            viewed.IsCheckedChanged += (_, _) =>
            {
                if (updating) return;
                if (viewed.IsChecked == true) state.Viewed.Add(file.Path); else state.Viewed.Remove(file.Path);
                ProgressChanged();
            };
            Ui.Place(header, title); Ui.Place(header, kept, 0, 1); Ui.Place(header, open, 0, 2); Ui.Place(header, viewed, 0, 3);
            var content = new Grid();
            var container = new Grid { RowDefinitions = new RowDefinitions("32,*") };
            Ui.Place(container, header); Ui.Place(container, content, 1);
            var frame = new Border { Name = "ReviewSection", Tag = file.Path, Child = container, BorderBrush = Ui.BorderBrush, Classes = { "review-section" } };
            var section = new Section(file, frame, container, header, content, kept, viewed);
            if (single()) frame.VerticalAlignment = VerticalAlignment.Stretch;
            else if (!IsCollapsed(file)) content.MinHeight = Math.Min(20000, 232 + 40 * (file.Added + file.Removed));
            sections[file.Path] = section;
            if (!single()) rows.Children.Add(frame);
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
            scroll.ScrollChanged += (_, _) => { state.Offset = scroll.Offset.Y; QueueVisible(); };
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
            var visible = !IsCollapsed(section.Change) && (scroll is null ||
                top < scroll.Bounds.Height * 2 && top + section.Frame.Bounds.Height > -scroll.Bounds.Height);
            if (visible && (section.View is null || section.Stale) && !section.Loading && !section.Failed) _ = LoadAsync(section, generation, reading.Token);
            else if (!visible && section.View is not null) Unmount(section);
        }
        Pin(pin);
        PlaceReveal();
        if (!revealing && anchored is null && active is not null && active != state.ActivePath) { state.ActivePath = active; changed(); }
    }

    private async Task LoadAsync(Section section, long stamp, CancellationToken token)
    {
        section.Loading = true;
        var retained = section.View;
        if (retained is null)
        {
            section.Body.Children.Clear();
            section.Body.Children.Add(Ui.Text("Loading diff…", Ui.Hint));
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
            if (disposed || token.IsCancellationRequested || stamp != generation || !ReferenceEquals(sections.GetValueOrDefault(section.Change.Path), section))
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
                section.Body.MinHeight = 0;
                section.Body.Children.Clear(); section.Body.Children.Add(view);
            }
            QueueVisible();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (disposed || token.IsCancellationRequested || stamp != generation) return;
            section.Failed = true;
            section.View?.Dispose(); section.View = null;
            section.Body.MinHeight = 80;
            var notice = new StackPanel { Spacing = 4, Margin = new Thickness(12) };
            notice.Children.Add(Ui.Text("Could not read diff: " + error.Message, Ui.Danger));
            notice.Children.Add(Ui.Button("Retry", () => { section.Failed = false; _ = LoadAsync(section, generation, reading!.Token); }));
            section.Body.Children.Clear(); section.Body.Children.Add(notice);
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
        section.Height = section.Body.Bounds.Height;
        section.View?.Dispose(); section.View = null; section.Body.Children.Clear();
        section.Body.MinHeight = IsCollapsed(section.Change) ? 0 : section.Height;
    }
    private void ClearSections()
    {
        Pin(null);
        foreach (var section in sections.Values) section.View?.Dispose();
        sections.Clear();
    }

    private void Pin(Section? section)
    {
        if (ReferenceEquals(section, pinned)) return;
        if (pinned is not null)
        {
            sticky.Children.Remove(pinned.Header);
            pinned.Container.Children.Add(pinned.Header);
        }
        pinned = section;
        if (section is not null)
        {
            section.Container.Children.Remove(section.Header);
            sticky.Children.Add(section.Header);
        }
        sticky.IsVisible = section is not null;
    }

    internal void Reveal(string path)
    {
        reveal = anchored = path;
        if (!files.ContainsKey(path)) return;
        state.ActivePath = path; state.Collapsed[path] = false;
        if (single() || !sections.TryGetValue(path, out var section) || section.Body.MinHeight == 0 && section.View is null) Build();
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
        target.Header.GetLogicalDescendants().OfType<Button>().FirstOrDefault()?.Focus();
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
                section.Viewed.IsChecked = state.Viewed.Contains(section.Change.Path);
                var keys = state.Keys.GetValueOrDefault(section.Change.Path) ?? [];
                section.Progress.Text = mutable && keys.Count > 0 ? $"{keys.Count(key => state.IsKept(section.Change.Path, key))}/{keys.Count} kept" : "";
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