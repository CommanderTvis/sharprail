using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;

using Tereditor.Host.Core;
using Tereditor.Plugins.Api;
using Tereditor.UI;
using Tereditor.UI.State;

using static Tereditor.Checks.E2E.E2eWorkspace;
using static Tereditor.Checks.E2E.WorkspaceFixture;

namespace Tereditor.Checks.E2E;

/// <summary>Projects groups a workspace under the terminal that created it when the preference is on, and only then.</summary>
internal static class RailGroupingE2E
{
    internal static void Run(string root)
    {
        Grouped(root);
        Unified(root);
    }

    private static void Grouped(string root)
    {
        using var app = OpenFixtureProject(Path.Combine(root, "rail-grouping"));
        var project = app.Window.ProjectRoot;
        var creatorWorkspace = CreateWorkspaceViaDialog(app);
        app.Window.Layout.NewTerminal(app.Center);
        var terminal = app.Window.Layout.Tabs(app.Center).Last(tab => tab.Kind == "terminal").Id;
        app.Window.Layout.Open(new("plugin:claude:chat-1", "Chat", "plugin", "claude:chat-1"), true, app.Center);
        var child = CreateWorkspaceViaDialog(app);
        var chatChild = CreateWorkspaceViaDialog(app);
        var orphan = CreateWorkspaceViaDialog(app);
        app.State!.RecordWorkspaceCreator(child, new TerminalRef(creatorWorkspace, terminal));
        app.State.RecordWorkspaceCreator(orphan, new TerminalRef(creatorWorkspace, "closed-terminal"));
        app.State.RecordWorkspaceCreator(chatChild, new TerminalRef(creatorWorkspace, "chat-1"));
        Until(() => app.State.Current.WorkspaceCreators.Count == 3);

        string[] Order() => Controls(app).OfType<Grid>().Where(item => item.Name == "WorkspaceItem" && item.IsEffectivelyVisible).Select(item => (string)item.Tag!).ToArray();
        Require(Order().SequenceEqual([project, orphan, chatChild, child, creatorWorkspace]), "Off, the rail stays flat, newest first: " + string.Join(", ", Order()));
        Require(!Controls(app).OfType<Border>().Any(guide => guide.Name == "WorkspaceNestGuide" && guide.IsEffectivelyVisible), "Off, no row shows a nesting guide.");
        Console.WriteLine("PASS rail grouping: off keeps the flat list");

        TurnOnGrouping(app);
        Until(() => Order().SequenceEqual([project, orphan, creatorWorkspace, child, chatChild]));
        // A nested row sits in its creator tab's slot, so its own margin is relative to that slot: compare where the rows show.
        Until(() => Left(app, child) > Left(app, creatorWorkspace) + 0.5 && Math.Abs(Left(app, orphan) - Left(app, creatorWorkspace)) <= 0.5 &&
            Left(app, chatChild) > Left(app, creatorWorkspace) + 0.5);
        Require(Named<Border>(Item(app, child), "WorkspaceNestGuide").IsEffectivelyVisible && !Named<Border>(Item(app, orphan), "WorkspaceNestGuide").IsVisible,
            "On, the created workspace sits indented under its creator's workspace; one whose creator tab is gone stays flat.");
        Require(Named<Border>(Item(app, chatChild), "WorkspaceNestGuide").IsEffectivelyVisible, "A workspace made by a chat tab nests under its creator too.");
        // The created workspace sits directly under the tab that made it, not below the creator's whole tab block.
        string[] Sequence() => Controls(app).Where(control => control.IsEffectivelyVisible && (control.Name == "WorkspaceItem" && (string?)control.Tag is { } path && (path == child || path == chatChild || path == creatorWorkspace) ||
            control.Name == "WorkspaceTabPreview" && (string?)control.Tag is { } tab && (tab == terminal || tab == "plugin:claude:chat-1")))
            .Select(control => control.Name == "WorkspaceItem" ? (string)control.Tag! : "tab:" + control.Tag).ToArray();
        Until(() => Sequence().SequenceEqual([creatorWorkspace, "tab:" + terminal, child, "tab:plugin:claude:chat-1", chatChild]));
        Until(() => GuideUnderIcon(app, child, Controls(app).First(control => control.Name == "WorkspaceTabPreview" && (string?)control.Tag == terminal)));
        Until(() => GuideUnderIcon(app, chatChild, Controls(app).First(control => control.Name == "WorkspaceTabPreview" && (string?)control.Tag == "plugin:claude:chat-1")));

        // The same holds while the creator's workspace is open: its live tab rows hold the nested workspaces in place.
        app.Click(Select(app, creatorWorkspace));
        Until(() => Active(app, creatorWorkspace));
        string Live(string id) => "DockTab_" + id.Replace(':', '_').Replace('/', '_');
        string[] LiveSequence() => Controls(app).Where(control => control.IsEffectivelyVisible && (control.Name == "WorkspaceItem" && (string?)control.Tag is { } path && (path == child || path == chatChild || path == creatorWorkspace) ||
            control.Name == Live(terminal) || control.Name == Live("plugin:claude:chat-1")))
            .Select(control => control.Name == "WorkspaceItem" ? (string)control.Tag! : control.Name == Live(terminal) ? "tab:terminal" : "tab:chat").ToArray();
        Until(() => LiveSequence().SequenceEqual([creatorWorkspace, "tab:terminal", child, "tab:chat", chatChild]));
        Until(() => GuideUnderIcon(app, child, Controls(app).First(control => control.Name == Live(terminal))));
        Until(() => GuideUnderIcon(app, chatChild, Controls(app).First(control => control.Name == Live("plugin:claude:chat-1"))));
        Console.WriteLine("PASS rail grouping: on nests under the creator and falls back to flat when the creator is gone");

        TitleBudget(app, project, [project, orphan, creatorWorkspace, child, chatChild]);

        // Two workspaces from one tab share one unbroken line from the tab's icon, whichever workspace is open.
        var second = CreateWorkspaceViaDialog(app);
        app.State.RecordWorkspaceCreator(second, new TerminalRef(creatorWorkspace, terminal));
        Until(() => app.State.Current.WorkspaceCreators.Count == 4 && Left(app, second) > Left(app, creatorWorkspace) + 0.5);
        foreach (var open in new[] { creatorWorkspace, project })
        {
            app.Click(Select(app, open));
            Until(() => Active(app, open));
            Until(() => GuidesJoin(app, creatorWorkspace, terminal, child, second));
        }
        Console.WriteLine("PASS rail grouping: sibling guide lines form one line from the icon, with the creator's workspace open or not");

        // Selection changes colours only: with the creator's terminal selected, a nested workspace selected or neither, every row keeps its columns.
        SteadyColumns(app, creatorWorkspace, terminal, [child, second], project);
        // The same for a terminal in the project's Default workspace that created two workspaces.
        app.Click(Select(app, project));
        Until(() => Active(app, project));
        app.Window.Layout.NewTerminal(app.Center);
        var orchestrator = app.Window.Layout.Tabs(app.Center).Last(tab => tab.Kind == "terminal").Id;
        // The creator is not the last tab, and the rail is narrow enough to shorten titles.
        app.Window.Layout.NewTerminal(app.Center);
        app.Window.Layout.Geometry(state => state.LeftWidth = .12);
        var first = CreateWorkspaceViaDialog(app);
        var last = CreateWorkspaceViaDialog(app);
        app.State.RecordWorkspaceCreator(first, new TerminalRef(project, orchestrator));
        app.State.RecordWorkspaceCreator(last, new TerminalRef(project, orchestrator));
        Until(() => app.State.Current.WorkspaceCreators.Count == 6 && Left(app, last) > Left(app, project) + 0.5);
        SteadyColumns(app, project, orchestrator, [first, last], orphan);
        Console.WriteLine("PASS rail grouping: selecting a terminal or a workspace moves no row, icon, title or guide line");
        OverflowColumns(app, project, orchestrator, [first, last]);
        Console.WriteLine("PASS rail grouping: an overflowing or scrolled rail keeps every column of the rail that shows all its rows");

        // A creator inside a tab group keeps its workspaces inside the group's box, right under it and before the next member,
        // with its workspace open (live strip) or not (preview).
        app.Click(Select(app, creatorWorkspace));
        Until(() => Active(app, creatorWorkspace));
        const string member = "plugin:claude:chat-2";
        app.Window.Layout.Open(new(member, "Second chat", "plugin", "claude:chat-2"), true, app.Center);
        Require(app.Window.Layout.GroupTabs(app.Center, member, terminal, "vertical") &&
            app.Window.Layout.PaneFor(app.Center, terminal)?.TabIds.SequenceEqual([terminal, member]) == true, "The terminal leads a tab group with the second chat.");
        Control? TabRow(string id) => Controls(app).FirstOrDefault(control => control.IsEffectivelyVisible &&
            (control.Name == "WorkspaceTabPreview" && (string?)control.Tag == id || control.Name == Live(id)));
        Rect Box(Control control) => new(control.TranslatePoint(default, app.Window) ?? default, control.Bounds.Size);
        bool InsideGroup(string boxName)
        {
            if (TabRow(terminal) is not { } creatorRow || TabRow(member) is not { } nextRow ||
                creatorRow.GetVisualAncestors().OfType<Border>().FirstOrDefault(border => border.Name == boxName) is not { } box ||
                !nextRow.GetVisualAncestors().Contains(box)) return false;
            return new[] { child, second }.All(path => Item(app, path) is var item && item.IsEffectivelyVisible && item.GetVisualAncestors().Contains(box) &&
                Box(box).Inflate(0.5).Contains(Box(item)) && Box(item).Top >= Box(creatorRow).Bottom - 0.5 && Box(item).Bottom <= Box(nextRow).Top + 0.5);
        }
        Until(() => InsideGroup("TabPaneGroup"));
        Until(() => GuidesJoin(app, creatorWorkspace, terminal, child, second));
        app.Click(Select(app, project));
        Until(() => Active(app, project));
        Until(() => InsideGroup("WorkspaceTabsPreviewPane"));
        Until(() => GuidesJoin(app, creatorWorkspace, terminal, child, second));
        // A creator outside any group keeps its place right under its own row, outside the group.
        Until(() => TabRow("plugin:claude:chat-1") is { } chatRow && GuideUnderIcon(app, chatChild, chatRow) &&
            !Item(app, chatChild).GetVisualAncestors().OfType<Border>().Any(border => border.Name == "WorkspaceTabsPreviewPane"));
        // Inside a group, too, the rows keep their columns whether the creator's workspace is open or not.
        SteadyColumns(app, creatorWorkspace, terminal, [child, second], project);
        Console.WriteLine("PASS rail grouping: a workspace made by a tab in a tab group sits inside the group, under that tab");

        var reloaded = new ProfileStore(Path.Combine(root, "rail-grouping", "sample-project") + "-profile");
        Require(reloaded.Data.Preferences.GroupWorkspacesUnderCreator, "The preference must be stored and restored.");
        Require(new HostStateStore(app.State.DirectoryPath).Current.WorkspaceCreators.Any(entry => entry.Workspace == child && entry.Creator.TabKey == terminal),
            "The host must restore the recorded creator after a restart.");
        Console.WriteLine("PASS rail grouping: the preference and the creators are stored and restored");
    }

