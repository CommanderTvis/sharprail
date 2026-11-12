using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace SharpRail.Plugins.UI.Kit;

internal sealed class DrawnTreeEntry(string id, string text)
{
    internal string Id { get; } = id;
    internal string Text { get; set; } = text;
    internal object? Tag { get; set; }
    internal string? Help { get; set; }
    internal string? Role { get; set; }
    internal string? Tip { get; set; }
    internal bool Main { get; set; }
    internal bool Muted { get; set; }
    /// <summary>The title's colour when it carries a mark (a changed file or a folder containing changes); null keeps the default.</summary>
    internal Avalonia.Media.IBrush? Tint { get; set; }
    /// <summary>A short trailing letter drawn at the row's right edge in <see cref="Tint"/>, such as a Git status.</summary>
    internal string? Badge { get; set; }
    /// <summary>A small trailing dot in <see cref="Tint"/>, such as a folder that contains changes.</summary>
    internal bool Dot { get; set; }
    internal bool Italic { get; set; }
    internal bool Skeleton { get; set; }
    internal bool DropTarget { get; set; }
    internal bool CanExpand { get; set; }
    internal bool Expanded { get; set; }
    internal int Depth { get; set; }
    internal DrawnTreeEntry? Parent { get; set; }
    internal IReadOnlyList<DrawnTreeEntry> Children { get; set; } = [];
    internal Func<bool, Control>? Icon { get; set; }
    internal Func<bool, object>? IconKey { get; set; }
    internal Action<bool>? ExpansionChanged { get; set; }
    internal Action<int>? Activate { get; set; }
    internal Func<ContextMenu>? Menu { get; set; }
}

internal sealed class DrawnTree : ListBox
{
    private readonly AvaloniaList<DrawnTreeEntry> rows = [];
    internal IReadOnlyList<DrawnTreeEntry> Roots { get; private set; } = [];
    internal IReadOnlyList<DrawnTreeEntry> Rows => rows;
    internal bool SpecColors { get; init; }
    internal Func<DrawnTreeEntry, PointerPressedEventArgs, Task>? Drag { get; set; }

    internal DrawnTree()
    {
        Classes.Add("drawn-tree");
        ItemsSource = rows;
        SelectionChanged += (_, _) => RefreshRows();
        GotFocus += (_, _) => RefreshRows();
        LostFocus += (_, _) => RefreshRows();
        AddHandler(KeyDownEvent, TreeKeyDown, RoutingStrategies.Tunnel);
    }

    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new TreeItem(this);

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        if (container is TreeItem row) row.ContentTemplate = row.LabelTemplate;
        base.PrepareContainerForItemOverride(container, item, index);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TreePeer(this);

