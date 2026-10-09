using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

using SharpRail.Checks.E2E;
using SharpRail.UI.State;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks;

/// <summary>The header's captioned Project, Workspace and Branch segments, after upstream's shell/locationBar unit tests.</summary>
internal static class LocationBarChecks
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "location-bar-git"));
        Switchers(Path.Combine(root, "location-bar-switchers"));
        SharedActions(Path.Combine(root, "location-bar-actions"));
        BranchCard(Path.Combine(root, "location-bar-branch"));
        Chrome(Path.Combine(root, "location-bar-chrome"));
    }

    private static ContextMenu OpenMenu(E2eWorkspace app, string pill)
    {
        var button = app.Find<Button>(pill);
        app.Click(button);
        Until(() => button.ContextMenu!.IsOpen);
        return button.ContextMenu!;
    }

    private static bool Shown(E2eWorkspace app, string segment)
    {
        var border = app.Find<Border>(segment);
        return border.IsVisible && border.Bounds.Width > 0;
    }

    private static void Switch(E2eWorkspace app, string workspace)
    {
        var menu = OpenMenu(app, "ScopeWorkspace");
        app.Click(menu.Items.OfType<MenuItem>().Single(item => item.Name == "ScopeWorkspaceOption" && Equals(item.Tag, workspace)), freshGesture: false);
        Until(() => Active(app, workspace) && Shown(app, "BranchSegment") && Label(app, "BranchLabel").Length > 0);
    }

    private static string Label(E2eWorkspace app, string name) => app.Find<TextBlock>(name).Text ?? "";

    private static void Switchers(string directory)
    {
        using var app = OpenFixtureProject(directory);
        Require(Shown(app, "ProjectSegment") && Shown(app, "WorkspaceSegment") && !Shown(app, "BranchSegment"),
            "Project Home shows the Project and Workspace segments and no Branch segment.");
        Require(Label(app, "ProjectLabel") == "sample-project" && Label(app, "WorkspaceLabel") == "Project home",
            "Project Home names the project and reads Project home.");
        var captions = app.Find<SharpRail.UI.LocationStrip>("LocationBar").GetLogicalDescendants().OfType<TextBlock>()
            .Where(text => text.Classes.Contains("location-caption") && text.Name is null).Select(text => text.Text);
        Require(captions.SequenceEqual(["PROJECT", "WORKSPACE", "BRANCH"]), "Each segment carries its uppercase caption.");

        var projects = OpenMenu(app, "ScopeProject");
        var option = projects.Items.OfType<MenuItem>().Single(item => item.Name == "ScopeProjectOption");
        Require(option.IsChecked && Equals(option.Tag, app.Root) && MenuEntry(projects, "ScopeProjectHome") is { IsEnabled: false } &&
            MenuEntry(projects, "ScopeProjectAdd") is { IsEnabled: true },
            "The project switcher checks the current project, disables Project home there and offers Add project.");
        CloseMenu(projects);

        var workspaces = OpenMenu(app, "ScopeWorkspace");
        Require(MenuEntry(workspaces, "WorkspaceCopyPath") is null && MenuEntry(workspaces, "WorkspaceRename") is null &&
            MenuEntry(workspaces, "ScopeWorkspaceNew") is not null,
            "At Project Home the workspace menu offers only the switcher and creation.");
        CloseMenu(workspaces);
        Switch(app, app.Root);
        Until(() => Label(app, "WorkspaceLabel") == "Default" && Shown(app, "BranchSegment") && Label(app, "BranchLabel") == "main");
        Require(Label(app, "ScopeBase").Length == 0, "The Default workspace's branch caption is plain.");

        projects = OpenMenu(app, "ScopeProject");
        Require(MenuEntry(projects, "ScopeProjectHome") is { IsEnabled: true }, "Project home is reachable from a workspace.");
        Choose(app, projects, "ScopeProjectHome");
        Until(() => app.Window.AtProjectHome && Label(app, "WorkspaceLabel") == "Project home" && !Shown(app, "BranchSegment"));
        Console.WriteLine("PASS location bar: project and workspace switchers move between Project Home and workspaces");
    }

    private static void SharedActions(string directory)
    {
        var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        var name = Path.GetFileName(workspace);
        var ready = app.Window.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "WorkspaceReadyBranch").Text!;
        var from = ready.Contains(" · from ", StringComparison.Ordinal) ? "· from " + ready[(ready.IndexOf(" · from ", StringComparison.Ordinal) + 8)..] : "";
        Require(Label(app, "WorkspaceLabel") == name && Label(app, "ScopeBase") == from, "A managed workspace names itself and what it was cut from.");

        var menu = OpenMenu(app, "ScopeWorkspace");
        Require(MenuEntry(menu, "WorkspaceOpenIn") is not null && MenuEntry(menu, "WorkspaceCopyPath") is not null &&
            MenuEntry(menu, "WorkspaceRemove") is { IsEnabled: true } &&
            menu.Items.OfType<MenuItem>().Single(item => item.Name == "ScopeWorkspaceOption").Tag is string other && other == app.Root,
            "The header menu carries the workspace actions above the sibling switcher.");
        Choose(app, menu, "WorkspaceRename");
        var host = app.Find<ContentControl>("ScopeRename");
        Until(() => host.Content is TextBox { IsFocused: true });
        var input = (TextBox)host.Content!;
        Require(input.Text == name && !app.Find<Button>("ScopeWorkspace").IsVisible &&
            !Item(app, workspace).GetLogicalChildren().OfType<TextBox>().Any(),
            "A header rename replaces the pill with one input and opens none in Projects.");
        input.Text = "Header Name";
        Press(input, Key.Enter);
        Until(() => host.Content is null && Label(app, "WorkspaceLabel") == "Header Name" && ItemText(app, workspace, "WorkspaceName") == "Header Name");
        Require(app.State!.Current.WorkspaceLabels.GetValueOrDefault(workspace) == "Header Name", "The header rename is one host label change.");

        Choose(app, OpenMenu(app, "ScopeWorkspace"), "WorkspaceRename");
        Until(() => host.Content is TextBox { IsFocused: true });
        ((TextBox)host.Content!).Text = "Abandoned";
        _ = app.Window.OpenProjectHomeAsync(app.Root);
        Until(() => app.Window.AtProjectHome && app.Window.WorkspaceMounted);
        Settle();
        Require(host.Content is null && app.Find<Button>("ScopeWorkspace").IsVisible && Label(app, "WorkspaceLabel") == "Project home" &&
            app.State.Current.WorkspaceLabels.GetValueOrDefault(workspace) == "Header Name",
            "Leaving the workspace abandons a header rename without renaming anything.");

        Switch(app, workspace);
        Choose(app, OpenMenu(app, "ScopeWorkspace"), "WorkspaceRemove");
        var confirm = Dialog(app);
        Require(Text(confirm).Contains(workspace, StringComparison.Ordinal), "The Remove dialog names the workspace it was opened for.");
        _ = app.Window.OpenProjectHomeAsync(app.Root);
        Until(() => app.Window.AtProjectHome && app.Window.OwnedWindows.Count == 0);
        Settle();
        Require(Directory.Exists(workspace) && WorktreePaths(app).Contains(workspace),
            "A Remove dialog dismissed by a workspace change removes nothing.");
        app.Dispose();
        Require(new ProfileStore(app.Root + "-profile").OpenState().Current.WorkspaceLabels.GetValueOrDefault(workspace) == "Header Name",
            "The header's label persists in host state.");
        Console.WriteLine("PASS location bar: header workspace actions share Projects' rename and removal, bound to their workspace");
    }

    private static void BranchCard(string directory)
    {
        using var app = OpenFixtureProject(directory);
        Git(app.Root, "branch", "compare-target");
        Switch(app, app.Root);
        app.Click(app.Find<Button>("Tab_changes"));
        Until(() => Buttons(app).Any(button => button.Name == "ChangesBranch"));
        var pill = app.Find<Button>("ScopeBranch");
        var flyout = (Flyout)pill.Flyout!;
        app.Click(pill);
        Until(() => flyout.IsOpen && flyout.Content is Border { IsFocused: true });
        var card = (Border)flyout.Content!;
        Require(card.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "main"), "The branch card names the branch.");
        var copy = Named<Button>(card, "ScopeBranchCopy");
        copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var copied = app.Window.Clipboard!.TryGetTextAsync();
        Until(() => copied.IsCompleted);
        Require(copied.Result == "main", "Copy copies the branch name.");

        MenuItem Target() => Named<Button>((Border)flyout.Content!, "ScopeDiffBase").ContextMenu!.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, "Local")).Items.OfType<MenuItem>().Single(item => item.Tag is "compare-target");
        Target().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Until(() => Text(Buttons(app).Single(button => button.Name == "ChangesBranch")) == "vs compare-target");
        Require(Text(Named<Button>((Border)flyout.Content!, "ScopeDiffBase")) == "compare-target" && Target().IsChecked,
            "The card's picker and Changes show the one comparison target.");
        var changes = Buttons(app).Single(button => button.Name == "ChangesBranch").ContextMenu!.Items.OfType<MenuItem>()
            .Single(item => Equals(item.Header, "Local")).Items.OfType<MenuItem>().Single(item => item.Tag is "main");
        changes.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Until(() => flyout.Content is Border current && Text(Named<Button>(current, "ScopeDiffBase")) == "main");
        Console.WriteLine("PASS location bar: the branch card copies the branch and shares Changes' comparison target");
    }

    private static void Chrome(string directory)
    {
        using var app = OpenFixtureProject(directory);
        Switch(app, app.Root);
        void DoubleClick(Control target, double x)
        {
            var point = target.TranslatePoint(new Point(x, target.Bounds.Height / 2), app.Window)!.Value;
            app.Window.MouseDown(point, MouseButton.Left); app.Window.MouseUp(point, MouseButton.Left);
            app.Window.MouseDown(point, MouseButton.Left); app.Window.MouseUp(point, MouseButton.Left);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        var segment = app.Find<Border>("BranchSegment");
        DoubleClick(segment, 1);
        Require(app.Window.WindowState == WindowState.Maximized, "A segment's hairline and caption stay part of the title bar's drag region.");
        app.Window.WindowState = WindowState.Normal;
        Settle();
        var pill = app.Find<Button>("ScopeBranch");
        DoubleClick(pill, pill.Bounds.Width / 2);
        Require(app.Window.WindowState == WindowState.Normal, "A pill must not act as title-bar chrome.");
        ((Flyout)pill.Flyout!).Hide();

        var widths = new List<(bool Project, bool Workspace, bool Branch)>();
        for (var width = 900; width >= 390; width -= 30)
        {
            app.Window.Width = width; Settle(60);
            widths.Add((Shown(app, "ProjectSegment"), Shown(app, "WorkspaceSegment"), Shown(app, "BranchSegment")));
        }
        Require(widths[0] == (true, true, true) && widths.All(state => state.Workspace) && widths.Any(state => state.Project && !state.Branch) &&
            !widths.Any(state => state.Branch && !state.Project) && widths[^1] == (false, true, false),
            "As the window narrows the branch yields before the project and the workspace stays: " + string.Join(" ", widths));
        var bar = app.Find<Control>("LocationBar");
        var settings = app.Find<Button>("SettingsButton");
        Require(bar.TranslatePoint(new Point(bar.Bounds.Width, 0), app.Window)!.Value.X <= settings.TranslatePoint(default, app.Window)!.Value.X,
            "The location bar never runs under the header's actions.");
        Console.WriteLine("PASS location bar: pills opt out of window dragging and segments drop in order as the header narrows");
    }
}