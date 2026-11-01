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
    private readonly StackPanel rows = new();
    private readonly ScrollViewer scroll;
    private readonly TextBlock message = Ui.Text("Reading processes…", Ui.Muted);
    private readonly TextBlock total = Ui.Text("", Ui.TextBrush);
    private readonly Button stopSelected, stopGroup;
    private readonly DispatcherTimer timer = new() { Interval = Interval };
    private readonly HashSet<string> collapsed = [];
    private readonly Dictionary<string, RowView> views = [];
    private ProcessSnapshot? snapshot;
    private string? selected;
    private int generation;
    private bool reading;

    /// <summary>One node's controls, kept for as long as the node stays in the tree; a refresh rewrites their text instead of replacing them.</summary>
    private sealed class RowView
    {
        internal readonly Border Row = new();
        internal readonly ContentControl Chevron = new() { Width = 12 };
        internal readonly TextBlock Label = Ui.Text("", Ui.TextBrush), Cpu = Ui.Text("", Ui.Muted), Memory = Ui.Text("", Ui.Muted);
        internal readonly Sparkline Spark = new() { HorizontalAlignment = HorizontalAlignment.Right };
        internal ProcessNode Node = null!;
        internal bool? Open;
        internal int Depth = -1;
    }

    internal ProcessesPanel(IPluginUIContext context)
        : this(() => context.RequestAsync(ProcessesContract.Snapshot, new ProcessSnapshotParams()).AsTask(),
            id => context.RequestAsync(ProcessesContract.Stop, new ProcessStopParams(id)).AsTask(), message => context.Log.Warn(message))
    { }

    /// <param name="read">Asks the host for a snapshot.</param>
    /// <param name="stop">Asks the host to stop a node.</param>
    /// <param name="warn">Reports a failed call.</param>
    /// <param name="visible">Whether the pane is on screen; the default is the control's own effective visibility.</param>
    internal ProcessesPanel(Func<Task<ProcessSnapshot>> read, Func<string, Task> stop, Action<string> warn, Func<bool>? visible = null)
    {
        this.read = read; this.stop = stop; this.warn = warn;
        this.visible = visible ?? (() => IsEffectivelyVisible);
        stopSelected = Ui.Button("Stop selected", () => Stop(selected));
        stopGroup = Ui.Button("Stop group", () => Stop(GroupOf(selected)));
        var footer = new DockPanel { Margin = new Thickness(8) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(stopSelected);
        actions.Children.Add(stopGroup);
        DockPanel.SetDock(actions, Dock.Right);
        footer.Children.Add(actions);
        footer.Children.Add(total);
        var body = new Grid { RowDefinitions = new("*,Auto") };
        scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = new StackPanel { Children = { message, rows } } };
        body.Children.Add(scroll);
        Grid.SetRow(footer, 1);
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

    private async void Stop(string? id)
    {
        if (id is null) return;
        try { await stop(id); }
        catch (PluginCallException error) { warn(error.Message); }
        if (visible()) Read();
    }

    /// <summary>Shows a snapshot: nothing at all when it equals the one shown, else the rows that changed are rewritten in place.</summary>
    internal void Apply(ProcessSnapshot next)
    {
        if (snapshot is { } shown && Same(shown, next)) return;
        snapshot = next;
        Render();
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
        message.IsVisible = snapshot is null;
        if (snapshot is null) return;
        if (selected is not null && Find(snapshot.Groups, selected) is null) selected = null;
        var wanted = new List<RowView>();
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in snapshot.Groups) Visit(group, 0, wanted, live);
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
        total.Text = $"Total {Cpu(snapshot.TotalCpu)} · {Memory(snapshot.TotalMemoryBytes)}";
        Buttons();
    }

    // A node is expanded unless the user collapsed it; the state is kept per node id, so a returning node keeps it.
    private void Visit(ProcessNode node, int depth, List<RowView> wanted, HashSet<string> live)
    {
        live.Add(node.Id);
        if (!views.TryGetValue(node.Id, out var view)) views[node.Id] = view = Create(node.Id);
        view.Node = node;
        var open = node.Children.Count > 0 && !collapsed.Contains(node.Id);
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

    private RowView Create(string id)
    {
        var view = new RowView();
        var grid = new Grid { ColumnDefinitions = new("*,52,68,56") };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { view.Chevron, view.Label } };
        grid.Children.Add(label);
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
        if (toggle && views.TryGetValue(id, out var view) && view.Node.Children.Count > 0 && !collapsed.Remove(id)) collapsed.Add(id);
        if (snapshot is null) return;
        Render();
    }

    internal static string Cpu(double percent) => percent.ToString("0", CultureInfo.InvariantCulture) + "%";

    internal static string Memory(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB"
    };
}