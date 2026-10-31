using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private readonly IPluginUIContext context;
    private readonly StackPanel rows = new();
    private readonly TextBlock message = Ui.Text("Reading processes…", Ui.Muted);
    private readonly TextBlock total = Ui.Text("", Ui.TextBrush);
    private readonly Button stopSelected, stopGroup;
    private readonly DispatcherTimer timer = new() { Interval = Interval };
    private readonly HashSet<string> collapsed = [];
    private ProcessSnapshot? snapshot;
    private string? selected;
    private int generation;
    private bool reading;

    internal ProcessesPanel(IPluginUIContext context)
    {
        this.context = context;
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
        body.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = new StackPanel { Children = { message, rows } } });
        Grid.SetRow(footer, 1);
        body.Children.Add(footer);
        Content = body;
        timer.Tick += (_, _) => Poll();
        AttachedToVisualTree += (_, _) => { timer.Start(); Poll(); };
        DetachedFromVisualTree += (_, _) => { timer.Stop(); generation++; reading = false; };
        Buttons();
    }

    // A hidden pane (a background tab or a collapsed rail) asks for nothing.
    private void Poll()
    {
        if (reading || !IsEffectivelyVisible) return;
        Read();
    }

    private async void Read()
    {
        var ticket = ++generation;
        reading = true;
        ProcessSnapshot next;
        try { next = await context.RequestAsync(ProcessesContract.Snapshot, new ProcessSnapshotParams()); }
        catch (PluginCallException error)
        {
            if (ticket != generation) return;
            reading = false;
            message.Text = "Could not read the processes.";
            message.IsVisible = true;
            context.Log.Warn(error.Message);
            return;
        }
        if (ticket != generation) return;
        reading = false;
        snapshot = next;
        Render();
    }

    private async void Stop(string? id)
    {
        if (id is null) return;
        try { await context.RequestAsync(ProcessesContract.Stop, new ProcessStopParams(id)); }
        catch (PluginCallException error) { context.Log.Warn(error.Message); }
        if (IsEffectivelyVisible) Read();
    }

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
        rows.Children.Clear();
        if (snapshot is null) return;
        if (selected is not null && Find(snapshot.Groups, selected) is null) selected = null;
        foreach (var group in snapshot.Groups) Add(group, 0);
        total.Text = $"Total {Cpu(snapshot.TotalCpu)} · {Memory(snapshot.TotalMemoryBytes)}";
        Buttons();
    }

    private void Add(ProcessNode node, int depth)
    {
        var grid = new Grid { ColumnDefinitions = new("*,52,68,56"), Margin = new Thickness(8 + depth * 14, 2, 8, 2) };
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var open = node.Children.Count > 0 && !collapsed.Contains(node.Id);
        label.Children.Add(node.Children.Count > 0 ? Ui.Icon(open ? "arrowDown" : "arrowRight", Ui.Muted, 12) : new Border { Width = 12 });
        label.Children.Add(Ui.Text(node.Label, node.Kind == "group" ? Ui.TextBrush : Ui.TextBrush, node.Kind == "group" ? Ui.FontSize : Ui.FontSize - 1));
        grid.Children.Add(label);
        var cpu = Ui.Text(Cpu(node.Cpu), Ui.Muted); cpu.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(cpu, 1); grid.Children.Add(cpu);
        var memory = Ui.Text(Memory(node.MemoryBytes), Ui.Muted); memory.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(memory, 2); grid.Children.Add(memory);
        var spark = new Sparkline { HorizontalAlignment = HorizontalAlignment.Right };
        spark.Show(node.Trend);
        Grid.SetColumn(spark, 3);
        grid.Children.Add(spark);
        var row = new Border { Child = grid, Background = node.Id == selected ? Ui.Selected : Avalonia.Media.Brushes.Transparent };
        if (node.Detail.Length > 0) ToolTip.SetTip(row, node.Detail);
        row.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
            selected = node.Id;
            if (node.Children.Count > 0 && e.GetPosition(row).X < 8 + depth * 14 + 20 && !collapsed.Remove(node.Id)) collapsed.Add(node.Id);
            Render();
        };
        rows.Children.Add(row);
        if (open) foreach (var child in node.Children) Add(child, depth + 1);
    }

    internal static string Cpu(double percent) => percent.ToString("0", CultureInfo.InvariantCulture) + "%";

    internal static string Memory(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB"
    };
}