    /// <summary>The macOS app draws the rail in the unified chrome, under the header the top bar's controls move into: the columns hold there too.</summary>
    private static void Unified(string root)
    {
        UnifiedChrome.Override = true;
        try
        {
            using var unified = OpenFixtureProject(Path.Combine(root, "rail-grouping-unified"));
            var home = unified.Window.ProjectRoot;
            TurnOnGrouping(unified);
            // The creator lives in the Default workspace, not on the project's home page.
            unified.Click(Select(unified, home));
            Until(() => Active(unified, home));
            unified.Window.Layout.NewTerminal(unified.Center);
            var creator = unified.Window.Layout.Tabs(unified.Center).Last(tab => tab.Kind == "terminal").Id;
            unified.Window.Layout.NewTerminal(unified.Center);
            var made = new[] { CreateWorkspaceViaDialog(unified), CreateWorkspaceViaDialog(unified) };
            // More rows than a short window holds once a workspace's tab rows open under it.
            for (var extra = 0; extra < 4; extra++) CreateWorkspaceViaDialog(unified);
            foreach (var path in made) unified.State!.RecordWorkspaceCreator(path, new TerminalRef(home, creator));
            Until(() => unified.State!.Current.WorkspaceCreators.Count == 2 && Left(unified, made[1]) > Left(unified, home) + 0.5);
            OverflowColumns(unified, home, creator, made);
        }
        finally { UnifiedChrome.Override = false; }
        Console.WriteLine("PASS rail grouping: the unified rail keeps its columns when it overflows or scrolls");
    }

