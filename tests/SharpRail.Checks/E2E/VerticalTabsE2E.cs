using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.UI.Docking;
using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

/// <summary>Fork layout.spec.ts: vertical centre tabs, tab panes, and the column living in Projects.</summary>
internal static class VerticalTabsE2E
{
    internal static void Run(string root)
    {
        var index = 0;
        void Case(string title, Action<E2eWorkspace> test)
        {
            using var app = new E2eWorkspace(Path.Combine(root, "vertical-" + index++));
            try { test(app); }
            catch (Exception error) { throw new InvalidOperationException("Fork layout.spec.ts: " + title, error); }
            Console.WriteLine("PASS fork layout.spec.ts: " + title);
        }

        Case("vertical tabs are a setting, a resizable column beside the editor, with no centre split", app =>
        {
            app.Open("README.md", true); app.Open("notes.txt", true);
            SetLayout(app, settings =>
            {
                var vertical = Control<CheckBox>(settings, "VerticalCenterTabs");
                var inProjects = Control<CheckBox>(settings, "VerticalTabsInProjects");
                Require(vertical.IsChecked == false && !inProjects.IsEnabled, "Vertical tabs start off, and their Projects home waits on them.");
                vertical.IsChecked = true;
                Until(() => inProjects.IsEnabled);
            });
            Require(app.Window.Preferences.VerticalCenterTabs, "The switch changes the app preference.");
            var scroller = app.Find<ScrollViewer>("TabScroller_" + app.Center);
            var body = app.Find<Border>("DockBody_" + app.Center);
            Until(() => scroller.Bounds.Width > 0);
            var strip = scroller.TranslatePoint(default, app.Window)!.Value;
            var editor = body.TranslatePoint(default, app.Window)!.Value;
            Require(Math.Abs(scroller.Bounds.Width - VerticalTabs.DefaultWidth) <= 2 && strip.X < editor.X && Math.Abs(strip.Y - editor.Y) <= 1,
                $"The column sits beside the editor at its default width, not above it: {scroller.Bounds}.");
            var readme = app.Tab("README.md");
            var notes = app.Tab("notes.txt");
            Require(readme.TranslatePoint(default, app.Window)!.Value.Y < notes.TranslatePoint(default, app.Window)!.Value.Y,
                "Tabs stack down the column.");

            var handle = app.Find<ResizeHandle>("VerticalTabsResize_" + app.Center);
            var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), app.Window)!.Value;
            app.Window.MouseMove(start); app.Window.MouseDown(start, MouseButton.Left);
            app.Window.MouseMove(start + new Vector(60, 0)); app.Window.MouseUp(start + new Vector(60, 0), MouseButton.Left); Settle();
            Require(Math.Abs(app.Window.Preferences.VerticalCenterTabsWidth - (VerticalTabs.DefaultWidth + 60)) <= 2,
                $"Dragging the edge stores the column's width in pixels: {app.Window.Preferences.VerticalCenterTabsWidth}.");

            app.Click(notes, mouseButton: MouseButton.Right);
            var menu = notes.ContextMenu!;
            Until(() => menu.IsOpen);
            var headers = menu.Items.OfType<MenuItem>().Select(item => item.Header as string).ToArray();
            Require(!headers.Any(header => header?.StartsWith("Split", StringComparison.Ordinal) == true) && headers.Contains("Show beside README.md") &&
                headers.Contains("Move up"), "The menu offers the neighbour to show beside and no split: " + string.Join(", ", headers));
            menu.Close();
        });

        Case("two tabs shown together render as panes, survive turning vertical tabs off, and can be restacked and separated", app =>
        {
            app.Open("README.md", true); app.Open("notes.txt", true);
            SetLayout(app, settings => Control<CheckBox>(settings, "VerticalCenterTabs").IsChecked = true);
            app.ContextAction(app.Tab("notes.txt"), "Show beside README.md");
            Until(() => app.Window.Layout.PaneFor(app.Center, "file:notes.txt") is not null);
            var pane = app.Find<Grid>("TabPane_" + app.Center);
            var members = pane.GetLogicalDescendants().OfType<Border>().Where(border => border.Name == "PaneMember").ToArray();
            Require(members.Length == 2 && members[0].TranslatePoint(default, pane)!.Value.X < members[1].TranslatePoint(default, pane)!.Value.X,
                "The members render side by side as columns.");
            var detached = 0;
            members[0].DetachedFromVisualTree += (_, _) => detached++;
            var refresh = app.Window.RefreshAsync();
            Until(() => refresh.IsCompleted);
            refresh.GetAwaiter().GetResult();
            Require(ReferenceEquals(pane, app.Find<Grid>("TabPane_" + app.Center)) && detached == 0,
                "A content refresh must retain the paired pane and its mounted member frames.");
            Require(app.Find<Grid>("DockTab_file_notes.txt").GetLogicalDescendants().OfType<Border>().Any(border => border.Name == "PaneMarker"),
                "Members carry a left accent in the column.");

            app.ContextAction(app.Tab("notes.txt"), "Stack this group");
            Until(() => app.Window.Layout.PaneFor(app.Center, "file:notes.txt")?.Direction == "vertical");

            SetLayout(app, settings => Control<CheckBox>(settings, "VerticalCenterTabs").IsChecked = false);
            Until(() => !app.Window.Preferences.VerticalCenterTabs);
            app.Click(app.Tab("README.md"));
            Until(() => app.Window.GetLogicalDescendants().OfType<Grid>().Any(grid => grid.Name == "TabPane_" + app.Center));
            Require(app.Window.GetLogicalDescendants().OfType<MenuItem>().All(item => item.Header as string != "Show beside notes.txt"),
                "Making a pane is a vertical-strip gesture.");

            app.ContextAction(app.Tab("notes.txt"), "Show on its own");
            Until(() => app.Window.Layout.PaneFor(app.Center, "file:notes.txt") is null);
            Require(!app.Window.GetLogicalDescendants().OfType<Grid>().Any(grid => grid.Name == "TabPane_" + app.Center), "Separated, the pane is gone.");
        });

        Case("dropping a tab on the middle of another shows them together", app =>
        {
            app.Open("README.md", true); app.Open("notes.txt", true);
            SetLayout(app, settings => Control<CheckBox>(settings, "VerticalCenterTabs").IsChecked = true);
            var source = app.Tab("notes.txt");
            var target = app.Find<Grid>("DockTab_markdown_README.md");
            Settle(550);
            var start = source.TranslatePoint(new Point(source.Bounds.Width / 2, source.Bounds.Height / 2), app.Window)!.Value;
            var middle = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), app.Window)!.Value;
            app.Window.MouseMove(start); app.Window.MouseDown(start, MouseButton.Left);
            app.Window.MouseMove(start + new Vector(4, -12)); Settle();
            app.Window.MouseMove(middle); Settle();
            Require(app.Window.GetVisualDescendants().OfType<Border>().Any(border => Equals(border.Tag, "DropShowBeside")), "The band between the edges offers to show the tab beside.");
            app.Window.MouseUp(middle, MouseButton.Left); Settle();
            Require(app.Window.Layout.PaneFor(app.Center, "file:notes.txt")?.TabIds.SequenceEqual(["markdown:README.md", "file:notes.txt"]) == true,
                "The drop makes a pane in the setting's arrangement.");
        });

        Case("a basename two tabs share gets its folder on a second line", app =>
        {
            File.WriteAllText(Path.Combine(app.Root, "styles", "notes.txt"), "styles notes\n");
            SetLayout(app, settings => Control<CheckBox>(settings, "VerticalCenterTabs").IsChecked = true);
            app.Open("notes.txt", true); app.Open("README.md", true);
            app.ExpandFolder("styles"); app.Open("styles/notes.txt", true);
            Until(() => app.Window.GetLogicalDescendants().OfType<TextBlock>().Count(text => text.Name == "TabSubtitle") == 2);
            Require(app.Window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "TabSubtitle" && text.Text?.EndsWith("styles", StringComparison.Ordinal) == true) &&
                app.Find<Grid>("DockTab_markdown_README.md").GetLogicalDescendants().OfType<TextBlock>().All(text => text.Name != "TabSubtitle"),
                "Only the ambiguous names carry their folder.");
        });

        Case("the column can live in Projects under its workspace, and comes back when Projects is hidden", app =>
        {
            app.Open("README.md", true);
            app.Click(app.Find<Button>("Tab_projects"));
            SetLayout(app, settings =>
            {
                Control<CheckBox>(settings, "VerticalCenterTabs").IsChecked = true;
                Control<CheckBox>(settings, "VerticalTabsInProjects").IsChecked = true;
            });
            Until(() => app.Window.GetLogicalDescendants().OfType<StackPanel>().Any(panel => panel.Name == "CenterTabsInProjects" &&
                panel.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "Tab_markdown_README.md")));
            var host = app.Window.GetLogicalDescendants().OfType<ContentControl>().Single(control => control.Name == "WorkspaceTabs" && Equals(control.Tag, app.Window.WorkspaceRoot));
            Require(host.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "Tab_markdown_README.md") &&
                !app.Window.GetLogicalDescendants().OfType<ResizeHandle>().Any(handle => handle.Name == "VerticalTabsResize_" + app.Center),
                "The strip renders under the active workspace's row and the centre keeps only its editor.");
            app.Click(app.Tab("README.md"));
            Require(app.Window.Layout.Selected(app.Center)?.Path == "README.md", "Its tabs still select.");
            app.Open("notes.txt", true); app.Click(app.Tab("README.md"));
            Until(() => app.Window.Layout.Selected(app.Center)?.Path == "README.md");
            // Anywhere on the row selects it, not only its icon and title.
            var row = app.Find<Grid>("DockTab_file_notes.txt");
            Settle(550);
            var blank = row.TranslatePoint(new Point(row.Bounds.Width - 40, row.Bounds.Height / 2), app.Window)!.Value;
            app.Window.MouseMove(blank); app.Window.MouseDown(blank, MouseButton.Left); app.Window.MouseUp(blank, MouseButton.Left);
            Until(() => app.Window.Layout.Selected(app.Center)?.Path == "notes.txt");

            app.Window.Layout.Visible("left", false);
            Until(() => app.Window.GetLogicalDescendants().OfType<ResizeHandle>().Any(handle => handle.Name == "VerticalTabsResize_" + app.Center));
            Require(app.Find<ScrollViewer>("TabScroller_" + app.Center).Bounds.Width > 0, "With Projects hidden the column comes back to the centre.");
        });
    }

    private static T Control<T>(SettingsWindow settings, string name) where T : Control =>
        settings.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static void SetLayout(E2eWorkspace app, Action<SettingsWindow> change)
    {
        app.Click(app.Find<Button>("SettingsButton"));
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any(window => window.IsVisible));
        var settings = app.Window.OwnedWindows.OfType<SettingsWindow>().Single(window => window.IsVisible);
        app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Settings_Layout"));
        change(settings);
        settings.Close(); Until(() => !settings.IsVisible);
        Settle();
    }
}