using Avalonia.Controls;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private void RefreshFileIcons()
    {
        if (toolContent.GetValueOrDefault("files") is not Grid panel) return;
        void Refresh(ItemCollection items)
        {
            foreach (var node in items.OfType<TreeViewItem>())
            {
                if (node.Tag is ProjectFile file && node.Header is Grid row)
                    row.Children[0] = FileIcon(file.Path, file.IsDirectory, file.IsDirectory ? "folder" : "fileText");
                Refresh(node.Items);
            }
        }
        Refresh(panel.Children.OfType<TreeView>().Single().Items);
    }

    private void ReconcileFilesPanel()
    {
        if (toolContent.GetValueOrDefault("files") is not Grid panel) return;
        var tree = panel.Children.OfType<TreeView>().Single();
        ReconcileFileNodes(tree.Items, folderCache.GetValueOrDefault("") ?? []);
    }

    private void ReconcileFileNodes(ItemCollection items, IReadOnlyList<ProjectFile> files)
    {
        var retained = items.OfType<TreeViewItem>().Where(node => node.Tag is ProjectFile)
            .ToDictionary(node => ((ProjectFile)node.Tag!).Path, StringComparer.Ordinal);
        var desired = files.Select(file => retained.TryGetValue(file.Path, out var node) && Equals(node.Tag, file)
            ? node : FileNode(file)).ToArray();
        var keep = desired.ToHashSet();
        for (var index = items.Count - 1; index >= 0; index--)
            if (items[index] is not TreeViewItem node || !keep.Contains(node)) items.RemoveAt(index);
        for (var index = 0; index < desired.Length; index++)
        {
            var node = desired[index];
            if (index >= items.Count || !ReferenceEquals(items[index], node))
            {
                items.Remove(node);
                items.Insert(index, node);
            }
            var file = files[index];
            if (!file.IsDirectory) continue;
            if (folderCache.TryGetValue(file.Path, out var children)) ReconcileFileNodes(node.Items, children);
            else if (node.Items.OfType<TreeViewItem>().Any(child => child.Tag is ProjectFile))
            {
                node.IsExpanded = false;
                node.Items.Clear();
                node.Items.Add(new TreeViewItem { Header = Ui.Text("Loading…", Ui.Hint) });
            }
        }
    }
}