    /// <summary>Where a workspace's rail row starts in the window.</summary>
    private static double Left(E2eWorkspace app, string path) => (Item(app, path).TranslatePoint(default, app.Window) ?? default).X;

    /// <summary>Turns on "Group workspaces under the terminal or chat that created them" in Settings, as a user does.</summary>
    private static void TurnOnGrouping(E2eWorkspace app)
    {
        app.OpenSettings();
        Until(() => app.Window.OpenSettingsViews.Any(window => window.IsVisible));
        var settings = app.Window.OpenSettingsViews.Single(window => window.IsVisible);
        app.Click(Named<Button>(settings, "Settings_Projects"));
        var toggle = Named<CheckBox>(settings, "GroupWorkspacesUnderCreator");
        Require(toggle.IsChecked == false, "Grouping is off by default.");
        toggle.IsChecked = true;
        settings.Close(); Until(() => !settings.IsVisible);
        Require(app.Window.Preferences.GroupWorkspacesUnderCreator, "The switch must change the app preference.");
    }

    /// <summary>Opens a workspace by its rail row; for the creator's workspace, also selects the creator's tab as a click selects it.</summary>
    private static void Choose(E2eWorkspace app, string open, string creator, string tab)
    {
        var live = "DockTab_" + tab.Replace(':', '_').Replace('/', '_');
        app.Click(Select(app, open));
        Until(() => Active(app, open));
        if (open != creator) return;
        // Selected as a click selects it: the tab is chosen and its pane focused, so the row wears the current look.
        Until(() => Controls(app).Any(control => control.Name == live && control.IsEffectivelyVisible));
        app.Click(Controls(app).First(control => control.Name == live).GetVisualDescendants().OfType<Button>().First(button => button.Name == "Tab_" + live["DockTab_".Length..]));
        Until(() => Controls(app).First(control => control.Name == live).GetVisualDescendants().OfType<Border>().Any(border => border.Name == "ActiveWorkspaceTab" && border.IsVisible));
    }

