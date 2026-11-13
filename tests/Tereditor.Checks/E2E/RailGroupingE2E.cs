using Avalonia;
using Avalonia.Controls;
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

        app.OpenSettings();
        Until(() => app.Window.OpenSettingsViews.Any(window => window.IsVisible));
        var settings = app.Window.OpenSettingsViews.Single(window => window.IsVisible);
        app.Click(Named<Button>(settings, "Settings_Projects"));
        var toggle = Named<CheckBox>(settings, "GroupWorkspacesUnderCreator");
        Require(toggle.IsChecked == false, "Grouping is off by default.");
        toggle.IsChecked = true;
        settings.Close(); Until(() => !settings.IsVisible);
        Require(app.Window.Preferences.GroupWorkspacesUnderCreator, "The switch must change the app preference.");
        Until(() => Order().SequenceEqual([project, orphan, creatorWorkspace, child, chatChild]));
        Require(Item(app, child).Margin.Left > Item(app, creatorWorkspace).Margin.Left && Item(app, orphan).Margin.Left == Item(app, creatorWorkspace).Margin.Left &&
            Named<Border>(Item(app, child), "WorkspaceNestGuide").IsEffectivelyVisible && !Named<Border>(Item(app, orphan), "WorkspaceNestGuide").IsVisible,
            "On, the created workspace sits indented under its creator's workspace; one whose creator tab is gone stays flat.");
        Require(Item(app, chatChild).Margin.Left > Item(app, creatorWorkspace).Margin.Left && Named<Border>(Item(app, chatChild), "WorkspaceNestGuide").IsEffectivelyVisible,
            "A workspace made by a chat tab nests under its creator too.");
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
        Until(() => app.State.Current.WorkspaceCreators.Count == 4 && Item(app, second).Margin.Left > Item(app, creatorWorkspace).Margin.Left);
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
        Until(() => app.State.Current.WorkspaceCreators.Count == 6 && Item(app, last).Margin.Left > Item(app, project).Margin.Left);
        SteadyColumns(app, project, orchestrator, [first, last], orphan);
        Console.WriteLine("PASS rail grouping: selecting a terminal or a workspace moves no row, icon, title or guide line");

        var reloaded = new ProfileStore(Path.Combine(root, "rail-grouping", "sample-project") + "-profile");
        Require(reloaded.Data.Preferences.GroupWorkspacesUnderCreator, "The preference must be stored and restored.");
        Require(new HostStateStore(app.State.DirectoryPath).Current.WorkspaceCreators.Any(entry => entry.Workspace == child && entry.Creator.TabKey == terminal),
            "The host must restore the recorded creator after a restart.");
        Console.WriteLine("PASS rail grouping: the preference and the creators are stored and restored");
    }

    /// <summary>Opens an unrelated workspace, then the creator with its tab selected by a click, then a nested workspace, and requires every column to stay within half a pixel.</summary>
    private static void SteadyColumns(E2eWorkspace app, string creator, string tab, string[] children, string unrelated)
    {
        var live = "DockTab_" + tab.Replace(':', '_').Replace('/', '_');
        (string Name, double X)[]? baseline = null;
        foreach (var (open, state) in new[] { (unrelated, "neither selected"), (creator, "terminal selected"), (children[0], "workspace selected") })
        {
            app.Click(Select(app, open));
            Until(() => Active(app, open));
            if (open == creator)
            {
                // Selected as a click selects it: the tab is chosen and its pane focused, so the row wears the current look.
                Until(() => Controls(app).Any(control => control.Name == live && control.IsEffectivelyVisible));
                app.Click(Controls(app).First(control => control.Name == live).GetVisualDescendants().OfType<Button>().First(button => button.Name == "Tab_" + live["DockTab_".Length..]));
                Until(() => Controls(app).First(control => control.Name == live).GetVisualDescendants().OfType<Border>().Any(border => border.Name == "ActiveWorkspaceTab" && border.IsVisible));
            }
            Until(() => GuidesJoin(app, creator, tab, children[0], children[1]));
            var columns = RailColumns(app, tab, live, [creator, .. children]);
            baseline ??= columns;
            static string Show((string Name, double X)[] items) => string.Join(", ", items.Select(item => $"{item.Name} {item.X:0.#}"));
            Require(columns.Length == baseline.Length && columns.Zip(baseline).All(pair => pair.First.Name == pair.Second.Name && Math.Abs(pair.First.X - pair.Second.X) <= 0.5),
                $"Rail rows keep their columns whichever row is selected ({state}): {Show(columns)}; expected {Show(baseline)}");
        }
    }

    /// <summary>The window X of the creator tab's icon and title and of each workspace's icon, title and guide line.</summary>
    private static (string Name, double X)[] RailColumns(E2eWorkspace app, string tab, string live, string[] workspaces)
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
        }
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