using Avalonia.Controls;
using Avalonia.LogicalTree;
using SharpRail.Host.Abstractions;
using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

internal static class FilesE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "files-git"));
        using var app = OpenFixtureProject(Path.Combine(root, "files-compact"));
        var workspace = CreateWorkspaceViaDialog(app);
        Directory.CreateDirectory(Path.Combine(workspace, "compact", "only", "here"));
        File.WriteAllText(Path.Combine(workspace, "compact", "only", "here", "leaf.txt"), "leaf\n");
        app.Click(app.Find<Button>("Tab_files"));
        app.FileRow("README.md");
        TreeViewItem? Node(string name, bool directory) => app.Find<TreeView>("FilesTree").GetLogicalDescendants().OfType<TreeViewItem>()
            .SingleOrDefault(node => node.Tag is ProjectFile file && file.Name == name && file.IsDirectory == directory);
        Until(() => Node("compact/only/here", true) is not null);
        app.ExpandFolder(Path.Combine("compact", "only", "here"));
        Until(() => Node("leaf.txt", false) is not null && Node("compact/only/here", true)!.IsExpanded);
        Directory.CreateDirectory(Path.Combine(workspace, "compact", "only", "sibling"));
        Until(() => Node("compact/only", true) is not null && Node("here", true) is not null && Node("leaf.txt", false) is not null);
        Require(Node("compact/only", true)!.IsExpanded && Node("here", true)!.IsExpanded,
            "Splitting a compacted run must keep the expanded folders open.");
        Console.WriteLine("PASS upstream files.spec.ts: shows files and compacts single-directory runs in the Files tree");
    }
}
