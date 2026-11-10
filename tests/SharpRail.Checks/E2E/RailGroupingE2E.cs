using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;

using SharpRail.Host.Core;
using SharpRail.Plugins.Api;
using SharpRail.UI;
using SharpRail.UI.State;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

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

        var reloaded = new ProfileStore(Path.Combine(root, "rail-grouping", "sample-project") + "-profile");
        Require(reloaded.Data.Preferences.GroupWorkspacesUnderCreator, "The preference must be stored and restored.");
        Require(new HostStateStore(app.State.DirectoryPath).Current.WorkspaceCreators.Any(entry => entry.Workspace == child && entry.Creator.TabKey == terminal),
            "The host must restore the recorded creator after a restart.");
        Console.WriteLine("PASS rail grouping: the preference and the creators are stored and restored");
    }

    /// <summary>Whether a grouped row's guide line is centred, within half a pixel, on the leading icon of the tab row that created it.</summary>
    private static bool GuideUnderIcon(E2eWorkspace app, string workspace, Control tabRow)
    {
        var guide = Named<Border>(Item(app, workspace), "WorkspaceNestGuide");
        var marked = tabRow.GetVisualDescendants().OfType<MarkedRow>().First();
        var icon = marked.Children.First(child => child.GetValue(MarkedRow.RoleProperty) == RowRole.Leading);
        if (!guide.IsEffectivelyVisible || guide.TranslatePoint(new Point(guide.Bounds.Width / 2, 0), icon) is not { } at) return false;
        return Math.Abs(at.X - icon.Bounds.Width / 2) <= 0.5;
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
            Require(Math.Abs(button.Padding.Right - (dotShown ? SharpRail.UI.WorkbenchWindow.RailDotInset : 4)) < 0.01, "The row reserves the indicator column only while an indicator shows: " + path);
            Require(name.Bounds.Left == MarkedRow.LeadingWidth + MarkedRow.Gap && name.Bounds.Width <= marked.Bounds.Width - name.Bounds.Left + 0.5, "The title starts after the icon column and stays within the row: " + path);
            var branch = row.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Name == "WorkspaceBranch");
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
            Require(Math.Abs(dot.Bounds.Center.Y - row.Bounds.Height / 2) < 0.01, "The indicator is centred on the row.");
            if (name.Length > 10)
            {
                var whole = new TextBlock { Text = name, FontSize = 14 };
                whole.Measure(Size.Infinity);
                Require(title.Bounds.Width < whole.DesiredSize.Width && dot.Bounds.Right > row.Bounds.Width - 24, $"A trimmed title leaves the indicator at the row's end, right after the ellipsis: dot {dot.Bounds}, title {title.Bounds}, row {row.Bounds}");
            }
        }
        Console.WriteLine("PASS rail titles: one shared line budget per list, wrapped between words, full title in the tooltip");
    }
}