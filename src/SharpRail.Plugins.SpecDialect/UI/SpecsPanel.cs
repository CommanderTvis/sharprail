using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.SpecDialect;

/// <summary>
/// The Specs side tool: a read-only parent tree over one workspace's graph, read from the store rather than fetched
/// here, so it refreshes in place when the graph changes and keeps what the user expanded. A failed read shows an
/// inline error with Retry above whatever tree it already had; an empty graph says so.
/// </summary>
internal sealed class SpecsPanel : DockPanel
{
    private readonly IPluginUIContext context;
    private readonly SpecSync sync;
    private readonly string workspace;
    private readonly DrawnTree tree = new() { Name = "SpecsTree", SpecColors = true, Background = Ui.Sidebar, Margin = new Thickness(4) };
    private readonly Border error;
    private readonly TextBlock errorText = Ui.Text("", Ui.TextBrush, 12);
    private readonly Dictionary<string, bool> expanded = new(StringComparer.Ordinal);
    private IDisposable? editorWatch;
    private IReadOnlyList<SpecGraphNode>? shown;
    private bool shownFailed;
    private bool rendered;

    public SpecsPanel(IPluginUIContext context, SpecSync sync, string workspace)
    {
        this.context = context; this.sync = sync; this.workspace = workspace;
        Name = "SpecsPanel";
        ScrollViewer.SetHorizontalScrollBarVisibility(tree, ScrollBarVisibility.Disabled);
        errorText.TextWrapping = TextWrapping.Wrap;
        errorText.VerticalAlignment = VerticalAlignment.Center;
        var retry = Ui.Button("Retry", () => _ = sync.LoadAsync(workspace), "refresh");
        retry.Name = "SpecsRetry";
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Ui.Place(row, errorText);
        Ui.Place(row, retry, 0, 1);
        error = new Border
        {
            Name = "SpecsError",
            Child = row,
            Margin = new Thickness(12, 12, 12, 0),
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Ui.Danger,
            Background = Ui.DangerWash,
            IsVisible = false
        };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        SetDock(error, Dock.Top);
        Children.Add(error);
        Children.Add(tree);
        Render();
        AttachedToVisualTree += (_, _) =>
        {
            sync.Store.Changed += StoreChanged;
            editorWatch = context.WatchHost(host => ActivePath(host), (next, _) => Select(next));
            Render();
        };
        DetachedFromVisualTree += (_, _) => { sync.Store.Changed -= StoreChanged; editorWatch?.Dispose(); editorWatch = null; };
    }

    private void StoreChanged(string changed)
    {
        if (changed == workspace) Render();
    }

    private void Render()
    {
        var nodes = sync.Store.Specs(workspace);
        var failed = sync.Store.Failed(workspace);
        error.IsVisible = failed;
        errorText.Text = nodes is null ? "Couldn't load specs." : "Couldn't update specs.";
        if (rendered && ReferenceEquals(nodes, shown) && failed == shownFailed) return;
        rendered = true;
        shown = nodes;
        shownFailed = failed;
        if (nodes is null)
        {
            tree.SetRoots([]);
            if (!failed) tree.SetRoots(Enumerable.Range(0, 6).Select(index => new DrawnTreeEntry("skeleton-" + index, "") { Skeleton = true }).ToArray());
            return;
        }
        // Rows are matched by path: a re-read updates the kept ones and creates or drops only what changed, so expansion,
        // selection, focus and scroll stay. Skeleton and empty placeholders never match a path.
        var previous = new Dictionary<string, DrawnTreeEntry>(StringComparer.Ordinal);
        void Collect(IEnumerable<DrawnTreeEntry> entries)
        {
            foreach (var entry in entries) { previous.TryAdd(entry.Id, entry); Collect(entry.Children); }
        }
        Collect(tree.Roots);
        tree.SetRoots(nodes.Count == 0 && !failed ? [new DrawnTreeEntry("empty", "No specs") { Muted = true }]
            : SpecTree.Build(nodes).Select(root => Row(root, 0, previous)).ToArray());
        Select(ActivePath(context.Host()));
    }

    private string? ActivePath(PluginHostProjection host) => host.ActiveEditor is { Kind: EditorKind.File } editor && editor.WorkspaceId == workspace ? editor.Path : null;
    private void Select(string? path) => tree.SelectedItem = tree.Rows.FirstOrDefault(row => row.Id == path);

    private DrawnTreeEntry Row(SpecTreeNode branch, int depth, Dictionary<string, DrawnTreeEntry> previous)
    {
        var spec = branch.Node;
        var main = depth == 0 && spec.Type == "goal-and-requirements";
        var role = main ? "Main spec" : SpecTree.RoleTag(spec.Type);
        var reused = previous.Remove(spec.Path, out var entry) && entry.Tag is string;
        if (!reused) entry = new DrawnTreeEntry(spec.Path, "") { Expanded = expanded.GetValueOrDefault(spec.Id, true) };
        entry!.Text = SpecTree.DisplayTitle(spec.Title);
        entry.Tag = spec.Path;
        entry.Help = spec.Path + " · " + role;
        entry.Tip = spec.Title + "\n" + spec.Id + " · " + spec.Type;
        entry.Main = main;
        entry.Role = role;
        entry.Children = branch.Children.Select(child => Row(child, depth + 1, previous)).ToArray();
        entry.CanExpand = entry.Children.Count > 0;
        entry.IconKey = selected => (spec.Type, main || selected);
        entry.Icon = selected => Ui.Icon(Icon(spec.Type, main || selected), main || selected ? Ui.Accent : Ui.Muted, 14);
        entry.ExpansionChanged = value => { expanded[spec.Id] = value; Select(ActivePath(context.Host())); };
        entry.Activate = clicks => _ = context.Editors.OpenAsync(workspace, spec.Path, new() { Preview = clicks < 2 });
        return entry;
    }

    private static string Icon(string type, bool filled) => type switch
    {
        "goal-and-requirements" => filled ? "bookFill" : "book",
        "architecture-design" => filled ? "networkFill" : "network",
        "module-design" => filled ? "boxFill" : "box",
        "submodule-design" => filled ? "stackFill" : "stack",
        "task-spec" => "list",
        _ => filled ? "fileTextFill" : "fileText"
    };

}