    /// <summary>
    /// A window short enough that the selected workspace's rows overflow the rail, so its scroll bar shows, keeps every column of a
    /// window tall enough for all of them: the creator's workspace row, the tab's box edges, each child's icon, title and guide line,
    /// the open workspace's launcher row and the rail itself, with the creator's terminal selected or a child workspace, scrolled to
    /// the top or the end. A failure carries the scroller's and the window content's geometry, so it names the layer that moved.
    /// </summary>
    private static void OverflowColumns(E2eWorkspace app, string creator, string tab, string[] children)
    {
        var live = "DockTab_" + tab.Replace(':', '_').Replace('/', '_');
        var scroller = Controls(app).OfType<ScrollViewer>().First(item => item.Content is StackPanel { Name: "ProjectsTree" });
        // The rail's own bar: the tab strips inside its rows have scrollers of their own, found first in a depth-first walk.
        var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().First(item => item.Name == "PART_VerticalScrollBar" && ReferenceEquals(item.TemplatedParent, scroller));
        var zoom = app.Window.FindControl<LayoutTransformControl>("ZoomRoot")!;
        var panel = Controls(app).First(control => control.Name == "ProjectsPanel");
        bool Overflows() => scroller.Extent.Height > scroller.Viewport.Height + 1;
        double X(Control control) => (control.TranslatePoint(default, app.Window) ?? default).X;
        string Geometry() => $"rail offset {scroller.Offset}, extent {scroller.Extent}, viewport {scroller.Viewport}, bounds {scroller.Bounds}, desired {scroller.DesiredSize}, " +
            $"scroll bar shown {bar.IsVisible} at {X(bar):0.#} width {bar.Bounds.Width}; projects panel at {X(panel):0.#} {panel.Bounds}; " +
            $"window content {zoom.Child?.Bounds} desired {zoom.Child?.DesiredSize} in {zoom.Bounds}";
        static string Show((string Name, double X)[] items) => string.Join(", ", items.Select(item => $"{item.Name} {item.X:0.#}"));
        (string Name, double X)[] Columns(string open)
        {
            var columns = RailColumns(app, tab, live, [creator, .. children], []).ToList();
            var row = Controls(app).First(control => control.IsEffectivelyVisible && (control.Name == "WorkspaceTabPreview" && (string?)control.Tag == tab || control.Name == live));
            columns.Add(("tab box left", X(row)));
            columns.Add(("tab box right", X(row) + row.Bounds.Width));
            columns.Add(("projects panel left", X(panel)));
            columns.Add(("projects panel right", X(panel) + panel.Bounds.Width));
            // The open workspace's launcher row (new terminal and the agents) wraps under its tabs; it must not change width either.
            if (Controls(app).FirstOrDefault(control => control.IsEffectivelyVisible && control.Name?.StartsWith("TabStripActions_", StringComparison.Ordinal) == true &&
                control.GetVisualAncestors().Any(ancestor => ancestor is Control { Name: "WorkspaceTabs" } host && Equals(host.Tag, open))) is { } launcher)
            {
                columns.Add(("launcher left", X(launcher)));
                columns.Add(("launcher right", X(launcher) + launcher.Bounds.Width));
            }
            return [.. columns];
        }
        var height = app.Window.Height;
        var tall = new Dictionary<string, (string Name, double X)[]>();
        try
        {
            foreach (var (rows, overflow) in new[] { (1600.0, false), (420.0, true) })
            {
                app.Window.Height = rows;
                Settle();
                foreach (var open in new[] { creator, children[0] })
                {
                    scroller.Offset = default;
                    Choose(app, open, creator, tab);
                    Until(() => GuidesJoin(app, creator, tab, children[0], children[1]));
                    Settle();
                    var state = (open == creator ? "terminal selected" : "workspace selected") + (overflow ? ", short window" : ", tall window");
                    if (!overflow) Require(!Overflows() && !bar.IsVisible, $"The tall window shows every rail row without a scroll bar ({state}): {Geometry()}");
                    else if (open != creator) Require(Overflows() && bar.IsVisible, $"The short window makes the rail overflow and show its scroll bar ({state}): {Geometry()}");
                    foreach (var end in overflow && Overflows() ? new[] { false, true } : new[] { false })
                    {
                        if (end)
                        {
                            scroller.Offset = new Vector(0, scroller.Extent.Height - scroller.Viewport.Height);
                            Settle();
                            Require(scroller.Offset.Y > 1, $"The rail scrolls to its end ({state}): {Geometry()}");
                        }
                        var where = state + (end ? ", scrolled to the end" : ", scrolled to the top");
                        Require(Math.Abs(scroller.Offset.X) <= 0.01, $"The rail never scrolls sideways ({where}): {Geometry()}");
                        Require(zoom.Child is { } content && Math.Abs(content.Bounds.X) <= 0.5 && Math.Abs(content.Bounds.Width - zoom.Bounds.Width) <= 0.5,
                            $"The window content fills the window from its left edge ({where}): {Geometry()}");
                        var columns = Columns(open);
                        if (open != creator)
                        {
                            // Across selections the tab's box (a live tab or its preview), every row and the rail itself hold too;
                            // only the launcher row belongs to the open workspace.
                            static (string Name, double X)[] Shared((string Name, double X)[] items) => [.. items.Where(item => !item.Name.StartsWith("launcher", StringComparison.Ordinal))];
                            var (selected, terminal) = (Shared(columns), Shared(tall[creator]));
                            Require(selected.Length == terminal.Length && selected.Zip(terminal).All(pair => pair.First.Name == pair.Second.Name && Math.Abs(pair.First.X - pair.Second.X) <= 0.5),
                                $"The tab's box and every rail row keep the columns they have with the terminal selected in the tall window ({where}): {Show(selected)}; expected {Show(terminal)}; {Geometry()}");
                        }
                        if (!overflow) { tall[open] = columns; continue; }
                        var expected = tall[open];
                        Require(columns.Length == expected.Length && columns.Zip(expected).All(pair => pair.First.Name == pair.Second.Name && Math.Abs(pair.First.X - pair.Second.X) <= 0.5),
                            $"Rail rows keep their columns when the rail overflows ({where}): {Show(columns)}; expected {Show(expected)}; {Geometry()}");
                    }
                }
            }
        }
        finally
        {
            app.Window.Height = height;
            scroller.Offset = default;
        }
    }

