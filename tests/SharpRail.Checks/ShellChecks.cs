using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.Checks.E2E;
using SharpRail.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>The window shell's own checks: the header's location bar and application-level behaviour.</summary>
internal static class ShellChecks
{
    internal static void Run(string root)
    {
        LocationBarChecks.Run(root);
        Menu(Path.Combine(root, "shell-menu"));
        Chrome(Path.Combine(root, "shell-chrome"));
        RegionErrors();
        ArrangementIsolation(Path.Combine(root, "shell-isolation"));
        Navigation(Path.Combine(root, "shell-navigation"));
        ProjectPicker(Path.Combine(root, "shell-project-picker"));
        CommitMenu(Path.Combine(root, "shell-commit-menu"));
        InertLinks(Path.Combine(root, "shell-inert-links"));
    }

    private static void ProjectPicker(string directory)
    {
        using var git = new IsolatedGit(directory + "-git");
        using var app = WorkspaceFixture.OpenFixtureProject(directory);
        var first = app.Root;
        var single = WorkspaceFixture.OpenNewWorkspaceDialog(app);
        Require(!single.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "WsProjectTrigger") &&
            WorkspaceFixture.Text(WorkspaceFixture.Named<StackPanel>(single, "WsProjectPicker")).Contains("sample-project", StringComparison.Ordinal),
            "With one open project the dialog names it and offers no picker.");
        WorkspaceFixture.Press(single, Key.Escape);
        Until(() => !app.Window.OwnedWindows.Any());

        var second = IsolatedGit.Repository(Path.Combine(directory, "second-project"));
        WorkspaceFixture.Git(second, "branch", "only-in-second");
        WorkspaceFixture.AddProject(app, "Open project", second);
        Until(() => app.Window.AtProjectHome && app.Window.ProjectRoot == second && app.Window.WorkspaceMounted);
        WorkspaceFixture.GoProjectHome(app, first);
        Until(() => app.Window.ProjectRoot == first);

