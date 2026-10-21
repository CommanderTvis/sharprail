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

    private IReadOnlyList<DrawnTreeEntry> FileEntries(IReadOnlyList<DrawnTreeEntry> previous, IReadOnlyList<ProjectFile> files, DrawnTree tree, bool ignoredParent = false)
    {
        var retained = previous.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        return files.Select(file =>
        {
            var kept = retained.TryGetValue(file.Path, out var old) && Equals(old.Tag, file) && old.Muted == (file.GitIgnored || ignoredParent);
            var entry = kept ? old! : FileNode(file, tree, ignoredParent);
            entry.Tip = ProjectFileTip(file);
            entry.Help = file.Path + (entry.Tip is { Length: > 0 } tip ? " · " + tip : "");
            if (file.IsDirectory)
            {
                if (folderCache.TryGetValue(file.Path, out var children)) entry.Children = FileEntries(entry.Children, children, tree, entry.Muted);
                else
                {
                    if (entry.Children.Any(child => child.Tag is ProjectFile)) entry.Expanded = false;
                    entry.Children = [new(file.Path + "/loading", "Loading…") { Muted = true }];
                }
                // A retained row follows this workspace's remembered expansion, so a switch or restore reopens its folders.
                var remembered = expandedFolders.Contains(file.Path) || expandedFolders.Any(path => path.StartsWith(file.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal));
                if (kept && entry.Expanded != remembered)
                {
                    entry.Expanded = remembered;
                    if (remembered) entry.ExpansionChanged?.Invoke(true);
                }
            }
            MarkEntry(entry);
            return entry;
        }).ToArray();
    }
}