    /// <summary>
    /// Opens an unrelated workspace, then the creator with its tab selected by a click, then a nested workspace, then the creator's
    /// tab again, and requires every column, the rows' right edges, the gap under the tab and the pitch between the nested rows
    /// to stay within half a pixel.
    /// </summary>
    private static void SteadyColumns(E2eWorkspace app, string creator, string tab, string[] children, string unrelated)
    {
        var live = "DockTab_" + tab.Replace(':', '_').Replace('/', '_');
        (string Name, double X)[]? baseline = null;
        // The lowest nested row is the one opened, so its own tab strip, which shows under it, stays out of the measured pitch.
        string? lowest = null;
        foreach (var (choice, state) in new (string? Open, string State)[] { (unrelated, "neither selected"), (creator, "terminal selected"), (null, "workspace selected"), (creator, "terminal selected again") })
        {
            var open = choice ?? lowest!;
            Choose(app, open, creator, tab);
            Until(() => GuidesJoin(app, creator, tab, children[0], children[1]));
            var columns = RailColumns(app, tab, live, [creator, .. children], children);
            baseline ??= columns;
            lowest ??= children.OrderBy(path => Item(app, path).TranslatePoint(default, app.Window).GetValueOrDefault().Y).Last();
            static string Show((string Name, double X)[] items) => string.Join(", ", items.Select(item => $"{item.Name} {item.X:0.#}"));
            Require(columns.Length == baseline.Length && columns.Zip(baseline).All(pair => pair.First.Name == pair.Second.Name && Math.Abs(pair.First.X - pair.Second.X) <= 0.5),
                $"Rail rows keep their columns whichever row is selected ({state}): {Show(columns)}; expected {Show(baseline)}");
        }
    }

