using Avalonia.Controls;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private void RefreshFileIcons()
    {
        if (toolContent.GetValueOrDefault("files") is Grid panel)
            panel.Children.OfType<DrawnTree>().Single().RefreshRows(forceIcons: true);
    }

    private void ReconcileFilesPanel()
    {
        if (toolContent.GetValueOrDefault("files") is not Grid panel) return;
        var tree = panel.Children.OfType<DrawnTree>().Single();
        tree.SetRoots(FileEntries(tree.Roots, folderCache.GetValueOrDefault("") ?? [], tree));
    }

    private IReadOnlyList<DrawnTreeEntry> FileEntries(IReadOnlyList<DrawnTreeEntry> previous, IReadOnlyList<ProjectFile> files, DrawnTree tree)
    {
        var retained = previous.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        return files.Select(file =>
        {
            var entry = retained.TryGetValue(file.Path, out var old) && Equals(old.Tag, file) ? old : FileNode(file, tree);
            if (file.IsDirectory)
            {
                if (folderCache.TryGetValue(file.Path, out var children)) entry.Children = FileEntries(entry.Children, children, tree);
                else
                {
                    if (entry.Children.Any(child => child.Tag is ProjectFile)) entry.Expanded = false;
                    entry.Children = [new(file.Path + "/loading", "Loading…") { Muted = true }];
                }
            }
            return entry;
        }).ToArray();
    }
}