using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

using SharpRail.Checks.E2E;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks;

/// <summary>The header's captioned Project and Branch segments, after upstream's shell/locationBar unit tests.</summary>
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
        Until(() => button.ContextMenu!.IsOpen && button.Classes.Contains("open"));
        return button.ContextMenu!;
    }

    private static bool Shown(E2eWorkspace app, string segment)
    {
        var border = app.Find<Border>(segment);
        return border.IsVisible && border.Bounds.Width > 0;
    }

    internal static void Switch(E2eWorkspace app, string workspace)
    {
        app.Click(Select(app, workspace));
        Until(() => Active(app, workspace) && Shown(app, "BranchSegment") && Label(app, "BranchLabel").Length > 0);
    }

    private static string Label(E2eWorkspace app, string name) => app.Find<TextBlock>(name).Text ?? "";

    private static void Switchers(string directory)
    {
        using var app = OpenFixtureProject(directory);
        Require(Shown(app, "ProjectSegment") && !Shown(app, "BranchSegment") && !Shown(app, "PullRequestSegment"),
            "Project Home shows the Project segment and no Branch segment.");
        Require(Label(app, "ProjectLabel") == "sample-project", "Project Home names the project.");
        var captions = app.Find<SharpRail.UI.LocationStrip>("LocationBar").GetLogicalDescendants().OfType<TextBlock>()
            .Where(text => text.Classes.Contains("location-caption") && text.Name is null).Select(text => text.Text);
        Require(captions.SequenceEqual(["Project", "Branch", "Pull request"]), "Each segment carries its sentence-case caption.");
        Require(new[] { "ProjectSegment", "BranchSegment" }.All(name => app.Find<Border>(name).BorderThickness == default),
            "Segments are separated by space alone, without hairlines.");
        Require(new[] { "ProjectChevron", "BranchChevron" }.All(name => app.Find<ContentControl>(name).Opacity == 0),
            "A pill's chevron is hidden at rest.");
        Require(Label(app, "ProjectInitial") == "S" && app.Find<Button>("ScopeProject").Foreground == Ui.Muted && app.Find<Button>("ScopeBranch").FontSize == Typography.TitleSection.FontSize,
            "The project tile shows the name's initial and every value rests muted in the section title style.");

        var projects = OpenMenu(app, "ScopeProject");
        var option = projects.Items.OfType<MenuItem>().Single(item => item.Name == "ScopeProjectOption");
        Require(option.IsChecked && Equals(option.Tag, app.Root) && MenuEntry(projects, "ScopeProjectHome") is { IsEnabled: false } &&
            MenuEntry(projects, "ScopeProjectAdd") is { IsEnabled: true },
            "The project switcher checks the current project, disables Project home there and offers Add project.");
        CloseMenu(projects);

        Switch(app, app.Root);
        Until(() => Shown(app, "BranchSegment") && Label(app, "BranchLabel") == "main");
        Require(Label(app, "ScopeBase").Length == 0, "The Default workspace's branch caption is plain.");

        projects = OpenMenu(app, "ScopeProject");
        Require(MenuEntry(projects, "ScopeProjectHome") is { IsEnabled: true }, "Project home is reachable from a workspace.");
        Choose(app, projects, "ScopeProjectHome");
        Until(() => app.Window.AtProjectHome && !Shown(app, "BranchSegment"));
        Console.WriteLine("PASS location bar: the project switcher and the rail move between Project Home and workspaces");
    }

    private static void SharedActions(string directory)
    {
        var app = OpenFixtureProject(directory);
        var workspace = CreateWorkspaceViaDialog(app);
        var name = Path.GetFileName(workspace);
        var ready = app.Window.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "WorkspaceReadyBranch").Text!;
        var from = ready.Contains(" · from ", StringComparison.Ordinal) ? "· from " + ready[(ready.IndexOf(" · from ", StringComparison.Ordinal) + 8)..] : "";
        Require(Label(app, "ScopeBase") == from, "A managed workspace names what it was cut from.");

        var menu = OpenWorkspaceMenu(app, workspace);
        Require(MenuEntry(menu, "WorkspaceCopyLink") is { IsEnabled: true },
            "The active workspace's menu offers Copy link, which moved there from the header.");
        CloseMenu(menu);
        menu = OpenWorkspaceMenu(app, app.Root);
        Require(MenuEntry(menu, "WorkspaceCopyLink") is { IsEnabled: false }, "Copy link applies to the active workspace only.");
        CloseMenu(menu);

        Choose(app, OpenWorkspaceMenu(app, workspace), "WorkspaceRemove");
        var confirm = Dialog(app);
        Require(Text(confirm).Contains(workspace, StringComparison.Ordinal), "The Remove dialog names the workspace it was opened for.");
        _ = app.Window.OpenProjectHomeAsync(app.Root);
        Until(() => app.Window.AtProjectHome && SharpRail.Plugins.UI.Kit.DialogWindow.On(app.Window).Count == 0);
        Settle();
        Require(Directory.Exists(workspace) && WorktreePaths(app).Contains(workspace),
            "A Remove dialog dismissed by a workspace change removes nothing.");
        app.Dispose();
        Console.WriteLine("PASS location bar: the rail's workspace menu carries Copy link and removal bound to its workspace");
    }

    private static void BranchCard(string directory)
    {
        using var app = OpenFixtureProject(directory);
        Git(app.Root, "branch", "compare-target");
        Switch(app, app.Root);
        app.ShowTool("changes");
        Until(() => Buttons(app).Any(button => button.Name == "ChangesBranch"));
        // Upstream #771: Remote is present for a branch even without an origin; the provider's Pull request item hides itself on a plain remote (W28).
        Until(() => app.Find<StackPanel>("RemoteRow").Children.Count > 0);
        Require(app.Find<StackPanel>("RemoteRow").GetVisualAncestors().Contains(app.Find<Border>("BranchSegment")), "The sync state lives in the Branch segment.");
        Require(app.Find<TextBlock>("RemoteState").Text == "No remote" && !app.Find<Border>("PullRequestSegment").IsVisible &&
            !Buttons(app).Any(button => button.Name?.StartsWith("Remote", StringComparison.Ordinal) == true),
            "Without an origin the header says so and offers no push.");
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

        var widths = new List<(bool Project, bool Branch)>();
        for (var width = 900; width >= 390; width -= 30)
        {
            app.Window.Width = width; Settle(60);
            widths.Add((Shown(app, "ProjectSegment"), Shown(app, "BranchSegment")));
        }
        Require(widths[0] == (true, true) && widths.Any(state => state.Project && !state.Branch) && !widths.Any(state => state.Branch && !state.Project),
            "As the window narrows the branch yields before the project: " + string.Join(" ", widths));
        var bar = app.Find<Control>("LocationBar");
        var settings = app.Find<Button>("SettingsButton");
        Require(bar.TranslatePoint(new Point(bar.Bounds.Width, 0), app.Window)!.Value.X <= settings.TranslatePoint(default, app.Window)!.Value.X,
            "The location bar never runs under the header's actions.");
        app.Window.Width = 1200; Settle(60);
        double Left(Control control) => control.TranslatePoint(default, app.Window)!.Value.X;
        var fields = new List<(string Segment, Control Icon)>
        {
            ("ProjectSegment", app.Window.GetLogicalDescendants().OfType<Border>().First(border => border.Classes.Contains("location-initial"))),
            ("BranchSegment", app.Find<ContentControl>("BranchIcon"))
        };
        if (app.Window.GetLogicalDescendants().OfType<ContentControl>().FirstOrDefault(control => control.Name == "PullRequestIcon") is { } prIcon)
            fields.Add(("PullRequestSegment", prIcon));
        foreach (var (segmentName, icon) in fields)
        {
            var caption = app.Find<Border>(segmentName).GetLogicalDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("location-caption"));
            Require(Math.Abs(Left(caption) - Left(icon)) < 0.5 && Math.Abs(icon.Bounds.Width - LayoutRoles.LocationIconSlot) < 0.5,
                $"{segmentName}'s caption and its value's icon slot share one left edge and one width.");
        }
        Console.WriteLine("PASS location bar: pills opt out of window dragging and segments drop in order as the header narrows");
    }
}