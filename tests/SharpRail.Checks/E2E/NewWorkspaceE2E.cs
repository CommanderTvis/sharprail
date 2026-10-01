using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;

using SharpRail.UI.State;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

internal static class NewWorkspaceE2E
{
    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "new-workspace-git"));
        ListsAndCreates(Path.Combine(root, "new-workspace-dialog"));
        EditedName(Path.Combine(root, "new-workspace-name"));
        FolderMode(Path.Combine(root, "new-workspace-folder"));
        EnterCreates(Path.Combine(root, "new-workspace-enter"));
        FetchFailure(Path.Combine(root, "new-workspace-fetch-failure"));
        RemoteGroups(Path.Combine(root, "new-workspace-remotes"));
        StalePrefetch(Path.Combine(root, "new-workspace-stale"));
        MissingPrefetch(Path.Combine(root, "new-workspace-missing"));
    }

    private static (string Root, string Repo, string Origin) SeedRemoteProject(string directory, bool withUpstream = false)
    {
        var repo = Path.Combine(directory, "repo");
        var origin = Path.Combine(directory, "origin.git");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-b", "main");
        File.WriteAllText(Path.Combine(repo, "README.md"), "base\n");
        Git(repo, "add", "README.md");
        Git(repo, "commit", "-m", "base");
        Git(directory, "init", "--bare", "-b", "main", origin);
        Git(repo, "remote", "add", "origin", origin);
        Git(repo, "push", "origin", "main");
        Git(repo, "fetch", "origin");
        Git(repo, "remote", "set-head", "origin", "main");
        if (withUpstream)
        {
            var upstream = Path.Combine(directory, "upstream.git");
            Git(directory, "init", "--bare", "-b", "trunk", upstream);
            Git(repo, "remote", "add", "upstream", upstream);
            Git(repo, "push", "upstream", "main:trunk");
            Git(repo, "fetch", "upstream");
            Git(repo, "remote", "set-head", "upstream", "trunk");
        }
        return (directory, repo, origin);
    }

    private static Window OpenPickedProjectWorkspaceDialog(E2eWorkspace app, string repo)
    {
        AddProject(app, "Open project", repo);
        Until(() => app.Window.AtProjectHome && app.Window.ProjectRoot == repo && Buttons(app).Any(button => button.Name == "ProjectName"));
        return OpenNewWorkspaceDialog(app);
    }

    private static string? RefOid(string repo, string reference)
    {
        try { return Git(repo, "rev-parse", "--verify", reference); }
        catch (InvalidOperationException) { return null; }
    }

    private static string Heading(Window dialog) => Named<TextBlock>(dialog, "DialogHeading").Text!;
    private static string Description(Window dialog) => Named<TextBlock>(dialog, "DialogExplanation").Text!;
    private static Button Create(Window dialog) => Named<Button>(dialog, "WsCreate");
    private static Button Picker(Window dialog) => Named<Button>(dialog, "WsBranchPicker");

    private static StackPanel OpenBranchPicker(E2eWorkspace app, Window dialog)
    {
        var picker = Picker(dialog);
        app.Click(picker, freshGesture: false);
        Until(() => picker.Flyout is Flyout { IsOpen: true });
        return (StackPanel)((StackPanel)((Flyout)picker.Flyout!).Content!).Children.OfType<ScrollViewer>().Single().Content!;
    }

    private static TextBox Search(E2eWorkspace app, Window dialog) =>
        ((StackPanel)((Flyout)Picker(dialog).Flyout!).Content!).Children.OfType<TextBox>().Single();

    private static Button[] Options(StackPanel options) => options.Children.OfType<Button>().Where(button => button.Name == "BranchOption").ToArray();

    private static void ListsAndCreates(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var dialog = OpenNewWorkspaceDialog(app);
        Require(Heading(dialog) == "Start work" && Description(dialog).Contains("A separate git worktree on its own new branch", StringComparison.Ordinal),
            "The dialog must explain a worktree workspace under its constant title.");
        Require(Named<ToggleButton>(dialog, "WsTargetWorktree").IsChecked == true, "The worktree target is the default.");
        var presenter = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(Named<ToggleButton>(dialog, "WsTargetWorktree"))
            .OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(item => item.Name == "PART_ContentPresenter");
        Require(Equals(presenter.Background, SharpRail.Plugins.UI.Kit.Ui.Hover),
            $"The checked target is filled with the hover surface, not the accent its muted label cannot be read on (was {presenter.Background}).");
        app.Click(Named<ToggleButton>(dialog, "WsTargetDefault"), freshGesture: false);
        Require(Heading(dialog) == "Start work" && Description(dialog).Contains("No isolation", StringComparison.Ordinal) &&
            !Picker(dialog).IsVisible && Text(Create(dialog)).Contains("Start", StringComparison.Ordinal),
            "The project-folder target has no branch picker and starts in place.");
        var current = Named<TextBlock>(dialog, "WsCurrentBranch");
        Require(current.IsVisible && current.Text == "On main", "Folder mode must say which branch the work lands on.");
        app.Click(Named<ToggleButton>(dialog, "WsTargetWorktree"), freshGesture: false);
        Require(Heading(dialog) == "Start work" && Description(dialog).Contains("A separate git worktree on its own new branch", StringComparison.Ordinal) &&
            Picker(dialog).IsVisible && !current.IsVisible && Text(Create(dialog)).Contains("Create", StringComparison.Ordinal),
            "Returning to the worktree target restores the branch picker.");
        Require(Text(Named<StackPanel>(dialog, "WsProjectPicker")).Contains("sample-project", StringComparison.Ordinal), "The dialog names its project.");
        Require(Named<TextBox>(dialog, "WsName").Text == "workspace-1", "The name field shows the host's next free name.");
        Require(Text(Picker(dialog)).Contains("From", StringComparison.Ordinal) && Text(Picker(dialog)).Contains("main", StringComparison.Ordinal),
            "The branch picker starts from main.");

        var options = OpenBranchPicker(app, dialog);
        var main = Options(options).SingleOrDefault(option => Equals(option.Tag, "main"));
        Require(main is not null && Text(main).Contains("default", StringComparison.Ordinal), "main is listed as the default branch.");
        Require(!Options(options).Any(option => Equals(option.Tag, "origin")), "No stray origin entry may be listed.");
        var search = Search(app, dialog);
        Require(search.PlaceholderText == "Search branches…", "The branch search must be labelled.");
        search.Text = "zzz-no-such-branch";
        Until(() => Options(options).Length == 0 && Text(options).Contains("No branches found.", StringComparison.Ordinal));
        search.Text = "main";
        Until(() => Options(options).Any(option => Equals(option.Tag, "main")));
        Press(search, Avalonia.Input.Key.Escape);
        Until(() => Picker(dialog).Flyout is Flyout { IsOpen: false });
        Require(dialog.IsVisible, "Escape in the branch picker closes only the picker.");
        Press(dialog, Avalonia.Input.Key.Escape);
        Until(() => !app.Window.OwnedWindows.Any());
        Require(!WorktreePaths(app).Any(), "Cancelling must not create a workspace.");

        dialog = OpenNewWorkspaceDialog(app);
        app.Click(Create(dialog));
        Until(() => !app.Window.OwnedWindows.Any() && WorktreePaths(app).Count() == 1);
        var workspace = WorktreePaths(app).Single();
        Until(() => Active(app, workspace));
        Require(app.Find<TextBlock>("ProjectLabel").Text == "sample-project" && app.Find<TextBlock>("WorkspaceLabel").Text == "workspace-1",
            "The scope context names the project and the new workspace.");
        Until(() => Text(app.Find<StackPanel>("WorkspacePlaceholder")).Contains("from main", StringComparison.Ordinal));
        var tip = ToolTip.GetTip(app.Find<TextBlock>("WorkspaceReadyBranch")) as string ?? "";
        Require(tip.Contains("cut from main", StringComparison.Ordinal) && tip.Contains("measured against it", StringComparison.Ordinal),
            "Hovering \"from main\" must say what it means.");
        Console.WriteLine("PASS upstream new-workspace.spec.ts: the dialog lists local branches (no stray origin) and creates a worktree");
    }

    private static void EditedName(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var dialog = OpenNewWorkspaceDialog(app);
        var name = Named<TextBox>(dialog, "WsName");
        Require(name.Text == "workspace-1", "The name field starts on the suggestion.");
        name.Text = "Login Rework";
        app.Click(Create(dialog), freshGesture: false);
        Until(() => !app.Window.OwnedWindows.Any() && WorktreePaths(app).Count() == 1);
        Until(() => app.Find<TextBlock>("WorkspaceLabel").Text == "Login Rework");
        Require(Git(WorktreePaths(app).Single(), "branch", "--show-current") == "login-rework",
            "The entered workspace name must also name its Git branch.");
        dialog = OpenNewWorkspaceDialog(app);
        Require(Named<TextBox>(dialog, "WsName").Text == "workspace-2", "The next dialog suggests the next free name.");
        Named<TextBox>(dialog, "WsName").Text = "Login Rework!";
        app.Click(Create(dialog), freshGesture: false);
        Until(() => !app.Window.OwnedWindows.Any() && WorktreePaths(app).Count() == 2);
        Until(() => app.Find<TextBlock>("WorkspaceLabel").Text == "Login Rework!");
        Require(Git(app.Window.WorkspaceRoot, "branch", "--show-current") == "login-rework-2",
            "Names that normalize to an existing branch must receive a unique suffix.");
        Console.WriteLine("PASS upstream new-workspace.spec.ts: an edited name names the worktree, and the placeholder leaves naming to the host");
    }

    private static void FolderMode(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var dialog = OpenNewWorkspaceDialog(app);
        app.Click(Named<ToggleButton>(dialog, "WsTargetDefault"), freshGesture: false);
        Require(Text(Create(dialog)).Contains("Start", StringComparison.Ordinal), "The folder target starts in place.");
        app.Click(Create(dialog), freshGesture: false);
        Until(() => !app.Window.OwnedWindows.Any() && Active(app, app.Root) && !HasWelcome(app));
        Require(!WorktreePaths(app).Any(), "Starting in the project folder must not create a worktree.");
        Console.WriteLine("PASS upstream new-workspace.spec.ts: folder-mode Start with an empty prompt lands in a fresh chat in the Default workspace");
    }

    private static void EnterCreates(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var dialog = OpenNewWorkspaceDialog(app);
        Press(dialog, Avalonia.Input.Key.Enter);
        Until(() => !app.Window.OwnedWindows.Any() && WorktreePaths(app).Count() == 1);
        Until(() => Active(app, WorktreePaths(app).Single()));
        Console.WriteLine("PASS upstream new-workspace.spec.ts: Enter in the prompt creates; Shift+Enter inserts a newline");
    }

    private static void FetchFailure(string directory)
    {
        var source = IsolatedGit.Repository(Path.Combine(directory, "source"));
        var remote = Path.Combine(directory, "dangling-head-remote.git");
        var repo = Path.Combine(directory, "dangling-head-fixture");
        Directory.CreateDirectory(remote);
        Git(remote, "init", "--bare", "-b", "main");
        Git(source, "push", remote, "main");
        Git(directory, "clone", remote, repo);
        Git(repo, "remote", "set-head", "origin", "main");
        Git(repo, "update-ref", "-d", "refs/remotes/origin/main");
        Directory.Delete(remote, true);
        using var app = OpenFresh(directory);
        var dialog = OpenPickedProjectWorkspaceDialog(app, repo);
        Require(Text(Picker(dialog)).Contains("origin/main", StringComparison.Ordinal), "The default base is the remote default branch.");
        var cached = Git(repo, "rev-parse", "refs/remotes/origin/main");
        app.Click(Create(dialog));
        Until(() => !app.Window.OwnedWindows.Any() && WorktreePaths(app).Count() == 1);
        var workspace = WorktreePaths(app).Single();
        Until(() => Active(app, workspace));
        Require(Git(workspace, "rev-parse", "HEAD") == cached,
            "A failed fetch must still create from a tracking ref already present locally.");
        Console.WriteLine("PASS workspace creation uses the cached remote base after a failed fetch");
    }

    private static void RemoteGroups(string directory)
    {
        var (_, repo, _) = SeedRemoteProject(directory, true);
        var app = OpenFresh(directory);
        var dialog = OpenPickedProjectWorkspaceDialog(app, repo);
        var options = OpenBranchPicker(app, dialog);
        var headings = options.Children.OfType<TextBlock>().Where(text => text.Name == "BranchGroup").Select(text => text.Text).ToArray();
        Button RemoteToggle(string remote) => options.Children.OfType<Button>().Single(button => button.Name == "RemoteGroupToggle" && Equals(button.Tag, remote));
        Require(headings.Contains("Remote") && headings.Contains("Local") && RemoteToggle("origin") is not null && RemoteToggle("upstream") is not null,
            "The picker groups branches under Local, Remote and each host-supplied remote: " + string.Join(",", headings));
        app.Click(RemoteToggle("upstream"), freshGesture: false);
        Until(() => !Options(options).Any(option => Equals(option.Tag, "upstream/trunk")));
        Picker(dialog).Flyout!.Hide();
        options = OpenBranchPicker(app, dialog);
        Require(!Options(options).Any(option => Equals(option.Tag, "upstream/trunk")) && Options(options).Any(option => Equals(option.Tag, "origin/main")),
            "A collapsed remote stays collapsed when the picker reopens, and only that remote collapses.");
        app.Click(RemoteToggle("upstream"), freshGesture: false);
        Until(() => Options(options).Any(option => Equals(option.Tag, "upstream/trunk")));
        app.Click(RemoteToggle("origin"), freshGesture: false);
        Until(() => !Options(options).Any(option => Equals(option.Tag, "origin/main")));
        Require(app.Workbench.Profile.Data.CollapsedRemotes.SetEquals(["origin"]), "The collapsed remotes are remembered in the profile.");
        app.Click(RemoteToggle("origin"), freshGesture: false);
        Until(() => Options(options).Any(option => Equals(option.Tag, "origin/main")));
        var origin = Options(options).Single(option => Equals(option.Tag, "origin/main"));
        Require(Text(origin).Contains("main", StringComparison.Ordinal) && !Text(origin).Contains("origin/", StringComparison.Ordinal),
            "Remote options show the branch name under their remote.");
        var upstream = Options(options).Single(option => Equals(option.Tag, "upstream/trunk"));
        Require(Text(upstream).Contains("trunk", StringComparison.Ordinal) && !Text(upstream).Contains("upstream/", StringComparison.Ordinal),
            "Remote options show the branch name under their remote.");
        Search(app, dialog).Text = "upstream/trunk";
        Until(() => Options(options).Length == 1);
        app.Click(Options(options).Single(), freshGesture: false);
        Until(() => Picker(dialog).Flyout is Flyout { IsOpen: false } && Text(Picker(dialog)).Contains("upstream/trunk", StringComparison.Ordinal));
        var workspace = CreateWorkspaceViaDialog(app);
        Require(Git(workspace, "rev-parse", "HEAD") == Git(repo, "rev-parse", "refs/remotes/upstream/trunk"),
            "The workspace must start from the selected ref.");
        app.Dispose();
        Require(new ProfileStore(Path.Combine(directory, "scratch") + "-profile").Data.GitSelections[workspace].Target == "upstream/trunk",
            "The workspace must record its base.");
        Console.WriteLine("PASS upstream new-workspace.spec.ts: the branch picker groups by host-supplied remotes and creates from the selected ref; branch-list.spec.ts: a remote group collapses and stays collapsed");
    }

    private static void StalePrefetch(string directory)
    {
        var (root, repo, origin) = SeedRemoteProject(directory);
        var oldSha = Git(repo, "rev-parse", "refs/remotes/origin/main");
        var writer = Path.Combine(root, "writer");
        Git(root, "clone", origin, writer);
        File.WriteAllText(Path.Combine(writer, "README.md"), "new\n");
        Git(writer, "add", "README.md");
        Git(writer, "commit", "-m", "new");
        Git(writer, "push", "origin", "main");
        var newSha = Git(writer, "rev-parse", "HEAD");
        Require(newSha != oldSha, "The writer must advance origin.");
        using var app = OpenFresh(directory);
        var dialog = OpenPickedProjectWorkspaceDialog(app, repo);
        Require(Text(Picker(dialog)).Contains("origin/main", StringComparison.Ordinal), "The default base is origin/main.");
        Until(() => RefOid(repo, "refs/remotes/origin/main") == newSha);
        Press(dialog, Avalonia.Input.Key.Escape);
        Console.WriteLine("PASS upstream new-workspace.spec.ts: opening New Workspace prefetches a stale default before create");
    }

    private static void MissingPrefetch(string directory)
    {
        var (_, repo, _) = SeedRemoteProject(directory);
        var expected = Git(repo, "rev-parse", "HEAD");
        Git(repo, "update-ref", "-d", "refs/remotes/origin/main");
        using var app = OpenFresh(directory);
        var dialog = OpenPickedProjectWorkspaceDialog(app, repo);
        Require(Text(Picker(dialog)).Contains("origin/main", StringComparison.Ordinal), "The default base is origin/main.");
        Until(() => RefOid(repo, "refs/remotes/origin/main") == expected);
        Press(dialog, Avalonia.Input.Key.Escape);
        Console.WriteLine("PASS upstream new-workspace.spec.ts: opening New Workspace prefetches a missing default tracking ref");
    }
}