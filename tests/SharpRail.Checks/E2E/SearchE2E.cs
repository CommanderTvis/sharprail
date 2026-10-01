using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.Scintilla;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

internal static class SearchE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "search-git"));
        OpensHitAtLine(Path.Combine(root, "search"));
        NoMatches(Path.Combine(root, "search-empty"));
    }

    private static Window Open(E2eWorkspace app)
    {
        app.Window.Focus();
        var modifiers = (OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control) | RawInputModifiers.Shift;
        app.Window.KeyPress(Key.F, modifiers, PhysicalKey.F, "f");
        app.Window.KeyRelease(Key.F, modifiers, PhysicalKey.F, "f");
        return Dialog(app, "SearchDialog");
    }

    private static Button[] Hits(Window dialog) => dialog.GetLogicalDescendants().OfType<Button>().Where(button => button.Name == "SearchHit").ToArray();

    private static void OpensHitAtLine(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        var lines = Enumerable.Range(1, 120).Select(index => "filler line " + index).ToArray();
        lines[79] = "beta needle beta";
        File.WriteAllText(Path.Combine(workspace, "haystack.txt"), string.Join("\n", lines) + "\n");
        File.WriteAllText(Path.Combine(workspace, "ignored.log"), "needle in an ignored file\n");
        File.AppendAllText(Path.Combine(workspace, ".gitignore"), "\n*.log\n");

        var dialog = Open(app);
        Named<TextBox>(dialog, "SearchQuery").Text = "NEEDLE";
        Until(() => Hits(dialog).Length == 1);
        var hit = (SearchHit)Hits(dialog)[0].Tag!;
        Require(hit is { Path: "haystack.txt", Line: 80 }, "The hit must name its file and line.");
        Require(dialog.GetLogicalDescendants().OfType<StackPanel>().Single(panel => panel.Name == "SearchFile").Tag as string == "haystack.txt",
            "Results must be grouped under their file.");
        app.Click(Hits(dialog)[0]);
        Until(() => !dialog.IsVisible && app.Tabs.Any(tab => tab.Path == "haystack.txt" && !tab.Preview));
        if (OperatingSystem.IsMacOS())
        {
            ScintillaEditor? Editor() => app.Window.GetLogicalDescendants().OfType<ScintillaEditor>().SingleOrDefault(editor => editor.IsEffectivelyVisible);
            Until(() => Editor()?.Text.Contains("beta needle beta", StringComparison.Ordinal) == true);
            Until(() => Editor()!.FirstVisibleLine > 60);
        }
        Console.WriteLine("PASS upstream search.spec.ts: Mod+Shift+F searches the worktree and a hit opens its file at that line");
    }

    private static void NoMatches(string directory)
    {
        using var app = OpenFixtureProject(directory);
        CreateWorkspaceViaDialog(app);
        var dialog = Open(app);
        Named<TextBox>(dialog, "SearchQuery").Text = "zzz-nothing-matches-this-zzz";
        var status = Named<TextBlock>(dialog, "SearchStatus");
        Until(() => status.IsVisible && status.Text == "No matches");
        Press(dialog, Key.Escape);
        Until(() => !dialog.IsVisible);
        Console.WriteLine("PASS upstream search.spec.ts: a query with no matches says so, and Escape closes the popup");
    }
}