    /// <summary>
    /// The window X of the creator tab's icon and title and of each workspace's icon, title, guide line and right edge (where a
    /// trimmed title's indicator column ends), then the tab row's height, the gap from it to its first nested row and the
    /// vertical pitch between the nested rows.
    /// </summary>
    private static (string Name, double X)[] RailColumns(E2eWorkspace app, string tab, string live, string[] workspaces, string[] nested)
    {
        double X(Control control) => (control.TranslatePoint(default, app.Window) ?? default).X;
        static Control Role(Control row, RowRole role) => row.GetVisualDescendants().OfType<MarkedRow>().First().Children.First(child => child.GetValue(MarkedRow.RoleProperty) == role);
        var row = Controls(app).First(control => control.IsEffectivelyVisible && (control.Name == "WorkspaceTabPreview" && (string?)control.Tag == tab || control.Name == live));
        var columns = new List<(string, double)> { ("tab icon", X(Role(row, RowRole.Leading))), ("tab title", X(Role(row, RowRole.Title))) };
        foreach (var path in workspaces)
        {
            var item = Item(app, path);
            var name = Path.GetFileName(path);
            columns.Add((name + " icon", X(Role(item, RowRole.Leading))));
            columns.Add((name + " title", X(Role(item, RowRole.Title))));
            var guide = Named<Border>(item, "WorkspaceNestGuide");
            if (guide.IsEffectivelyVisible) columns.Add((name + " guide", X(guide)));
            var select = item.GetVisualDescendants().OfType<Button>().First(button => button.Name == "WorkspaceSelect");
            columns.Add((name + " right", X(select) + select.Bounds.Width));
        }
        var tops = nested.Select(path => (Item(app, path).TranslatePoint(default, app.Window) ?? default).Y).Order().ToArray();
        columns.Add(("tab height", row.Bounds.Height));
        if (tops.Length > 0) columns.Add(("gap under tab", tops[0] - ((row.TranslatePoint(default, app.Window) ?? default).Y + row.Bounds.Height)));
        for (var index = 1; index < tops.Length; index++) columns.Add(("pitch " + index, tops[index] - tops[index - 1]));
        return [.. columns];
    }

    /// <summary>Whether two children of one tab have guides that start at the tab's icon and touch, with no gap between the rows.</summary>
    private static bool GuidesJoin(E2eWorkspace app, string creator, string tab, string first, string second)
    {
        Rect Span(string path)
        {
            var guide = Named<Border>(Item(app, path), "WorkspaceNestGuide");
            return new Rect(guide.TranslatePoint(default, app.Window) ?? default, guide.Bounds.Size);
        }
        var upper = Item(app, first).TranslatePoint(default, app.Window).GetValueOrDefault().Y < Item(app, second).TranslatePoint(default, app.Window).GetValueOrDefault().Y ? first : second;
        var lower = upper == first ? second : first;
        var row = Controls(app).FirstOrDefault(control => control.IsEffectivelyVisible && (control.Name == "WorkspaceTabPreview" && (string?)control.Tag == tab || control.Name == "DockTab_" + tab.Replace(':', '_').Replace('/', '_')));
        if (row is null) return false;
        var icon = row.GetVisualDescendants().OfType<MarkedRow>().First().Children.First(child => child.GetValue(MarkedRow.RoleProperty) == RowRole.Leading);
        var iconBottom = (icon.TranslatePoint(new Point(0, icon.Bounds.Height), app.Window) ?? default).Y;
        return Math.Abs(Span(upper).Top - iconBottom) <= 0.5 && Span(upper).Bottom >= Span(lower).Top - 0.5;
    }

    /// <summary>Whether a grouped row's guide line is centred, within half a pixel, on the leading icon of the tab row that created it.</summary>
    private static bool GuideUnderIcon(E2eWorkspace app, string workspace, Control tabRow)
    {
        var guide = Named<Border>(Item(app, workspace), "WorkspaceNestGuide");
        var marked = tabRow.GetVisualDescendants().OfType<MarkedRow>().First();
        var icon = marked.Children.First(child => child.GetValue(MarkedRow.RoleProperty) == RowRole.Leading);
        if (!guide.IsEffectivelyVisible || guide.TranslatePoint(new Point(guide.Bounds.Width / 2, 0), icon) is not { } at) return false;
        return Math.Abs(at.X - icon.Bounds.Width / 2) <= 0.5 && GuideClearOfIcon(app, guide, icon) && GuideClearOfChildIcon(app, guide, Item(app, workspace));
    }

