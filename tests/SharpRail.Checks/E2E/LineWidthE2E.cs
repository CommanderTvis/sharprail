using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class LineWidthE2E
{
    private static string TextOf(SelectableTextBlock block) => string.Concat(block.Inlines?.OfType<Run>().Select(run => run.Text) ?? []);

    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "line-width-git"));
        var longLine = string.Join(' ', Enumerable.Range(1, 80).Select(index => $"segment-{index:00}"));
        var directory = IsolatedGit.Repository(Path.Combine(root, "line-width-diff"), ("LONG_LINE.txt", longLine));
        using var app = new E2eWorkspace(directory, openFiles: false);
        File.WriteAllText(Path.Combine(directory, "LONG_LINE.txt"), "changed " + longLine);
        var refresh = app.Window.RefreshAsync();
        Until(() => refresh.IsCompleted); refresh.GetAwaiter().GetResult();
        app.Click(app.Find<Button>("Tab_changes"));
        Until(() => app.Find<Control>("ChangesPanel").GetLogicalDescendants().OfType<Button>()
            .Any(button => AutomationProperties.GetName(button) == "LONG_LINE.txt"));
        app.Click(app.Find<Control>("ChangesPanel").GetLogicalDescendants().OfType<Button>()
            .Single(button => AutomationProperties.GetName(button) == "LONG_LINE.txt"));
        Until(() => app.Tabs.Count(tab => tab.Kind == "diff") == 1);
        Until(() => app.Window.GetLogicalDescendants().OfType<SelectableTextBlock>()
            .Any(block => block.Name == "DiffNewText" && TextOf(block).Contains("changed segment-01", StringComparison.Ordinal)));
        Settle(300);
        foreach (var name in new[] { "DiffOldText", "DiffNewText" })
        {
            var block = app.Find<SelectableTextBlock>(name);
            var scroll = block.GetLogicalAncestors().OfType<ScrollViewer>().First();
            var logicalLines = TextOf(block).TrimEnd('\n').Split('\n').Length;
            var rendered = block.TextLayout.TextLines.Count;
            Require(rendered > logicalLines, $"The {name} long line must wrap ({rendered} rendered vs {logicalLines} logical lines).");
            Require(block.Bounds.Width <= app.Window.Preferences.PreviewWidth + 1 && scroll.Extent.Width <= scroll.Viewport.Width + 1,
                $"The {name} side must stay within the default file width without horizontal scrolling.");
        }
        Console.WriteLine("PASS upstream line-width-settings.spec.ts: the default file width wraps both sides of a long-line diff");
    }
}