        var dialog = WorkspaceFixture.OpenNewWorkspaceDialog(app);
        var trigger = WorkspaceFixture.Named<Button>(dialog, "WsProjectTrigger");
        string Shown() => WorkspaceFixture.Text(WorkspaceFixture.Named<StackPanel>(dialog, "WsProjectPicker"));
        Require(Shown().Contains("sample-project", StringComparison.Ordinal), "The dialog opens on the window's project.");
        app.Click(trigger);
        Until(() => trigger.ContextMenu!.IsOpen);
        var options = trigger.ContextMenu!.Items.OfType<MenuItem>().ToArray();
        Require(options.Select(item => item.Tag).SequenceEqual(app.State!.Current.Projects) && options.Single(item => item.IsChecked).Tag is string current && current == first,
            "The picker lists every open project and checks the dialog's own.");
        app.Click(options.Single(item => Equals(item.Tag, second)), freshGesture: false);
        Until(() => Shown().Contains("second-project", StringComparison.Ordinal));
        Require(app.Window.ProjectRoot == first && app.Window.AtProjectHome, "Picking a project in the dialog does not move the window.");
        app.Click(WorkspaceFixture.Named<Button>(dialog, "WsBranchPicker"));
        Until(() => dialog.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "BranchOption") ||
            WorkspaceFixture.Named<Button>(dialog, "WsBranchPicker").Flyout is Avalonia.Controls.Flyout { Content: Control list } &&
            list.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "BranchOption" && button.Tag is "only-in-second"));
        WorkspaceFixture.Press(dialog, Key.Escape);
        Settle(200);
        if (!dialog.IsVisible) throw new InvalidOperationException("Escape in the branch list must return to the dialog, not dismiss it.");
        app.Click(WorkspaceFixture.Named<Button>(dialog, "WsCreate"));
        Until(() => !app.Window.OwnedWindows.Any() && app.Window.WorkspaceMounted && !app.Window.AtProjectHome && app.Window.ProjectRoot == second);
        var created = app.Window.WorkspaceRoot;
        Require(created != second && WorkspaceFixture.Git(second, "worktree", "list").Contains(created, StringComparison.Ordinal) &&
            !WorkspaceFixture.Git(first, "worktree", "list").Contains(created, StringComparison.Ordinal),
            "Create makes the workspace in the picked project and opens it there.");
        Console.WriteLine("PASS shell: the Create workspace dialog picks among open projects without moving the window");
    }

    private static void CommitMenu(string directory)
    {
        var now = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        Require(Ui.RelativeTime(now.AddSeconds(-59), now) == "just now" && Ui.RelativeTime(now.AddMinutes(-5), now) == "5m ago" &&
            Ui.RelativeTime(now.AddHours(-3), now) == "3h ago" && Ui.RelativeTime(now.AddDays(-2), now) == "2d ago" &&
            Ui.RelativeTime(now.AddMinutes(1), now) == "just now", "Relative time is measured against the given now.");

        using var git = new IsolatedGit(directory + "-git");
        using var app = WorkspaceFixture.OpenFixtureProject(directory);
        var workspace = WorkspaceFixture.CreateWorkspaceViaDialog(app);
        for (var index = 0; index < 40; index++)
            IsolatedGit.Run(workspace, "commit", "--allow-empty", "-m", $"commit {index} " + string.Concat(Enumerable.Repeat("with a long subject ", 8)));
        app.Click(app.Find<Button>("Tab_changes"));
        _ = app.Window.RefreshAsync();
        Button Scope() => WorkspaceFixture.Buttons(app).Single(button => button.Name == "ChangesScope");
        MenuItem[] Commits() => Scope().ContextMenu!.Items.OfType<MenuItem>().Where(item => item.Name?.StartsWith("ChangesCommit_", StringComparison.Ordinal) == true).ToArray();
        Until(() => WorkspaceFixture.Buttons(app).Any(button => button.Name == "ChangesScope") && Commits().Length == 40);
        Require(Commits().All(item => WorkspaceFixture.Text((Control)item.Header!).Contains(" · just now", StringComparison.Ordinal)),
            "A commit row shows how long ago it was committed.");
        var scope = Scope();
        app.Click(scope);
        Until(() => scope.ContextMenu!.IsOpen);
        var menu = scope.ContextMenu!;
        TopLevel.GetTopLevel(menu)!.UpdateLayout();
        var scroller = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(menu).OfType<ScrollViewer>().First();
        Require(menu.Bounds.Height is > 0 and <= 480 && menu.Bounds.Width <= 420, $"A long menu stays inside its bounds: {menu.Bounds.Size}.");
        Require(scroller.Extent.Height > scroller.Viewport.Height + 100 && scroller.Extent.Width <= scroller.Viewport.Width + 1,
            $"A long menu scrolls vertically and hides horizontal overflow: extent {scroller.Extent}, viewport {scroller.Viewport}.");
        WorkspaceFixture.CloseMenu(menu);
        Console.WriteLine("PASS shell: long commit menus are bounded and scroll, and commit rows carry relative time");
    }

    private static void InertLinks(string directory)
    {
        Require(MarkdownPreview.ResolveLink("docs/guide/a.md", "../b.md#part") == "docs/b.md" && MarkdownPreview.ResolveLink("docs/a.md", "./x/../c%20d.md") == "docs/c d.md" &&
            MarkdownPreview.ResolveLink("docs/a.md", "/SPEC.md") == "SPEC.md" && MarkdownPreview.ResolveLink("a.md", "#top") == "#top" &&
            MarkdownPreview.ResolveLink("a.md", "https://example.com/x") == "https://example.com/x",
            "Relative links resolve against the document inside the worktree.");
        foreach (var escaping in new[] { "../outside.md", "docs/../../outside.md", "%2E%2E/outside.md", "..\\outside.md", "docs/%zz.md", "bad%", "nul%00.md" })
            Require(MarkdownPreview.ResolveLink("a.md", escaping) is null, "A link that leaves the worktree or does not decode cannot be followed: " + escaping);

        using var app = new E2eWorkspace(directory);
        File.WriteAllText(Path.Combine(app.Root, "INERT.md"), "# Inert links\n\nSee [outside the tree](../../outside.md), [broken encoding](docs/%zz.md) and [the spec](SPEC.md).\n");
        _ = app.Window.RefreshAsync();
        Until(() => app.Find<TreeView>("FilesTree").GetLogicalDescendants().OfType<TreeViewItem>().Any(item => item.Tag is SharpRail.Host.Abstractions.ProjectFile { Path: "INERT.md" }));
        app.Open("INERT.md", true);
        var preview = app.Find<MarkdownPreview>("MarkdownPreview");
        var blocks = preview.GetLogicalDescendants().OfType<SelectableTextBlock>().ToArray();
        var links = blocks.SelectMany(text => text.Inlines?.OfType<Avalonia.Controls.Documents.InlineUIContainer>() ?? [])
            .Select(inline => inline.Child).OfType<Button>().Select(button => (button.Content as TextBlock)?.Text).ToArray();
        var runs = blocks.SelectMany(text => text.Inlines?.OfType<Avalonia.Controls.Documents.Run>() ?? []).Select(run => run.Text).ToArray();
        Require(links.SequenceEqual(["the spec"]) && runs.Contains("outside the tree") && runs.Contains("broken encoding"),
            "Only the followable link is a control; the others are plain text: " + string.Join("|", links));
        Console.WriteLine("PASS shell: links that leave the worktree or do not decode render inert");
    }

    private static void Navigation(string directory)
    {
        WindowLocation[] locations = [WindowLocation.Main, new("/srv/my project"), new("/srv/p", "/srv/p-worktrees/ws 1"),
            new("/srv/p", "/srv/p", "docs/Über 100%.md")];
        Require(locations.All(location => WindowLocation.Parse(location.Serialize()) == location) &&
            locations[3].Serialize() == "#/v1/projects/%2Fsrv%2Fp/workspaces/%2Fsrv%2Fp/resources/docs%2F%C3%9Cber%20100%25.md",
            "Every location survives its link, one encoded segment per id.");
        foreach (var invalid in new[] { "", "#/v2/projects/a", "#/v1/projects/", "#/v1/projects/a/extra", "#/v1/projects/a/workspaces/b/chats/c",
            "#/v1/projects/a/resources/b", "#/v1/projects/a/workspaces/b/resources/c/more/d", "#/v1/projects/%zz", "/v1/projects/a%" })
            Require(WindowLocation.Parse(invalid) == WindowLocation.Main, "An invalid link means Welcome: " + invalid);

        var list = new NavigationHistory();
        list.Push(locations[0]); list.Push(locations[1]); list.Push(locations[1]); list.Push(locations[2]);
        Require(list.Move(-1) == locations[1] && list.Move(-1) == locations[0] && list.Move(-1) is null && !list.CanGoBack && list.CanGoForward,
            "Back steps through distinct locations and stops at the first.");
        list.Move(1); list.Push(locations[3]);
        Require(!list.CanGoForward && list.Current == locations[3] && list.Move(-1) == locations[1], "A new navigation drops the entries ahead of it.");

        using var git = new IsolatedGit(directory + "-git");
        using var app = WorkspaceFixture.OpenFixtureProject(directory);
        var window = app.Window;
        var root = app.Root;
        var removed = WorkspaceFixture.CreateWorkspaceViaDialog(app);
        var gone = window.Location.Serialize();
        Require(window.Location == new WindowLocation(root, removed), "A workspace without a selected file is a workspace location.");
        app.Click(WorkspaceFixture.Select(app, root));
        Until(() => WorkspaceFixture.Active(app, root));
        app.Click(app.Find<Button>("Tab_files"));
        app.Open("README.md", true); app.Open("notes.txt", true);
        Require(window.Location == new WindowLocation(root, root, "notes.txt"), "The selected file is part of the location.");
        var link = window.Location.Serialize();

        string? Selected() => window.Layout.Selected(window.Layout.View.FocusedCenter)?.Path;
        _ = window.GoBackAsync();
        Until(() => Selected() == "README.md" && window.CanGoForward);
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Alt;
        void Chord(bool back)
        {
            app.Find<Button>("Tab_files").Focus();
            if (OperatingSystem.IsMacOS()) window.KeyPress(back ? Key.OemOpenBrackets : Key.OemCloseBrackets, command, back ? PhysicalKey.BracketLeft : PhysicalKey.BracketRight, null);
            else window.KeyPress(back ? Key.Left : Key.Right, command, back ? PhysicalKey.ArrowLeft : PhysicalKey.ArrowRight, null);
        }
        Chord(back: false);
        Until(() => Selected() == "notes.txt" && !window.CanGoForward);
        while (window.CanGoBack && !WorkspaceFixture.Active(app, removed)) { Chord(back: true); Settle(); Until(() => window.WorkspaceMounted); }
        Require(WorkspaceFixture.Active(app, removed), "Back reaches the workspace visited before this one.");
        _ = window.GoBackAsync();
        Until(() => window.AtProjectHome && window.WorkspaceMounted);
        Require(window.Location == new WindowLocation(root), "Back from the workspace is the Project Home it was created from.");
        _ = window.GoBackAsync();
        Until(() => WorkspaceFixture.Active(app, root) && !window.CanGoBack);
        _ = window.GoForwardAsync();
        Until(() => window.AtProjectHome && window.WorkspaceMounted);

        _ = window.NavigateAsync(link);
        Until(() => WorkspaceFixture.Active(app, root) && Selected() == "notes.txt");
        Require(!window.CanGoForward && window.CanGoBack, "Opening a link is a navigation of its own.");
        _ = window.GoBackAsync();
        Until(() => window.AtProjectHome && window.WorkspaceMounted);

        WorkspaceFixture.Git(root, "worktree", "remove", "--force", removed);
        _ = window.NavigateAsync(gone);
        Until(() => window.WorkspaceMounted && window.CanGoBack); Settle();
        Require(window.AtProjectHome && window.Location == new WindowLocation(root), "A link to a workspace that is gone lands on its Project Home.");
        _ = window.NavigateAsync("#/v1/projects/%2Fnot%2Fopen");
        Until(() => window.ShowsWelcome && !window.WorkspaceMounted && window.Location == WindowLocation.Main);
        _ = window.GoBackAsync();
        Until(() => window.AtProjectHome && window.WorkspaceMounted && window.ProjectRoot == root);
        Console.WriteLine("PASS shell: Back and Forward step through locations and links open a project, workspace or file");
    }

    private static string[] Headers(NativeMenu menu) => menu.Items.Select(item => item is NativeMenuItem entry ? entry.Header ?? "" : "-").ToArray();

    private static NativeMenuItem Entry(NativeMenu menu, string header) => menu.Items.OfType<NativeMenuItem>().Single(item => item.Header == header);

    private static void Pick(NativeMenu menu, string header)
    {
        ((INativeMenuItemExporterEventsImplBridge)Entry(menu, header)).RaiseClicked();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Menu(string directory)
    {
        using var app = new E2eWorkspace(directory);
        var box = new TextBox { Text = "hello" };
        var dialog = new Window { Content = box, Width = 240, Height = 120 };
        using (ApplicationMenu.Install(Application.Current!, app.Window.Commands)) dialog.Show();
        var bar = NativeMenu.GetMenu(dialog);
        Require(bar is not null && Headers(bar).SequenceEqual(["Edit", "Window"]), "Every window that opens gets the Edit and Window menus.");
        var edit = Entry(bar!, "Edit").Menu!;
        var windows = Entry(bar!, "Window").Menu!;
        Require(Headers(edit).SequenceEqual(["Undo", "Redo", "-", "Cut", "Copy", "Paste", "Delete", "Select All"]) &&
            Headers(windows).SequenceEqual(["Minimize", "Zoom", "Close", "-", "Bring All to Front"]),
            "The menus carry the standard editing and window commands.");
        Require(Entry(edit, "Copy").Gesture is { Key: Key.C, KeyModifiers: KeyModifiers.Meta } &&
            Entry(edit, "Redo").Gesture is { Key: Key.Z, KeyModifiers: KeyModifiers.Meta | KeyModifiers.Shift } &&
            Entry(windows, "Close").Gesture is { Key: Key.W, KeyModifiers: KeyModifiers.Meta } &&
            Entry(windows, "Minimize").Gesture is { Key: Key.M, KeyModifiers: KeyModifiers.Meta } && Entry(edit, "Delete").Gesture is null,
            "Menu items show their platform chords.");

        box.Focus();
        Pick(edit, "Select All");
        Require(box.SelectionStart == 0 && box.SelectionEnd == 5, "Select All reaches the focused text box.");
        Pick(edit, "Cut");
        Until(() => box.Text is null or "");
        var cut = dialog.Clipboard!.TryGetTextAsync();
        Until(() => cut.IsCompleted);
        Require(cut.Result == "hello", "Cut moves the selection to the clipboard.");
        Pick(edit, "Paste");
        Until(() => box.Text == "hello");
        Pick(edit, "Select All"); Pick(edit, "Delete");
        Require(box.Text is null or "", "Delete removes the selection.");

        var chords = new List<(Key Key, KeyModifiers Modifiers, string? Symbol)>();
        var other = new Button { Content = "target" };
        other.KeyDown += (_, e) => chords.Add((e.Key, e.KeyModifiers, e.KeySymbol));
        dialog.Content = other; Dispatcher.UIThread.RunJobs(); other.Focus();
        Pick(edit, "Copy"); Pick(edit, "Redo"); Pick(edit, "Delete");
        Require(chords.SequenceEqual([(Key.C, KeyModifiers.Meta, "c"), (Key.Z, KeyModifiers.Meta | KeyModifiers.Shift, "Z")]),
            "Outside a text box an editing command arrives as its chord, and Delete is not forwarded: " + string.Join(" ", chords));

        Pick(windows, "Zoom");
        Require(dialog.WindowState == WindowState.Maximized, "Zoom maximizes the window.");
        Pick(windows, "Zoom");
        Require(dialog.WindowState == WindowState.Normal, "Zoom restores a maximized window.");
        Pick(windows, "Close");
        Until(() => !dialog.IsVisible);

        var workbench = Entry(ApplicationMenu.Build(app.Window, app.Window.Commands), "Window").Menu!;
        app.Open("README.md", true);
        app.Window.Layout.Focus(app.Center);
        Pick(workbench, "Close");
        Until(() => app.Tabs.All(tab => tab.Path != "README.md"));
        Require(app.Window.IsVisible, "Close in a workbench window closes the selected tab through the app command, never the window.");
        Pick(workbench, "Minimize");
        Require(app.Window.WindowState == WindowState.Minimized, "Minimize minimizes the window.");
        app.Window.WindowState = WindowState.Normal;
        Console.WriteLine("PASS shell: the application menu routes editing and window commands to their existing paths");
    }

    private static void Chrome(string directory)
    {
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        using var app = new E2eWorkspace(directory, openFiles: false);
        var window = app.Window;
        var header = app.Find<Grid>("MainHeader");
        var inset = OperatingSystem.IsMacOS() ? 80 : 12;
        Require(header.Margin.Left == inset, "A windowed header leaves room for the native window controls.");
        window.WindowState = WindowState.FullScreen; Dispatcher.UIThread.RunJobs();
        Require(header.Margin.Left == 12, "Full screen hides the traffic lights, so the header drops their inset.");
        window.TitleBarDoubleClick(null);
        Require(window.WindowState == WindowState.FullScreen, "A title-bar double-click does nothing in full screen.");
        window.WindowState = WindowState.Normal; Dispatcher.UIThread.RunJobs();
        Require(header.Margin.Left == inset, "Leaving full screen restores the inset.");

        Require(WorkbenchWindow.TitleBarActionFor(null) == TitleBarAction.Zoom && WorkbenchWindow.TitleBarActionFor("Maximize") == TitleBarAction.Zoom &&
            WorkbenchWindow.TitleBarActionFor("Fill") == TitleBarAction.Zoom && WorkbenchWindow.TitleBarActionFor("Minimize") == TitleBarAction.Minimize &&
            WorkbenchWindow.TitleBarActionFor("None") == TitleBarAction.None, "The double-click preference maps to zoom, minimize or nothing.");
        window.TitleBarDoubleClick("None");
        Require(window.WindowState == WindowState.Normal, "A None preference leaves the window alone.");
        window.TitleBarDoubleClick("Fill");
        Require(window.WindowState == WindowState.Maximized, "Fill zooms the window.");
        window.TitleBarDoubleClick("Maximize");
        Require(window.WindowState == WindowState.Normal, "A second double-click restores it.");
        window.TitleBarDoubleClick("Minimize");
        Require(window.WindowState == WindowState.Minimized, "Minimize minimizes the window.");
        window.WindowState = WindowState.Normal; Dispatcher.UIThread.RunJobs();

        bool Zoomed(double factor) => Math.Abs(window.Preferences.Zoom - factor) < 1e-9 && Math.Abs(InterfaceZoom.Current - factor) < 1e-9;
        Require(Zoomed(1), "The zoom fixture starts at 100%.");
        window.PinchZoom(0.1); window.PinchZoom(0.1);
        Require(Zoomed(1.2), $"A pinch applies its accumulated scale to the starting factor and may rest between the steps: {window.Preferences.Zoom}.");
        window.PinchZoom(5);
        Require(Zoomed(2), "A pinch stops at 200%.");
        window.PinchZoom(-5.9);
        Require(Zoomed(0.5), "A pinch stops at 50%.");
        window.PinchZoom(0.9);
        Require(Zoomed(1.2), "Within one gesture every scale is taken against the same baseline.");
        Settle(400);
        Require(Math.Abs(new ProfileStore(app.Root + "-profile").Data.Preferences.Zoom - 1.2) < 1e-9, "The factor a pinch ends on persists.");
        window.KeyPress(Key.OemPlus, command, PhysicalKey.Equal, null); Dispatcher.UIThread.RunJobs();
        Require(Zoomed(1.25), "The zoom chords step to the adjacent factor from a value between the steps.");

        void Magnify(Control source, double delta) => source.RaiseEvent(new PointerDeltaEventArgs(InputElement.PointerTouchPadGestureMagnifyEvent, source,
            new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, true), window, default, 0, new PointerPointProperties(), KeyModifiers.None, new Vector(delta, 0)));
        Magnify(header, 0.2);
        Require(Zoomed(1.5), "A trackpad magnify gesture anywhere in the window drives the page zoom.");
        Settle(400);
        var claimed = app.Find<Border>("WindowTitleBar");
        void Claim(object? sender, PointerDeltaEventArgs e) => e.Handled = true;
        claimed.AddHandler(InputElement.PointerTouchPadGestureMagnifyEvent, Claim);
        Magnify(header, 0.2);
        claimed.RemoveHandler(InputElement.PointerTouchPadGestureMagnifyEvent, Claim);
        Require(Zoomed(1.5), "Content that claims the gesture keeps it.");
        window.KeyPress(Key.D0, command, PhysicalKey.Digit0, null); Dispatcher.UIThread.RunJobs();
        Require(Zoomed(1), "Mod+0 resets a pinched zoom.");
        Console.WriteLine("PASS shell: full-screen inset, title-bar double-click preference and pinch zoom");
    }

    private sealed class FailingLayout : Control
    {
        protected override Size MeasureOverride(Size availableSize) => throw new InvalidOperationException("measure failed");
    }

    private static void RegionErrors()
    {
        var session = new LayoutSession();
        session.SwitchWorkspace("one");
        var center = session.State.Center.Leaves().Single();
        session.Open(new("bad-build", "Bad", "file", "bad.txt"), true);
        var failing = session.State.Groups.First(group => group.Region != "center" && group.Tools.Count > 0);
        var healthy = session.State.Groups.First(group => group.Region != "center" && group.Tools.Count > 0 && group.Region != failing.Region);
        session.Visible(failing.Region, true); session.Visible(healthy.Region, true);
        var broken = failing.Tools[0].Id;
        session.Select(failing.Id, broken); session.Select(healthy.Id, healthy.Tools[0].Id);
        var surface = new DockSurface(session, tab => tab?.Id == "bad-build" ? throw new InvalidOperationException("build failed")
            : tab?.Id == broken ? new FailingLayout() : new TextBlock { Name = "Healthy", Text = tab?.Title ?? "empty" });
        var window = new Window { Content = surface, Width = 1200, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Border Body(string group) => window.GetLogicalDescendants().OfType<Border>().Single(border => border.Name == "DockBody_" + group);
        string Notice(string group) => Body(group).Child is StackPanel { Name: "RegionError" } notice
            ? string.Join("\n", notice.Children.OfType<TextBlock>().Select(text => text.Text)) : "";
        Require(Notice(center).Contains("build failed", StringComparison.Ordinal), "A body that fails to build shows its error in its own region.");
        Require(Notice(failing.Id).Contains("measure failed", StringComparison.Ordinal), "A body that fails during layout shows its error in its own region.");
        Require(window.IsVisible && Body(healthy.Id).Child is TextBlock { Name: "Healthy", Bounds.Width: > 0 }, "Sibling regions and the window carry on.");
        session.Open(new("good", "Good", "file", "good.txt"), true);
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        Require(Body(session.View.FocusedCenter).Child is TextBlock { Text: "Good" }, "The region shows the next body that works.");
        window.Close();
        Console.WriteLine("PASS shell: a failing body is contained to its own region");
    }

    private static void ArrangementIsolation(string directory)
    {
        using var app = new E2eWorkspace(directory);
        app.Open("README.md", true); app.Open("notes.txt", true);
        var surface = app.Find<DockSurface>("WorkspaceWorkbench");
        var detached = new List<string>();
        Dictionary<string, Control> Mounted(string prefix) => app.Window.GetLogicalDescendants().OfType<Border>()
            .Where(border => border.Name?.StartsWith(prefix, StringComparison.Ordinal) == true && border.Child is not null)
            .ToDictionary(border => border.Name!, border => border.Child!);
        var bodies = Mounted("DockBody_");
        var strips = Mounted("TabStrip_");
        Require(bodies.Count >= 3 && strips.Count >= bodies.Count, "The fixture mounts bodies in several groups.");
        foreach (var (name, mounted) in bodies) mounted.DetachedFromVisualTree += (_, _) => detached.Add(name);
        bool Same(Dictionary<string, Control> before, string prefix, string? except = null) =>
            Mounted(prefix) is var now && before.All(entry => entry.Key == except || ReferenceEquals(now.GetValueOrDefault(entry.Key), entry.Value));
        void Untouched(string message, string? except = null) => Require(
            detached.All(name => name == except) && Same(bodies, "DockBody_", except) && Same(strips, "TabStrip_", except),
            message + " Detached: " + string.Join(",", detached));

        var tab = app.Tab("notes.txt");
        var start = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(start); app.Window.MouseDown(start, MouseButton.Left);
        app.Window.MouseMove(start + new Vector(12, 8)); Settle();
        var body = app.Find<Border>("DockBody_" + app.Center);
        app.Window.MouseMove(body.TranslatePoint(new Point(body.Bounds.Width * .9, body.Bounds.Height / 2), app.Window)!.Value); Settle();
        Require(surface.IsDragging, "The fixture must hold an active tab drag.");
        Untouched("A tab drag and its drop preview must keep every mounted body and strip.");
        app.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        app.Window.MouseUp(start, MouseButton.Left); Settle();
        Require(!surface.IsDragging, "Escape cancels the drag.");
        Untouched("Cancelling a drag must keep every mounted body and strip.");

        var separator = app.Find<ResizeHandle>("rightSeparator");
        var grip = separator.TranslatePoint(new Point(separator.Bounds.Width / 2, separator.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(grip); app.Window.MouseDown(grip, MouseButton.Left);
        app.Window.MouseMove(grip + new Vector(-60, 0)); Settle();
        Require(separator.IsActive, "The fixture must hold an active side resize.");
        Untouched("A resize preview must keep every mounted body and strip.");
        app.Window.MouseUp(grip + new Vector(-60, 0), MouseButton.Left); Settle();
        Require(Same(bodies, "DockBody_"), "Committing a resize re-mounts the same bodies rather than rebuilding them.");
        detached.Clear(); strips = Mounted("TabStrip_");

        var centerBody = "DockBody_" + app.Center;
        app.Click(app.Tab("README.md"));
        Until(() => app.Window.Layout.Selected(app.Center)?.Path == "README.md");
        Settle();
        Require(detached.Contains(centerBody), "Selecting another tab swaps that group's own body.");
        Untouched("Selecting a tab in one group must leave sibling bodies mounted.", centerBody);
        Require(Same(strips, "TabStrip_"), "Selecting a tab must keep every group's strip, its own included.");
        detached.Clear(); bodies = Mounted("DockBody_");
        foreach (var group in app.Window.Layout.State.Groups.Where(group => !group.Folded)) app.Window.Layout.Focus(group.Id);
        app.Window.Layout.Focus(app.Center); Settle();
        Untouched("Focusing a group must leave every body and strip untouched.");
        Console.WriteLine("PASS shell: drag and resize previews, selection and focus keep sibling bodies and chrome mounted");
    }
}