    /// <summary>Whether the guide's rectangle misses the icon of the grouped workspace row it belongs to, with a gap on both sides.</summary>
    private static bool GuideClearOfChildIcon(E2eWorkspace app, Control guide, Control item)
    {
        var childIcon = item.GetVisualDescendants().OfType<MarkedRow>().First().Children.First(child => child.GetValue(MarkedRow.RoleProperty) == RowRole.Leading);
        var line = new Rect(guide.TranslatePoint(default, app.Window) ?? default, guide.Bounds.Size);
        var mark = new Rect(childIcon.TranslatePoint(default, app.Window) ?? default, childIcon.Bounds.Size);
        return !line.Inflate(2).Intersects(mark);
    }

    /// <summary>Whether the guide's rectangle stays clear of the creator icon's bounds and starts just below them, never over the icon.</summary>
    private static bool GuideClearOfIcon(E2eWorkspace app, Control guide, Control icon)
    {
        var line = new Rect(guide.TranslatePoint(default, app.Window) ?? default, guide.Bounds.Size);
        var mark = new Rect(icon.TranslatePoint(default, app.Window) ?? default, icon.Bounds.Size);
        return !line.Intersects(mark) && line.Top >= mark.Bottom - 0.01 && line.Top - mark.Bottom <= 4;
    }

