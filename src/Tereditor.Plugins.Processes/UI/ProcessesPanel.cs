using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Processes.UI;

/// <summary>
/// The Processes side tool: the host's process tree with CPU, memory and a trend per row, a total, and Stop selected / Stop group. It asks the host
/// for a snapshot every <see cref="Interval"/> while it is attached and visible, and for nothing otherwise, so a hidden pane costs no sampling.
/// </summary>
internal sealed class ProcessesPanel : UserControl
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private const double Indent = 14;
    private readonly Func<Task<ProcessSnapshot>> read;
    private readonly Func<string, Task> stop;
    private readonly Action<string> warn;
    private readonly Func<bool> visible;
    private readonly Func<IReadOnlySet<string>?> scope;
    private readonly ProcessesView view;
    private readonly CheckBox allProjects = new() { Content = "All projects" };
    private bool syncing;
    private readonly StackPanel rows = new();
    private readonly ScrollViewer scroll;
    private readonly TextBlock message = Ui.Text("Reading processes…", Ui.Muted);
    private readonly TextBlock total = Ui.Text("", Ui.TextBrush);
    private readonly Button stopSelected, stopGroup;
    private readonly DispatcherTimer timer = new() { Interval = Interval };
    // The trend column goes when the whole pane is narrow, for every row at once: a row narrower only by its indent would shift its numbers off the columns.
    private const double NarrowWidth = 320;
    private bool narrow;
    // Rows that fit the pane is its height over this; a row is a line of text plus its 2 px margins.
    private const double RowHeight = 22;
    // What the user chose per node id (true = open); wins over the default and survives refreshes.
    private readonly Dictionary<string, bool> chosen = new(StringComparer.Ordinal);
    private Dictionary<string, bool> opened = new(StringComparer.Ordinal);
    private bool reevaluate;
    private readonly Dictionary<string, RowView> views = [];
    private ProcessSnapshot? full, snapshot;
    private string? selected;
    private int generation;
    private bool reading;

    /// <summary>One node's controls, kept for as long as the node stays in the tree; a refresh rewrites their text instead of replacing them.</summary>
    private sealed class RowView
    {
        internal readonly Border Row = new();
        internal readonly ContentControl Chevron = new() { Width = 12 };
        internal readonly TextBlock Label = Ui.Text("", Ui.TextBrush), Cpu = Ui.Text("", Ui.Muted), Memory = Ui.Text("", Ui.Muted);
        // The trend keeps a gap from the memory figure, which is right-aligned against its column.
        internal readonly Sparkline Spark = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8, 0, 0, 0) };
        internal Grid Grid = null!;
        internal ProcessNode Node = null!;
        internal bool? Open;
        internal int Depth = -1;
    }

    internal ProcessesPanel(IPluginUIContext context, string workspace, ProcessesView view)
        : this(() => context.RequestAsync(ProcessesContract.Snapshot, new ProcessSnapshotParams()).AsTask(),
            id => context.RequestAsync(ProcessesContract.Stop, new ProcessStopParams(id)).AsTask(), message => context.Log.Warn(message),
            scope: () => ProjectWorkspaces(context.Host(), workspace), view: view)
    { }

    // The ids of every workspace of the project the pane's workspace belongs to, from the in-memory host projection.
    private static IReadOnlySet<string> ProjectWorkspaces(PluginHostProjection host, string workspace)
    {
        var all = host.Workspaces.Values.SelectMany(items => items).ToArray();
        var project = all.FirstOrDefault(item => item.Id == workspace)?.ProjectId;
        return all.Where(item => item.ProjectId == project).Select(item => item.Id).Append(workspace).ToHashSet(StringComparer.Ordinal);
    }

    /// <param name="read">Asks the host for a snapshot.</param>
    /// <param name="stop">Asks the host to stop a node.</param>
    /// <param name="warn">Reports a failed call.</param>
    /// <param name="visible">Whether the pane is on screen; the default is the control's own effective visibility.</param>
    /// <param name="scope">The workspace ids of the pane's own project; null shows every row.</param>
    /// <param name="view">The shared view choice (all projects or the own one); a fresh one by default.</param>
    internal ProcessesPanel(Func<Task<ProcessSnapshot>> read, Func<string, Task> stop, Action<string> warn, Func<bool>? visible = null,
        Func<IReadOnlySet<string>?>? scope = null, ProcessesView? view = null)
    {
        this.read = read; this.stop = stop; this.warn = warn;
        this.scope = scope ?? (() => null);
        this.view = view ?? new ProcessesView();
        allProjects.IsChecked = this.view.AllProjects;
        allProjects.IsVisible = scope is not null;
        allProjects.Margin = new Thickness(8, 4, 8, 0);
        allProjects.IsCheckedChanged += (_, _) =>
        {
            if (syncing) return;
            this.view.AllProjects = allProjects.IsChecked == true;
            this.view.Persist?.Invoke(this.view.AllProjects);
            Refilter();
        };
        this.visible = visible ?? (() => IsEffectivelyVisible);
        stopSelected = Ui.Button("Stop selected", () => Stop(selected));
        stopGroup = Ui.Button("Stop group", () => Stop(GroupOf(selected)));
        // The total reads in full on its own line (wrapping, never cut); the buttons sit under it and wrap when the pane is narrow.
        total.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        total.TextTrimming = Avalonia.Media.TextTrimming.None;
        var footer = new StackPanel { Margin = new Thickness(8), Spacing = 8 };
        var actions = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        actions.Children.Add(stopSelected);
        actions.Children.Add(stopGroup);
        footer.Children.Add(total);
        footer.Children.Add(actions);
        var body = new Grid { RowDefinitions = new("Auto,*,Auto") };
        scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = new StackPanel { Children = { message, rows } } };
        Grid.SetRow(scroll, 1);
        scroll.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged is false || snapshot is null) return;
            reevaluate = true;
            Render();
        };
        body.Children.Add(allProjects);
        body.Children.Add(scroll);
        Grid.SetRow(footer, 2);
        body.Children.Add(footer);
        Content = body;
        timer.Tick += (_, _) => Poll();
        AttachedToVisualTree += (_, _) => { timer.Start(); Poll(); };
        DetachedFromVisualTree += (_, _) => { timer.Stop(); generation++; reading = false; };
        Buttons();
    }

    /// <summary>How many snapshots were requested; a hidden pane must not raise it.</summary>
    internal int Requests { get; private set; }
    internal ScrollViewer Scroll => scroll;
    internal string? Selected => selected;
    internal ProcessSnapshot? Snapshot => snapshot;
    internal Control? RowOf(string id) => views.GetValueOrDefault(id)?.Row;
    internal int RowCount => rows.Children.Count;

    // A hidden pane (a background tab or a collapsed rail) asks for nothing.
    internal void Poll()
    {
        if (reading || !visible()) return;
        Read();
    }

    private async void Read()
    {
        var ticket = ++generation;
        reading = true;
        Requests++;
        ProcessSnapshot next;
        try { next = await read(); }
        catch (PluginCallException error)
        {
            if (ticket != generation) return;
            reading = false;
            message.Text = "Could not read the processes.";
            message.IsVisible = true;
            warn(error.Message);
            return;
        }
        if (ticket != generation) return;
        reading = false;
        Apply(next);
    }

    // A row, workspace or parent node stays inside one workspace, so the host's stop on its id touches only that project; a group spans
    // projects, so a scoped group is stopped through the visible rows below it.
    private async void Stop(string? id)
    {
        if (id is null || snapshot is null) return;
        var node = Find(snapshot.Groups, id);
        if (node is null) return;
        try { foreach (var target in StopTargets(node)) await stop(target); }
        catch (PluginCallException error) { warn(error.Message); }
        if (visible()) Read();
    }

    internal IReadOnlyList<string> StopTargets(ProcessNode node) =>
        node.Workspace is not null || !Scoped ? [node.Id] : [.. node.Children.Where(child => child.CanStop).SelectMany(StopTargets)];

    /// <summary>Whether the view is limited to the pane's own project.</summary>
    internal bool Scoped => !view.AllProjects && scope() is not null;

    /// <summary>Shows a snapshot: nothing at all when what it shows equals the one shown, else the rows that changed are rewritten in place.</summary>
    internal void Apply(ProcessSnapshot next)
    {
        full = next;
        Refilter();
    }

    private void Refilter()
    {
        if (full is null) return;
        var next = Scoped ? Filter(full, scope()!) : full;
        syncing = true; allProjects.IsChecked = view.AllProjects; syncing = false;
        if (snapshot is { } shown && Same(shown, next) && scopedShown == Scoped) return;
        snapshot = next;
        scopedShown = Scoped;
        Render();
    }

    private bool scopedShown;

    private static double At(ProcessNode node, int index) => index >= 0 ? node.Trend[index] : 0;

    /// <summary>The snapshot limited to rows of <paramref name="workspaces"/>; the group sums and the total are those of what remains.</summary>
    internal static ProcessSnapshot Filter(ProcessSnapshot source, IReadOnlySet<string> workspaces)
    {
        var groups = source.Groups.Select(group =>
        {
            var kept = group.Children.Where(child => child.Workspace is null || workspaces.Contains(child.Workspace)).ToArray();
            var length = kept.Length == 0 ? 0 : kept.Max(child => child.Trend.Count);
            var trend = Enumerable.Range(0, length).Select(index => Math.Round(kept.Sum(child => At(child, child.Trend.Count - length + index)), 1)).ToArray();
            return group with { Children = kept, Cpu = Math.Round(kept.Sum(child => child.Cpu), 1), MemoryBytes = kept.Sum(child => child.MemoryBytes), Trend = trend, CanStop = kept.Any(child => child.CanStop) };
        }).ToArray();
        return source with { Groups = groups, TotalCpu = Math.Round(groups.Sum(group => group.Cpu), 1), TotalMemoryBytes = groups.Sum(group => group.MemoryBytes) };
    }

    private static bool Same(ProcessSnapshot a, ProcessSnapshot b) =>
        a.TotalCpu == b.TotalCpu && a.TotalMemoryBytes == b.TotalMemoryBytes && SameNodes(a.Groups, b.Groups);

    private static bool SameNodes(IReadOnlyList<ProcessNode> a, IReadOnlyList<ProcessNode> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => pair.First.Id == pair.Second.Id && pair.First.Kind == pair.Second.Kind && pair.First.Label == pair.Second.Label &&
            pair.First.Detail == pair.Second.Detail && pair.First.Pid == pair.Second.Pid && pair.First.Cpu == pair.Second.Cpu && pair.First.MemoryBytes == pair.Second.MemoryBytes &&
            pair.First.CanStop == pair.Second.CanStop && pair.First.Trend.SequenceEqual(pair.Second.Trend) && SameNodes(pair.First.Children, pair.Second.Children));

    private string? GroupOf(string? id) => id is null || snapshot is null ? null : snapshot.Groups.FirstOrDefault(group => Contains(group, id))?.Id;

    private static bool Contains(ProcessNode node, string id) => node.Id == id || node.Children.Any(child => Contains(child, id));

    private static ProcessNode? Find(IEnumerable<ProcessNode> nodes, string id) =>
        nodes.Select(node => node.Id == id ? node : Find(node.Children, id)).FirstOrDefault(found => found is not null);

    private void Buttons()
    {
        var node = selected is null || snapshot is null ? null : Find(snapshot.Groups, selected);
        stopSelected.IsEnabled = node?.CanStop == true;
        stopGroup.IsEnabled = GroupOf(selected) is { } group && Find(snapshot!.Groups, group)?.CanStop == true;
    }

    private void Render()
    {
        if (snapshot is null) { message.IsVisible = true; return; }
        var empty = snapshot.Groups.All(group => group.Children.Count == 0);
        message.Text = Scoped && empty ? "Nothing running for this project" : "Reading processes…";
        message.IsVisible = Scoped && empty;
        if (selected is not null && Find(snapshot.Groups, selected) is null) selected = null;
        var wanted = new List<RowView>();
        var live = new HashSet<string>(StringComparer.Ordinal);
        var capacity = scroll.Bounds.Height > 0 ? Math.Max(1, (int)(scroll.Bounds.Height / RowHeight)) : int.MaxValue;
        opened = Expansion(snapshot.Groups, capacity, chosen, opened, reevaluate);
        reevaluate = false;
        if (!(Scoped && empty)) foreach (var group in snapshot.Groups) Visit(group, 0, wanted, live);
        foreach (var id in views.Keys.Where(id => !live.Contains(id)).ToArray()) views.Remove(id);
        // Only rows that appeared, vanished or moved touch the panel; the others keep their place, focus and pointer capture.
        for (var index = 0; index < wanted.Count; index++)
        {
            var row = wanted[index].Row;
            if (index < rows.Children.Count && ReferenceEquals(rows.Children[index], row)) continue;
            rows.Children.Remove(row);
            rows.Children.Insert(index, row);
        }
        while (rows.Children.Count > wanted.Count) rows.Children.RemoveAt(rows.Children.Count - 1);
        total.Text = $"Total{(scope() is null ? "" : Scoped ? " (this project)" : " (all projects)")} {Cpu(snapshot.TotalCpu)} · {Memory(snapshot.TotalMemoryBytes)}";
        Buttons();
    }

    /// <summary>
    /// Which nodes with children are open. Walking from the top in order, a node opens while the visible rows, counting its children, stay within
    /// <paramref name="capacity"/>; otherwise it starts collapsed. A user's choice wins. A default, once set, is kept for the node unless
    /// <paramref name="reevaluate"/>, so a refresh never flips a node the user did not touch.
    /// </summary>
    internal static Dictionary<string, bool> Expansion(IReadOnlyList<ProcessNode> roots, int capacity, IReadOnlyDictionary<string, bool> chosen,
        IReadOnlyDictionary<string, bool> previous, bool reevaluate)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        var rows = roots.Count;
        void Walk(ProcessNode node)
        {
            if (node.Children.Count == 0) return;
            bool open;
            if (chosen.TryGetValue(node.Id, out var own)) open = own;
            else if (!reevaluate && previous.TryGetValue(node.Id, out var before)) open = before;
            else open = rows + node.Children.Count <= capacity;
            result[node.Id] = open;
            if (!open) return;
            rows += node.Children.Count;
            foreach (var child in node.Children) Walk(child);
        }
        foreach (var root in roots) Walk(root);
        return result;
    }

    // A node's openness is the user's choice, else its default; the state is kept per node id, so a returning node keeps the choice.
    private void Visit(ProcessNode node, int depth, List<RowView> wanted, HashSet<string> live)
    {
        live.Add(node.Id);
        if (!views.TryGetValue(node.Id, out var view)) views[node.Id] = view = Create(node.Id);
        view.Node = node;
        var open = node.Children.Count > 0 && opened.GetValueOrDefault(node.Id);
        if (view.Open != open || view.Depth != depth || view.Chevron.Content is null)
        {
            view.Chevron.Content = node.Children.Count > 0 ? Ui.Icon(open ? "arrowDown" : "arrowRight", Ui.Muted, 12) : null;
            view.Row.Margin = new Thickness(8 + depth * Indent, 2, 8, 2);
            view.Open = open;
            view.Depth = depth;
        }
        view.Label.Text = node.Label;
        view.Label.FontSize = node.Kind == "group" ? Ui.FontSize : Ui.FontSize - 1;
        view.Cpu.Text = Cpu(node.Cpu);
        view.Memory.Text = Memory(node.MemoryBytes);
        view.Spark.Show(node.Trend);
        ToolTip.SetTip(view.Row, node.Detail.Length > 0 ? node.Detail : null);
        view.Row.Background = node.Id == selected ? Ui.Selected : Avalonia.Media.Brushes.Transparent;
        wanted.Add(view);
        if (open) foreach (var child in node.Children) Visit(child, depth + 1, wanted, live);
    }

    private void ApplyNarrow(RowView view)
    {
        view.Spark.IsVisible = !narrow;
        view.Grid.ColumnDefinitions[3].Width = narrow ? new GridLength(0) : new GridLength(64);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var next = e.NewSize.Width < NarrowWidth;
        if (next == narrow) return;
        narrow = next;
        foreach (var view in views.Values) ApplyNarrow(view);
    }

    private RowView Create(string id)
    {
        var view = new RowView();
        var grid = new Grid { ColumnDefinitions = new("*,52,68,64") };
        // The name takes what the numbers leave and is cut with an ellipsis; below NarrowWidth the trend column gives its space to the name.
        view.Label.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        view.Chevron.Margin = new Thickness(0, 0, 4, 0);
        DockPanel.SetDock(view.Chevron, Dock.Left);
        var label = new DockPanel { ClipToBounds = true, Children = { view.Chevron, view.Label } };
        grid.Children.Add(label);
        view.Grid = grid;
        ApplyNarrow(view);
        foreach (var (control, column) in new (Control, int)[] { (view.Cpu, 1), (view.Memory, 2), (view.Spark, 3) })
        {
            if (control is TextBlock text) text.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(control, column);
            grid.Children.Add(control);
        }
        view.Row.Child = grid;
        view.Row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(view.Row).Properties.IsLeftButtonPressed) return;
            Choose(id, e.GetPosition(view.Row).X < view.Row.Margin.Left + 20);
        };
        return view;
    }

    /// <summary>Selects a node; with <paramref name="toggle"/> also flips its expansion. Only the affected rows are rewritten.</summary>
    internal void Choose(string id, bool toggle)
    {
        selected = id;
        if (toggle && views.TryGetValue(id, out var view) && view.Node.Children.Count > 0) chosen[id] = !opened.GetValueOrDefault(id);
        if (snapshot is null) return;
        Render();
    }

    internal static string Cpu(double percent) => percent.ToString("0", CultureInfo.InvariantCulture) + "%";

    internal static string Memory(long bytes) => bytes switch
    {
        >= 1L << 30 => Units.Pair((bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture), "GB"),
        >= 1L << 20 => Units.Pair((bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture), "MB"),
        _ => Units.Pair((bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture), "KB")
    };
}