    private sealed class TreePeer(DrawnTree owner) : ListBoxAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tree;
    }

    private sealed class TreeItem : ListBoxItem
    {
        private readonly DrawnTree tree;
        private DrawnTreeLabel? label;
        internal IDataTemplate LabelTemplate { get; }

        internal TreeItem(DrawnTree tree)
        {
            this.tree = tree;
            LabelTemplate = new FuncDataTemplate<DrawnTreeEntry>((entry, _) => entry is null ? null : label ??= new DrawnTreeLabel(tree, entry), supportsRecycling: true);
        }

        protected override Type StyleKeyOverride => typeof(ListBoxItem);
        protected override AutomationPeer OnCreateAutomationPeer() => new ItemPeer(this, tree);
    }

    private sealed class ItemPeer(TreeItem owner, DrawnTree tree) : ListItemAutomationPeer(owner), IExpandCollapseProvider
    {
        private DrawnTreeEntry? Entry => owner.Content as DrawnTreeEntry;
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TreeItem;
        protected override string? GetNameCore() => Entry?.Text;
        protected override string? GetHelpTextCore() => Entry?.Help;
        public ExpandCollapseState ExpandCollapseState => Entry is not { CanExpand: true } ? ExpandCollapseState.LeafNode
            : Entry.Expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        public bool ShowsMenu => false;
        public void Expand() { if (Entry is { } entry) tree.Expand(entry, true); }
        public void Collapse() { if (Entry is { } entry) tree.Expand(entry, false); }
    }

    internal void SetRoots(IReadOnlyList<DrawnTreeEntry> roots)
    {
        Roots = roots;
        Rebuild();
    }

    internal void Expand(DrawnTreeEntry entry, bool expanded)
    {
        if (!entry.CanExpand || entry.Expanded == expanded) return;
        entry.Expanded = expanded;
        Rebuild();
        entry.ExpansionChanged?.Invoke(expanded);
    }

    private void Rebuild()
    {
        var desired = new List<DrawnTreeEntry>();
        void Visit(DrawnTreeEntry entry, DrawnTreeEntry? parent, int depth)
        {
            entry.Depth = depth; entry.Parent = parent;
            desired.Add(entry);
            if (entry.Expanded) foreach (var child in entry.Children) Visit(child, entry, depth + 1);
        }
        foreach (var root in Roots) Visit(root, null, 0);
        var selected = SelectedItem as DrawnTreeEntry;
        var focused = GetRealizedContainers().OfType<ListBoxItem>().FirstOrDefault(item => item.IsKeyboardFocusWithin)?.Content as DrawnTreeEntry;
        var changed = !rows.SequenceEqual(desired);
        var keep = desired.ToHashSet();
        for (var index = rows.Count - 1; index >= 0; index--)
            if (!keep.Contains(rows[index])) rows.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < rows.Count && ReferenceEquals(rows[index], desired[index])) continue;
            var old = rows.IndexOf(desired[index]);
            if (old < 0) rows.Insert(index, desired[index]);
            else rows.Move(old, index);
        }
        if (selected is not null && !keep.Contains(selected))
        {
            while (selected is not null && !keep.Contains(selected)) selected = selected.Parent;
            SelectedItem = selected;
        }
        RefreshRows();
        if (changed && focused is not null && keep.Contains(focused))
        {
            // Collection edits can recycle a focused container even while its entry remains visible.
            UpdateLayout();
            ContainerFromItem(focused)?.Focus();
        }
    }

    internal void RefreshRows(bool forceIcons = false)
    {
        foreach (var label in this.GetVisualDescendants().OfType<DrawnTreeLabel>()) label.Refresh(forceIcons);
        foreach (var container in GetRealizedContainers())
            if (container is ListBoxItem item && item.Content is DrawnTreeEntry entry)
            {
                Avalonia.Automation.AutomationProperties.SetName(item, entry.Text);
                Avalonia.Automation.AutomationProperties.SetHelpText(item, entry.Help ?? entry.Id);
            }
        InvalidateVisual();
    }

    private void TreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (SelectedItem is DrawnTreeEntry entry)
        {
            if (e.Key == Key.Right)
            {
                if (!entry.Expanded) Expand(entry, true);
                else if (entry.Children.FirstOrDefault() is { } child) Reveal(child);
                e.Handled = true;
            }
            else if (e.Key == Key.Left)
            {
                if (entry.Expanded && entry.CanExpand) Expand(entry, false);
                else if (entry.Parent is { } parent) Reveal(parent);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                entry.Activate?.Invoke(2);
                e.Handled = true;
            }
            else if (e.Key == Key.Apps || e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift)
            {
                Reveal(entry);
                this.GetVisualDescendants().OfType<DrawnTreeLabel>().FirstOrDefault(label => ReferenceEquals(label.Entry, entry))?.OpenMenu();
                e.Handled = true;
            }
        }
    }

    internal void Reveal(DrawnTreeEntry entry)
    {
        SelectedItem = entry;
        ScrollIntoView(entry);
        UpdateLayout();
        (ContainerFromItem(entry) as Control)?.Focus();
    }
}