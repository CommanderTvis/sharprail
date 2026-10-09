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

        // A single click on a folder's name toggles it, like the reference tree; a double click must not undo that.
        Directory.CreateDirectory(Path.Combine(workspace, "single"));
        File.WriteAllText(Path.Combine(workspace, "single", "inside.txt"), "inside\n");
        Until(() => Node("single", true) is not null);
        app.Click(app.FileRow("single"));
        Until(() => Node("single", true)!.IsExpanded && Node("inside.txt", false) is not null);
        app.Click(app.FileRow("single"));
        Until(() => !Node("single", true)!.IsExpanded);
        app.Click(app.FileRow("single"), twice: true);
        Settle();
        Require(Node("single", true)!.IsExpanded, "A double click on a folder must leave it toggled once, not twice.");
        File.WriteAllText(Path.Combine(workspace, "single", "external.txt"), "external\n");
        Until(() => Node("external.txt", false) is not null);
        File.Move(Path.Combine(workspace, "single", "external.txt"), Path.Combine(workspace, "single", "renamed.txt"));
        Until(() => Node("external.txt", false) is null && Node("renamed.txt", false) is not null);
        File.Delete(Path.Combine(workspace, "single", "renamed.txt"));
        Until(() => Node("renamed.txt", false) is null);
        Require(Node("single", true)!.IsExpanded, "External changes must preserve expanded folders.");
        Console.WriteLine("PASS files tree folders toggle on a single click of their name");

        File.WriteAllBytes(Path.Combine(workspace, "blob.bin"), [0xFF, 0xFE, 0x00, 0x01]);
        Until(() => Node("blob.bin", false) is not null);
        app.Click(app.FileRow("blob.bin"));
        Until(() => app.Window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "BinaryNotice" && text.Text!.Contains("4 bytes", StringComparison.Ordinal)));
        Require(!app.Find<TextBlock>("WorkspaceError").IsVisible, "Opening a byte-only file is a notice in its tab, not a window error.");
        Console.WriteLine("PASS SharpRail: a byte-only file opens as a notice with its size instead of failing");
    }
}