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

}