    /// <summary>A project's titles share one line budget that shrinks as its list grows, and they wrap between words.</summary>
    private static void TitleBudget(E2eWorkspace app, string project, string[] paths)
    {
        Require(RailTitleBudget.Lines(1) == 3 && RailTitleBudget.Lines(4) == 3 && RailTitleBudget.Lines(5) == 2 && RailTitleBudget.Lines(8) == 1 && RailTitleBudget.Lines(40) == 1,
            "Titles take 3 lines in a short list, 2 in a longer one and 1 in a long one.");
        for (var rows = 1; rows < 40; rows++) Require(RailTitleBudget.Lines(rows + 1) <= RailTitleBudget.Lines(rows), "A longer list never gets more title lines: " + rows);
        var titles = paths.Select(path => Item(app, path).GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "WorkspaceName")).ToArray();
        var expected = RailTitleBudget.Lines(paths.Length);
        Require(titles.All(title => title.MaxLines == expected), "Every title in one list has the same budget of " + expected + ": " + string.Join(", ", titles.Select(title => title.MaxLines)));
        foreach (var path in paths)
        {
            var row = Item(app, path);
            var name = row.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "WorkspaceName");
            var button = row.GetLogicalDescendants().OfType<Button>().Single(item => item.Name == "WorkspaceSelect");
            var marked = name.GetVisualAncestors().OfType<MarkedRow>().First();
            var dotShown = row.GetLogicalDescendants().OfType<Border>().Single(item => item.Name == "WorkspaceUnread").IsVisible;
            // A title may take the row's width minus the icon column and the padding, which reserves the dot's column only while a dot shows.
            Require(Math.Abs(button.Padding.Right - (dotShown ? Tereditor.UI.WorkbenchWindow.RailDotInset : 4)) < 0.01, "The row reserves the indicator column only while an indicator shows: " + path);
            Require(name.Bounds.Left == MarkedRow.LeadingWidth + MarkedRow.Gap && name.Bounds.Width <= marked.Bounds.Width - name.Bounds.Left + 0.5, "The title starts after the icon column and stays within the row: " + path);
            var branch = row.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Name == "WorkspaceBranch");
            var rowDot = row.GetLogicalDescendants().OfType<Border>().Single(item => item.Name == "WorkspaceUnread");
            if (dotShown) Require(Math.Abs(rowDot.Bounds.Center.Y - (name.Bounds.Top + name.LineHeight / 2)) <= 0.5, $"The indicator is centred on the title's first line: dot {rowDot.Bounds}, title {name.Bounds}: " + path);
            if (branch is not null) foreach (var number in row.GetLogicalDescendants().OfType<StackPanel>().Single(item => item.Name == "WorkspaceDiffStats").Children.OfType<TextBlock>())
                Require(number.FontSize == branch.FontSize && number.LineHeight == branch.LineHeight && number.VerticalAlignment == branch.VerticalAlignment, "Change numbers share the branch line's size and line height: " + path);
        }
        var select = Item(app, paths[1]).GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "WorkspaceSelect");
        Require(ToolTip.GetTip(select) is string tip && tip.EndsWith(paths[1]) && tip.StartsWith(titles[1].Text ?? "?"), "A shortened title keeps its full text and the path in the tooltip.");

        // Wrapping at each budget: normal words break only between words, and the lines never pass the budget.
        const string words = "Review the environment check before the shell script location moves";
        foreach (var lines in new[] { 3, 2, 1 })
        {
            var text = new TextBlock { Text = words, FontSize = 14, LineHeight = RailTitleBudget.TitleLineHeight, TextWrapping = TextWrapping.Wrap, MaxLines = lines, TextTrimming = TextTrimming.CharacterEllipsis };
            text.Measure(new Size(150, double.PositiveInfinity));
            var layout = text.TextLayout.TextLines;
            Require(layout.Count == lines, $"A long title fills its budget of {lines} lines: {layout.Count}");
            Require(Math.Abs(text.DesiredSize.Height - lines * RailTitleBudget.TitleLineHeight) <= 1, $"{lines} title lines are {lines} line heights tall: {text.DesiredSize.Height}");
            foreach (var line in layout.Skip(1))
                Require(char.IsWhiteSpace(words[line.FirstTextSourceIndex - 1]), $"A title breaks between words, not inside one ({lines} lines, at {line.FirstTextSourceIndex}).");
        }

        // An indicator presses against the title's end: after a short title, and after the ellipsis of a trimmed one, inside the row.
        foreach (var name in new[] { "Short", "A very long workspace title that cannot fit the row at all" })
        {
            var title = new TextBlock { Text = name, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
            var dot = new Border { Width = 8, Height = 8 };
            var row = new MarkedRow { Width = 200, Height = 36 };
            row.Add(new Border { Width = 14, Height = 14 }, RowRole.Leading);
            row.Add(title, RowRole.Title);
            row.Add(dot, RowRole.Mark);
            row.Measure(new Size(200, 36)); row.Arrange(new Rect(0, 0, 200, 36));
            Require(Math.Abs(dot.Bounds.Left - (title.Bounds.Right + MarkedRow.Gap)) < 0.01, $"The indicator sits one gap after the title: dot {dot.Bounds}, title {title.Bounds}");
            Require(dot.Bounds.Right <= row.Bounds.Width + 0.01 && dot.Bounds.Left >= title.Bounds.Right, "The indicator stays inside the row, after the title.");
            Require(Math.Abs(dot.Bounds.Center.Y - (title.Bounds.Top + title.Bounds.Height / 2)) <= 0.5, "A one-line title's indicator is centred on it.");
            if (name.Length > 10)
            {
                var whole = new TextBlock { Text = name, FontSize = 14 };
                whole.Measure(Size.Infinity);
                Require(title.Bounds.Width < whole.DesiredSize.Width && dot.Bounds.Right > row.Bounds.Width - 24, $"A trimmed title leaves the indicator at the row's end, right after the ellipsis: dot {dot.Bounds}, title {title.Bounds}, row {row.Bounds}");
            }
        }
        // With a second line and with wrapped titles the indicator centres on the title's FIRST line only, and the title does not move.
        foreach (var (text, width, below) in new[] { ("Default", 200.0, true), ("Review the environment check before the shell script location moves", 150.0, false), ("Review the environment check before the shell script location moves", 150.0, true) })
        {
            var title = new TextBlock { Text = text, FontSize = 14, LineHeight = 17.5, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis };
            var dot = new Border { Width = 8, Height = 8 };
            var row = new MarkedRow { Width = width };
            row.Add(new Border { Width = 14, Height = 14 }, RowRole.Leading);
            row.Add(title, RowRole.Title);
            row.Add(dot, RowRole.Mark);
            if (below) row.Add(new TextBlock { Text = "branch", FontSize = 12, LineHeight = 15 }, RowRole.Below);
            row.Measure(new Size(width, double.PositiveInfinity)); row.Arrange(new Rect(row.DesiredSize));
            Require(Math.Abs(dot.Bounds.Center.Y - (title.Bounds.Top + 17.5 / 2)) <= 0.5, $"The indicator centres on the title's first line ({text.Length} chars, below {below}): dot {dot.Bounds}, title {title.Bounds}");
        }
        Console.WriteLine("PASS rail titles: one shared line budget per list, wrapped between words, full title in the tooltip");
    }
}