using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private TreeView ChangesTree(GitChange[] changes)
    {
        var tree = new TreeView { Name = "ChangesTree", Background = Ui.Sidebar, Margin = new Thickness(6) };
        foreach (var node in ChangeNodes(changes.Select(change => (Change: change, Parts: change.Path.Split('/'))).ToArray(), 0)) tree.Items.Add(node);
        return tree;
    }

    private IEnumerable<TreeViewItem> ChangeNodes((GitChange Change, string[] Parts)[] changes, int depth)
    {
        var folders = changes.Where(change => change.Parts.Length > depth + 1)
            .GroupBy(change => change.Parts[depth]).OrderBy(group => group.Key, StringComparer.CurrentCulture);
        foreach (var folder in folders)
        {
            var descendants = folder.ToArray();
            var parts = descendants[0].Parts;
            var end = depth + 1;
            while (descendants.All(change => change.Parts.Length > end + 1 && change.Parts[end] == parts[end])) end++;
            var label = string.Join('/', parts.Skip(depth).Take(end - depth));
            var node = new TreeViewItem { IsExpanded = true, Tag = string.Join('/', parts.Take(end)) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,Auto"), Height = 28 };
            Ui.Place(row, Ui.Row("folder", label));
            var stats = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            stats.Children.Add(Ui.Text("+" + descendants.Sum(change => change.Change.Added), Ui.Success, 12));
            stats.Children.Add(Ui.Text("−" + descendants.Sum(change => change.Change.Removed), Ui.Danger, 12));
            Ui.Place(row, stats, 0, 2);
            var button = new Button
            {
                Content = row,
                Padding = new Thickness(4, 0),
                Background = Brushes.Transparent,
                BorderThickness = new(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => node.IsExpanded = !node.IsExpanded;
            node.Header = button;
            foreach (var child in ChangeNodes(descendants, end)) node.Items.Add(child);
            yield return node;
        }
        foreach (var change in changes.Where(change => change.Parts.Length == depth + 1)
            .OrderBy(change => change.Parts[^1], StringComparer.CurrentCulture))
            yield return new TreeViewItem { Header = ChangeRow(change.Change, change.Parts[^1]), Tag = change.Change };
    }
}
