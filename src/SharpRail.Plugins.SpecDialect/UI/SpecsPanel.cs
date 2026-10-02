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
    private readonly TreeView tree = new() { Name = "SpecsTree", Background = Ui.Sidebar, Margin = new Thickness(4) };
    private readonly Border error;
    private readonly TextBlock errorText = Ui.Text("", Ui.TextBrush, 12);
    private readonly Dictionary<string, bool> expanded = new(StringComparer.Ordinal);
    private readonly List<Action<string?>> selectionRows = [];
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
        tree.Items.Clear();
        selectionRows.Clear();
        if (nodes is null)
        {
            if (!failed)
                for (var index = 0; index < 6; index++)
                    tree.Items.Add(new Border { Name = "SpecSkeleton" + index, Height = 16, Margin = new Thickness(4, 4), Background = Ui.Hover, CornerRadius = new CornerRadius(4) });
            return;
        }
        foreach (var root in SpecTree.Build(nodes)) tree.Items.Add(Row(root, 0));
        if (nodes.Count == 0 && !failed) tree.Items.Add(Ui.Text("No specs", Ui.Muted, 12));
        Select(ActivePath(context.Host()));
    }

    private string? ActivePath(PluginHostProjection host) => host.ActiveEditor is { Kind: EditorKind.File } editor && editor.WorkspaceId == workspace ? editor.Path : null;
    private void Select(string? path)
    {
        foreach (var update in selectionRows) update(path);
    }

    private TreeViewItem Row(SpecTreeNode tree, int depth)
    {
        var spec = tree.Node;
        var main = depth == 0 && spec.Type == "goal-and-requirements";
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("14,4,*,Auto"), Background = Brushes.Transparent };
        var icon = new ContentControl();
        Ui.Place(header, icon);
        var title = Ui.Text(SpecTree.DisplayTitle(spec.Title));
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        Ui.Place(header, title, 0, 2);
        var role = Ui.Text(main ? "Main spec" : SpecTree.RoleTag(spec.Type), main ? Ui.Accent : Ui.Hint, 12);
        role.Name = "SpecRole"; role.IsVisible = false; role.Margin = new Thickness(8, 0, 0, 0);
        Ui.Place(header, role, 0, 3);
        var frame = new Border { Child = header, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Padding = new Thickness(4, 0) };
        var node = new TreeViewItem
        {
            Header = frame,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsExpanded = expanded.GetValueOrDefault(spec.Id, true),
            Tag = spec.Path
        };
        selectionRows.Add(path =>
        {
            var active = path == spec.Path;
            node.IsSelected = active;
            frame.Background = active ? Ui.PrimarySubtle : Brushes.Transparent;
            frame.BorderBrush = active ? Ui.PrimaryMuted : Brushes.Transparent;
            title.Foreground = active ? Ui.TextBrush : Ui.Muted;
            role.Foreground = main || active ? Ui.Accent : Ui.Hint;
            icon.Content = Ui.Icon(Icon(spec.Type, main || active), main || active ? Ui.Accent : Ui.Muted, 14);
        });
        node.PropertyChanged += (_, e) =>
        {
            if (e.Property == TreeViewItem.IsExpandedProperty) expanded[spec.Id] = node.IsExpanded;
        };
        // The row's own focus, not focus within: a tree item contains its children, and a focused child must not
        // show every ancestor's tag the way the fork's sibling rows never do.
        void UpdateRole() => role.IsVisible = header.IsPointerOver || node.IsFocused;
        header.PointerEntered += (_, _) => role.IsVisible = true;
        header.PointerExited += (_, _) => role.IsVisible = node.IsFocused;
        node.GotFocus += (_, _) => UpdateRole();
        node.LostFocus += (_, _) => UpdateRole();
        AutomationProperties.SetName(node, spec.Title);
        AutomationProperties.SetHelpText(node, spec.Path + " · " + role.Text);
        ToolTip.SetTip(node, spec.Title + "\n" + spec.Id + " · " + spec.Type);
        node.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.Source is not Control source || !IsOwnHeader(node, source) ||
                !e.GetCurrentPoint(node).Properties.IsLeftButtonPressed || source is ToggleButton ||
                source.GetVisualAncestors().OfType<ToggleButton>().Any()) return;
            _ = context.Editors.OpenAsync(workspace, spec.Path, new() { Preview = e.ClickCount < 2 });
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        node.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _ = context.Editors.OpenAsync(workspace, spec.Path);
            e.Handled = true;
        };
        foreach (var child in tree.Children) node.Items.Add(Row(child, depth + 1));
        return node;
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

    private static bool IsOwnHeader(TreeViewItem node, Control source) =>
        ReferenceEquals(source is TreeViewItem ? source : source.GetVisualAncestors().OfType<TreeViewItem>().FirstOrDefault(), node);
}