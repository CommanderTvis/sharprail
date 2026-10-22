using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.Plugins.UI.Kit;
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

        Case("workspaces open without an implicit terminal", app =>
        {
            Until(() => app.Workbench.TerminalCatalog?.Workspaces.ContainsKey(app.Root) == true);
            Require(!app.Window.Preferences.VerticalCenterTabs &&
                app.Window.Layout.View.Documents.Values.SelectMany(tabs => tabs).All(tab => tab.Kind != "terminal") &&
                app.Workbench.TerminalCatalog!.Workspaces[app.Root].Count == 0 && app.Terminals.StartedIn(app.Root) == 0,
                "Forced Projects tab mode must open without a default terminal even when the desktop preferences are off.");
            app.Window.Layout.NewTerminal(app.Center);
            Until(() => app.Workbench.TerminalCatalog!.Workspaces[app.Root].Count == 1 && app.Terminals.StartedIn(app.Root) == 1);
        });

        Case("new workspaces in Projects tab mode have no implicit terminal and still allow explicit launches", app =>
        {
            var workspace = Path.Combine(app.Root, "new-workspace");
            Directory.CreateDirectory(workspace);
            var open = app.Window.OpenProjectAsync(workspace);
            Until(() => open.IsCompleted);
            open.GetAwaiter().GetResult();
            Until(() => app.Workbench.TerminalCatalog?.Workspaces.ContainsKey(workspace) == true);
            Require(app.Window.Layout.View.Documents.Values.SelectMany(tabs => tabs).All(tab => tab.Kind != "terminal") &&
                app.Workbench.TerminalCatalog!.Workspaces[workspace].Count == 0 && app.Terminals.StartedIn(workspace) == 0,
                "A new workspace in Projects tab mode must not place, reserve or start a default terminal.");
            app.Window.Layout.NewTerminal(app.Center);
            Until(() => app.Workbench.TerminalCatalog!.Workspaces[workspace].Count == 1 && app.Terminals.StartedIn(workspace) == 1);
            Require(app.Window.Layout.Tabs(app.Center).Single(tab => tab.Kind == "terminal").Title == "Terminal 1",
                "An explicit terminal still starts with the first terminal number.");
            var terminal = app.Window.Layout.Tabs(app.Center).Single(tab => tab.Kind == "terminal").Id;

            open = app.Window.OpenProjectAsync(workspace);
            Until(() => open.IsCompleted);
            open.GetAwaiter().GetResult();
            Require(app.Window.Layout.View.Documents.Values.SelectMany(tabs => tabs).Single(tab => tab.Kind == "terminal").Id == terminal &&
                app.Workbench.TerminalCatalog!.Workspaces[workspace].Single().Key == terminal,
                "Revisiting a workspace in Projects tab mode must retain its existing explicit terminal.");
        });

        Case("terminal Rename in Projects shares titles and cancellation leaves them alone", app =>
        {
            app.ShowTool("projects");
            app.Window.Layout.NewTerminal(app.Center);
            var workspace = app.Window.WorkspaceRoot;
            var terminal = app.Tabs.Last(tab => tab.Kind == "terminal");
            using var peer = app.NewWindow();
            var peerOpening = peer.Window.OpenProjectAsync(workspace);
            Until(() => peerOpening.IsCompleted && peer.Window.WorkspaceMounted && peer.Window.WorkspaceRoot == workspace);
            if (peerOpening.IsFaulted) throw peerOpening.Exception!;
            Until(() => peer.Window.Layout.State.Workspaces.GetValueOrDefault(workspace)?.Documents.Values
                .SelectMany(tabs => tabs).Any(tab => tab.Id == terminal.Id) == true);
            Window Rename()
            {
                var button = app.Window.WorkspaceRoot == workspace ? app.Find<Button>("Tab_" + terminal.Id.Replace(':', '_')) :
                    app.Window.GetLogicalDescendants().OfType<ContentControl>().Single(host => host.Name == "WorkspaceTabs" && Equals(host.Tag, workspace))
                        .GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "WorkspaceTabPreview" && Equals(button.Tag, terminal.Id));
                app.ContextAction(button, "Rename");
                Until(() => app.Window.OwnedWindows.Any(window => window.Title == "Rename terminal"));
                return app.Window.OwnedWindows.Single(window => window.Title == "Rename terminal");
            }
            var dialog = Rename();
            var input = dialog.GetLogicalDescendants().OfType<TextBox>().Single();
            Require(input.Text == terminal.Title, "Rename starts with the displayed terminal name.");
            input.Text = "Canceled name";
            app.Click(dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: "Cancel" }));
            Require(app.Tabs.Single(tab => tab.Id == terminal.Id).Title == terminal.Title, "Cancel preserves the existing title.");
            dialog = Rename();
            dialog.GetLogicalDescendants().OfType<TextBox>().Single().Text = "  Review changes  ";
            app.Click(dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: "Continue" }));
            string? Title(E2eWorkspace window) => window.Window.Layout.State.Workspaces[workspace].Documents.Values
                .SelectMany(tabs => tabs).Single(tab => tab.Id == terminal.Id).Title;
            Until(() => Title(app) == "Review changes" && Title(peer) == "Review changes");
            app.Window.Layout.NewTerminal(app.Center);
            Until(() => peer.Window.Layout.State.Workspaces[workspace].Documents.Values
                .SelectMany(tabs => tabs).Count(tab => tab.Kind == "terminal") == app.Window.Layout.State.Workspaces[workspace].Documents.Values
                .SelectMany(tabs => tabs).Count(tab => tab.Kind == "terminal"));
            Require(Title(app) == "Review changes" && Title(peer) == "Review changes", "Catalog reconciliation preserves a renamed terminal in both windows.");
            var other = Path.Combine(app.Root, "other-project");
            Directory.CreateDirectory(other);
            var opening = app.Window.OpenProjectAsync(other);
            Until(() => opening.IsCompleted && app.Window.WorkspaceMounted && app.Window.WorkspaceRoot == other);
            if (opening.IsFaulted) throw opening.Exception!;
            dialog = Rename();
            dialog.GetLogicalDescendants().OfType<TextBox>().Single().Text = "Rename inactive terminal";
            app.Click(dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: "Continue" }));
            Until(() => Title(app) == "Rename inactive terminal" && Title(peer) == "Rename inactive terminal");
            Require(app.State!.Current.TerminalTitles.All(title => title.Terminal.TabKey != terminal.Id || title.Terminal.WorkspaceId == workspace),
                "An inactive Projects tab retains its original workspace when renamed.");
        });

        Case("Projects uniquely marks the active tab across tabs and pane members", app =>
        {
            app.Open("README.md", true); app.Open("notes.txt", true);
            app.ShowTool("projects");
            void Check(string tab)
            {
                var markers = app.Window.GetLogicalDescendants().OfType<Border>()
                    .Where(border => border.Name == "ActiveWorkspaceTab" && border.IsVisible).ToArray();
                Require(markers.Length == 1 && markers[0].GetLogicalAncestors().OfType<Grid>()
                    .Any(grid => grid.Name == "DockTab_" + tab), "Exactly the active tab carries the rail accent.");
                var row = markers[0].GetLogicalAncestors().OfType<Grid>().First(grid => grid.Name == "DockTab_" + tab);
                var fill = row.Children.OfType<Border>().First().Background;
                Require(ReferenceEquals(fill, Ui.PrimarySoft) && !ReferenceEquals(fill, Ui.Selected),
                    "The focused Projects tab uses the accent wash instead of the neutral workspace selection.");
                Require(app.Window.GetLogicalDescendants().OfType<Border>().Count(border =>
                    border.Classes.Contains("project-tab-current")) == 1,
                    "Focus changes leave only one Projects tab with the active background, including pane members.");
                Until(() => row.Bounds.Height > 0 && markers[0].Bounds.Height > 0);
                Require(markers[0].Bounds.Top > 0 && markers[0].Bounds.Bottom < row.Bounds.Height &&
                    markers[0].Bounds.Right < row.Bounds.Width,
                    "The active accent stays inside the row and clear of its rounded corners.");
            }
            app.Click(app.Tab("README.md")); Check("markdown_README.md");
            app.Click(app.Tab("notes.txt")); Check("file_notes.txt");
            app.ContextAction(app.Tab("notes.txt"), "Show beside README.md");
            app.Click(app.Tab("README.md")); Check("markdown_README.md");
            app.Click(app.Tab("notes.txt")); Check("file_notes.txt");
        });

        Case("Projects is the only tab home and the main column offers tab groups, not splits", app =>
        {
            app.Open("README.md", true); app.Open("notes.txt", true);
            SetLayout(app, settings => Require(!settings.GetLogicalDescendants().OfType<Control>().Any(control =>
                control.Name is "VerticalCenterTabs" or "VerticalTabsInProjects" or "BottomAlignment"),
                "Layout settings must not expose retired tab modes or bottom alignment."));
            app.Click(app.Tab("notes.txt"), mouseButton: MouseButton.Right);
            var menu = app.Tab("notes.txt").ContextMenu!;
            Until(() => menu.IsOpen);
            Require(!menu.Items.OfType<MenuItem>().Any(item => item.Header is string header && header.StartsWith("Split", StringComparison.Ordinal)) &&
                menu.Items.OfType<MenuItem>().Any(item => item.Header as string == "Show beside README.md"),
                "Projects tabs must offer tab groups and no main column splits.");
            menu.Close();
            var center = app.Center;
            Require(!app.Window.Layout.Move("file:notes.txt", center, center, 0, "right") && app.Window.Layout.State.Center.IsLeaf,
                "Splitting the main column must be refused.");
            Require(app.Tab("notes.txt").Focus() && app.Window.Layout.View.FocusedGroup == center,
                "Focusing a Projects document tab must retain its center group's keyboard ownership.");
            app.Window.KeyPress(Key.W, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control, PhysicalKey.W, null);
            Require(app.Window.Layout.Tabs(center).All(tab => tab.Path != "notes.txt"),
                "Close Tab must close the focused Projects document rather than its surrounding tool group.");
        });

        Case("two tabs shown together render as panes and can be restacked and separated", app =>
        {
            app.Open("README.md", true); app.Open("notes.txt", true);
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
            Require(Equals(app.Find<Grid>("DockTab_file_notes.txt").Tag, "paned"),
                "Projects keeps the members in one tab group.");

            app.ContextAction(app.Tab("notes.txt"), "Stack this group");
            Until(() => app.Window.Layout.PaneFor(app.Center, "file:notes.txt")?.Direction == "vertical");

            app.ContextAction(app.Tab("notes.txt"), "Show on its own");
            Until(() => app.Window.Layout.PaneFor(app.Center, "file:notes.txt") is null);
            Require(!app.Window.GetLogicalDescendants().OfType<Grid>().Any(grid => grid.Name == "TabPane_" + app.Center), "Separated, the pane is gone.");
        });

        Case("dropping a tab on the middle of another shows them together", app =>
        {
            app.Open("README.md", true); app.Open("notes.txt", true);
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
            app.Open("notes.txt", true); app.Open("README.md", true);
            app.ExpandFolder("styles"); app.Open("styles/notes.txt", true);
            Until(() => app.Window.GetLogicalDescendants().OfType<TextBlock>().Count(text => text.Name == "TabSubtitle") == 2);
            Require(app.Window.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Name == "TabSubtitle" && text.Text?.EndsWith("styles", StringComparison.Ordinal) == true) &&
                app.Find<Grid>("DockTab_markdown_README.md").GetLogicalDescendants().OfType<TextBlock>().All(text => text.Name != "TabSubtitle"),
                "Only the ambiguous names carry their folder.");
        });

        Case("tabs stay in Projects when its panel is hidden", app =>
        {
            app.Open("README.md", true);
            app.ShowTool("projects");
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
            Require(!app.Window.GetLogicalDescendants().OfType<ResizeHandle>().Any(handle => handle.Name == "VerticalTabsResize_" + app.Center),
                "Hiding Projects must not introduce a second tab home.");
            app.Window.Layout.Visible("left", true);
            Until(() => app.Window.GetLogicalDescendants().OfType<StackPanel>().Any(panel => panel.Name == "CenterTabsInProjects"));
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