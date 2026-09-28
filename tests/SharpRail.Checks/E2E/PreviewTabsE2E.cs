using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class PreviewTabsE2E
{
    internal static void Run(string root)
    {
        var index = 0;
        void Case(string title, Action<E2eWorkspace> test)
        {
            using var app = new E2eWorkspace(Path.Combine(root, "preview-" + index++));
            try { test(app); }
            catch (Exception error) { throw new InvalidOperationException("Upstream preview-tabs.spec.ts: " + title, error); }
            Console.WriteLine("PASS upstream preview-tabs.spec.ts: " + title);
        }
        Case("a single click previews into one reusable slot, a double click keeps the tab", app =>
        {
            app.Open("README.md"); Require(app.Tabs.Count == 1 && app.Tabs[0].Preview, "Single click must preview.");
            Require(app.Tab("README.md").GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Text == "README.md").FontStyle == FontStyle.Italic, "Preview label must be italic.");
            app.Open("notes.txt"); Require(app.Tabs.Count == 1 && app.Tabs[0].Path == "notes.txt" && app.Tabs[0].Preview, "The preview slot must be reused.");
            app.Open("notes.txt", true); Require(app.Tabs.Count == 1 && !app.Tabs[0].Preview, "Double click must keep the tab.");
            Require(app.Tab("notes.txt").GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Text == "notes.txt").FontStyle == FontStyle.Normal, "Kept label must be normal.");
            app.Open("README.md"); Require(app.Tabs.Count == 2 && app.Tabs[1].Preview, "A kept tab must survive browsing.");
            app.Click(app.Tab("README.md")); Until(() => !app.Tabs[1].Preview);
            app.Open("notes.txt"); Require(app.Tabs.Count == 2 && !app.Tabs[0].Preview && app.Window.Layout.Selected(app.Center)?.Path == "notes.txt", "Reopening a kept tab must retain and activate it.");
        });
        Case("a double click claims the slot on its way to keeping the tab, at any latency", app =>
        {
            app.Open("README.md", true); app.Open("notes.txt"); app.Open("LINKS.md", true);
            Require(app.Tabs.Count == 2 && app.Tabs[0].Path == "README.md" && app.Tabs[1].Path == "LINKS.md" && !app.Tabs[1].Preview,
                "Keeping a new file must claim the reusable preview slot.");
        });
        Case("a double click on an unopened file sends exactly one fs.readFile", app =>
        {
            app.Open("README.md", true); Settle();
            Require(app.Tabs.Count == 1 && !app.Tabs[0].Preview && app.Host.Reads.GetValueOrDefault("README.md") == 1, "A double click must issue one host read.");
        });
        Case("a browse the user has navigated away from is dropped, not activated on arrival", app =>
        {
            app.Open("README.md", true); var gate = app.Host.Hold("notes.txt");
            app.Click(app.FileRow("notes.txt")); Until(() => app.Host.Reads.GetValueOrDefault("notes.txt") == 1);
            app.Click(app.Tab("README.md"), freshGesture: false); gate.SetResult(); Settle();
            Require(app.Tabs.Count == 1 && app.Window.Layout.Selected(app.Center)?.Path == "README.md", "An abandoned browse must not arrive later.");
        });
        Case("of two browse clicks in flight at once, the later one wins", app =>
        {
            var gate = app.Host.Hold("README.md"); app.Click(app.FileRow("README.md")); Until(() => app.Host.Reads.GetValueOrDefault("README.md") == 1);
            app.Click(app.FileRow("notes.txt"), freshGesture: false);
            Until(() => app.Window.Layout.Selected(app.Center)?.Path == "notes.txt"); gate.SetResult(); Settle();
            Require(app.Tabs.Count == 1 && app.Tabs[0].Path == "notes.txt" && app.Tabs[0].Preview, "The later browse must win.");
        });
        Case("a keep that lands first does not invalidate a browse requested after it", app =>
        {
            var gate = app.Host.Hold("README.md"); app.Click(app.FileRow("README.md"), true);
            app.Click(app.FileRow("notes.txt"), freshGesture: false); gate.SetResult();
            Until(() => app.Tabs.Any(tab => tab.Path == "notes.txt"));
            Require(app.Tabs.Count == 2 && app.Tabs[0].Path == "README.md" && !app.Tabs[0].Preview && app.Tabs[1].Path == "notes.txt" && app.Tabs[1].Preview,
                "The earlier keep and later browse must both survive.");
        });
        Case("a newer tab click cancels an older preview-tab settle timer", app =>
        {
            app.Open("notes.txt", true); app.Open("README.md");
            app.Click(app.Tab("README.md")); app.Click(app.Tab("notes.txt"), freshGesture: false); Settle();
            Require(app.Window.Layout.Selected(app.Center)?.Path == "notes.txt" && app.Tabs.Single(tab => tab.Path == "README.md").Preview,
                "A newer selection must defeat the preview keep timer.");
        });
        Case("the Specs panel shares the one slot, and closing the preview tab releases it", app =>
        {
            app.Open("README.md", true); app.Open("notes.txt"); app.Click(app.Find<Button>("Tab_specs"));
            var tree = app.Find<TreeView>("SpecsTree"); Until(() => tree.Items.Count > 0);
            var node = tree.Items.OfType<TreeViewItem>().Single(item => Equals(item.Tag, "SPEC.md")); app.Click((Control)node.Header!);
            Until(() => app.Tabs.Any(tab => tab.Path == "SPEC.md"));
            Require(app.Tabs.Count == 2 && app.Tabs[0].Path == "README.md" && app.Tabs[1].Preview, "Specs must use the same preview slot.");
            app.Click(app.Tab("SPEC.md").GetLogicalAncestors().OfType<Grid>().First(grid => grid.Name?.StartsWith("DockTab_", StringComparison.Ordinal) == true)
                .GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "CloseTab"));
            Require(app.Tabs.Count == 1, "Closing the preview must release its slot.");
            app.Click(app.Find<Button>("Tab_files")); app.Open("notes.txt");
            Require(app.Tabs.Count == 2 && app.Tabs[1].Path == "notes.txt" && app.Tabs[1].Preview, "Browsing must refill the released preview slot.");
        });
    }
}
