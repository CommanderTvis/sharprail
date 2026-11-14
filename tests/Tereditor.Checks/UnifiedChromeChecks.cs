using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Checks.E2E;
using Tereditor.UI;
using Tereditor.UI.Docking;
using Tereditor.UI.Rendering;

using static Tereditor.Checks.E2E.E2eWorkspace;
using static Tereditor.Checks.E2E.WorkspaceFixture;

namespace Tereditor.Checks;

/// <summary>The window without a top bar: the fixed Projects rail with its header, the right bar's footer and the layout migration.</summary>
internal static class UnifiedChromeChecks
{
    internal static void Run(string root)
    {
        Migration();
        PlaygroundGuideChecks.Run(Path.Combine(root, "playground-guide"));
        ZoomedColumns(Path.Combine(root, "unified-zoom"));
        Window(Path.Combine(root, "unified-chrome"));
        Classic(Path.Combine(root, "classic-chrome"));
    }

    private static IEnumerable<MenuItem> Entries(IEnumerable<object?> items) => items.OfType<MenuItem>().SelectMany(item => new[] { item }.Concat(Entries(item.Items)));

    /// <summary>The View menu opens from its button (or the header) twice in a row, fills its entries and survives each of them being shown.</summary>
    private static void ViewMenu(E2eWorkspace app, bool unified)
    {
        var menu = app.Window.ViewMenu;
        for (var round = 0; round < 2; round++)
        {
            if (unified && !OperatingSystem.IsMacOS()) app.Click(app.Find<Button>("AppMenuButton"), freshGesture: false);
            else if (unified) app.Find<StackPanel>("RailHeader").RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            else app.Find<Grid>("MainHeader").RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
            Until(() => menu.IsOpen);
            var entries = Entries(menu.Items).ToArray();
            Require(entries.Any(item => item.Name == "NewWindow") && entries.Any(item => Equals(item.Header, "Reset frame")) && entries.Any(item => Equals(item.Header, "Toggle right")),
                $"The View menu lists its entries on round {round}: " + string.Join("|", entries.Select(item => item.Name + "/" + item.Header)));
            Require(!unified || entries.Any(item => item.Name == "AboutMenu"), "The overflow menu keeps About reachable.");
            var names = entries.Select(item => item.Name).ToList();
            var settings = entries.SingleOrDefault(item => item.Name == "SettingsMenu");
            Require(unified == (settings is not null), "Only the window without a top bar lists Settings in the overflow menu.");
            if (settings is not null)
                Require(Equals(settings.Header, "Settings…") && settings.Icon is not null && names.IndexOf("SettingsMenu") + 1 == names.IndexOf("AboutMenu"),
                    "The overflow menu holds Settings, with its icon, right before About.");
            foreach (var entry in entries.Where(item => item.Items.Count > 0)) { entry.Open(); entry.Close(); }
            menu.Close();
            Until(() => !menu.IsOpen);
        }
    }

    /// <summary>The rail header's other popups open without an exception: the project switcher, the branch card and the overflow menu's Settings.</summary>
    private static void HeaderPopups(E2eWorkspace app)
    {
        var projects = app.Find<Button>("ScopeProject").ContextMenu!;
        for (var round = 0; round < 2; round++)
        {
            app.Click(app.Find<Button>("ScopeProject"), freshGesture: false);
            Until(() => projects.IsOpen);
            Require(Entries(projects.Items).Any(item => item.Name == "ScopeProjectHome"), "The project switcher lists its entries.");
            projects.Close();
            Until(() => !projects.IsOpen);
        }
        var branch = app.Find<Button>("ScopeBranch");
        for (var round = 0; round < 2; round++)
        {
            app.Click(branch, freshGesture: false);
            Until(() => branch.Flyout!.IsOpen);
            branch.Flyout!.Hide();
            Until(() => !branch.Flyout.IsOpen);
        }
        app.OpenSettings();
        Until(() => app.Window.OpenSettingsViews.Any());
        app.Window.OpenSettingsViews.Single().Close();
    }

    /// <summary>The header's blocks share one left edge for their icon column, and their values fill the rail's width.</summary>
    // The add-project menu opens directly below its + button, left edges aligned, inside the window, never over the button, and is bounded in width.
    private static void AddMenuUnderButton(E2eWorkspace app)
    {
        var add = app.Find<Button>("AddProjectMenu");
        app.Click(add);
        Until(() => add.ContextMenu!.IsOpen);
        var menu = add.ContextMenu!;
        Until(() => menu.Bounds.Width > 0 && menu.Bounds.Height > 0);
        static (double X, double Y, double W, double H) Screen(Control control)
        {
            var top = TopLevel.GetTopLevel(control)!;
            var origin = top.PointToScreen(control.TranslatePoint(default, top)!.Value);
            return (origin.X, origin.Y, control.Bounds.Width * top.RenderScaling, control.Bounds.Height * top.RenderScaling);
        }
        var button = Screen(add);
        var popup = Screen(menu);
        var window = Screen(app.Window);
        Require(popup.Y >= button.Y + button.H - 1, $"The add-project menu starts at y={popup.Y:0.#}, above the bottom of its button ({button.Y + button.H:0.#}).");
        Require(Math.Abs(popup.X - button.X) <= 8 || popup.X + popup.W >= window.X + window.W - 1, $"The add-project menu starts at x={popup.X:0.#}, not at its button's edge x={button.X:0.#}.");
        Require(popup.X >= window.X - 1 && popup.X + popup.W <= window.X + window.W + 1 && popup.Y + popup.H <= window.Y + window.H + 1, "The add-project menu stays inside the window.");
        Require(menu.Bounds.Width <= menu.MaxWidth + 0.5, $"The add-project menu is {menu.Bounds.Width:0.#} wide, past its bound {menu.MaxWidth:0.#}.");
        menu.Close();
    }

    /// <summary>A 1 px line in the border role closes the rail header: at its bottom edge, the rail's full width, with the list right below it.</summary>
    private static void HeaderDivider(E2eWorkspace app)
    {
        Dispatcher.UIThread.RunJobs();
        app.Window.UpdateLayout();
        var top = app.Find<Border>("FixedLeftHeader");
        var body = app.Find<Control>("DockBody_" + app.Window.Layout.State.Groups.Single(group => group.Region == "left").Id);
        var group = (Control)top.Parent!;
        Require(top.BorderThickness == new Thickness(0, 0, 0, 1) && Equals(top.BorderBrush, Ui.BorderBrush), "The rail header's bottom edge is a 1 px line in the border colour.");
        Require(Math.Abs(top.Bounds.Width - group.Bounds.Width) <= 1 && Math.Abs(top.Bounds.Left) <= 1, $"The line spans the rail's full width ({top.Bounds.Width}, {group.Bounds.Width}).");
        Require(Math.Abs(top.Bounds.Bottom - body.Bounds.Top) <= 1, $"The project list starts right at the line, with no gap ({top.Bounds.Bottom}, {body.Bounds.Top}).");
    }

    /// <summary>The right tool bar has no Settings gear: its tools start at the rail's top and nothing sits below them.</summary>
    private static void RightBar(E2eWorkspace app)
    {
        Dispatcher.UIThread.RunJobs();
        app.Window.UpdateLayout();
        var rail = app.Find<Border>("rightRail");
        var entries = app.Find<StackPanel>("rightRailEntries");
        Rect Box(Control control) => new(control.TranslatePoint(default, app.Window)!.Value, control.Bounds.Size);
        Require(ReferenceEquals(rail.Child, entries) && !app.Find<Button>("SettingsButton").IsEffectivelyVisible && !rail.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "SettingsButton"),
            "The right tool bar holds only its tools: no Settings gear anywhere in the window's corner.");
        var railBox = Box(rail);
        var cluster = entries.GetLogicalDescendants().OfType<StackPanel>().First(panel => panel.Name?.StartsWith("RailCluster_", StringComparison.Ordinal) == true);
        // The rail's inset (2) and the first drop boundary (9) are all that precede the first tool.
        Require(Math.Abs(Box(cluster).Top - railBox.Top - 11) <= 1, $"The tool icons start at the top of the rail ({Box(cluster).Top - railBox.Top}).");
        var first = cluster.GetLogicalDescendants().OfType<Button>().First(button => button.IsVisible);
        Require(Math.Abs(Box(first).Top - Box(cluster).Top) <= 1, $"The first tool opens the cluster ({Box(first).Top - Box(cluster).Top}).");
    }

    /// <summary>
    /// The host's status is a captioned block like Project and Branch: absent without leaving a gap while a healthy embedded host
    /// has nothing to say, and with a status its caption, glyph and text share the project's caption edge, icon column and text column.
    /// </summary>
    private static void HostBlock(E2eWorkspace app, StackPanel header)
    {
        void Settle() { Dispatcher.UIThread.RunJobs(); app.Window.UpdateLayout(); }
        double Left(Control control) => control.TranslatePoint(default, header)!.Value.X;
        double Top(Control control) => control.TranslatePoint(default, app.Window)!.Value.Y;
        Settle();
        var host = app.Find<Grid>("HostSegment");
        var list = app.Find<Grid>("ProjectsPanel");
        var listTop = Top(list);
        Require(ReferenceEquals(host.Parent, header) && ReferenceEquals(header.Children[^1], host) && !host.IsVisible,
            "The Host block is the rail header's last block, and a healthy embedded host does not show it.");
        Require(!app.Find<ContentControl>("ConnectionIcon").Classes.Contains("turning"), "A hidden connection state turns nothing.");

        app.Window.Report(new IOException("Host replied: permission denied"));
        Settle();
        var caption = host.Children.OfType<TextBlock>().Single(text => text.Classes.Contains("location-caption"));
        var projectCaption = ((Panel)app.Find<Button>("ScopeProject").Parent!).Children.OfType<TextBlock>().Single(text => text.Classes.Contains("location-caption"));
        var dot = app.Find<ContentControl>("ConnectionIcon");
        var status = app.Find<TextBlock>("ConnectionStatus");
        var initial = app.Find<Button>("ScopeProject").GetLogicalDescendants().OfType<Border>().Single(border => border.Classes.Contains("location-initial"));
        var label = app.Find<TextBlock>("ProjectLabel");
        Require(host.IsVisible && caption.Text == "Host" && status.Text == "Error" && Math.Abs(Left(caption) - Left(projectCaption)) <= 0.5,
            $"A status shows the Host block, its sentence-case caption at the others' edge ({Left(caption)}, {Left(projectCaption)}).");
        Require(Math.Abs(Left(dot) - Left(initial)) <= 0.5 && Math.Abs(dot.Bounds.Width - initial.Bounds.Width) <= 0.5 &&
            Math.Abs(Left(status) - Left(label)) <= 0.5 && status.FontSize == Tereditor.Plugins.UI.Kit.Typography.TitleSection.FontSize,
            $"The host's glyph takes the icon column and its text the value's text column and role (glyph {Left(dot)}/{Left(initial)}, text {Left(status)}/{Left(label)}).");
        // No colour coin: each Host mark is the branch's plain glyph (same size and brush); only the Error text takes the danger colour.
        var branchGlyph = (Border)app.Find<ContentControl>("BranchIcon").Content!;
        foreach (var mark in new[] { dot, app.Find<ContentControl>("HostListeningIcon") })
            Require(mark.Content is Border { OpacityMask: not null } glyph && glyph.Width == branchGlyph.Width && glyph.Height == branchGlyph.Height &&
                ReferenceEquals(glyph.Background, branchGlyph.Background) &&
                !host.GetLogicalDescendants().Any(control => control is Avalonia.Controls.Shapes.Ellipse || control is TextBlock { Text: "●" }),
                $"The Host block's {mark.Name} is a plain glyph like the branch's, not a coloured dot.");
        Require(ReferenceEquals(status.Foreground, Tereditor.Plugins.UI.Kit.Ui.Danger), "The Host block's Error text carries the problem's colour.");
        // One glyph per state: the listener's radio tower, unplug for Error, a turning loader while reconnecting.
        Require(Equals(app.Find<ContentControl>("HostListeningIcon").Tag, "radioTower"), "The Listening mark wears the radio tower.");
        Require(Equals(dot.Tag, "unplug") && !dot.Classes.Contains("turning"), $"Error wears the unplugged glyph, still ({dot.Tag}).");
        status.Text = "Reconnecting";
        Settle();
        Require(Equals(dot.Tag, "loader") && dot.Classes.Contains("turning") && dot.Content is Border { OpacityMask: not null } loader &&
            ReferenceEquals(loader.Background, branchGlyph.Background), $"Reconnecting wears the muted loader, turning ({dot.Tag}).");
        status.Text = "Error";
        Settle();
        Require(Equals(dot.Tag, "unplug") && !dot.Classes.Contains("turning"), "The loader stops turning once the state moves on.");
        Require(Top(list) > listTop, "The Host block takes its own row above the project list.");

        var connection = app.Find<Button>("ConnectionButton");
        app.Click(connection);
        var retry = connection.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Retry"));
        retry.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        connection.ContextMenu.Close();
        Until(() => !app.Find<TextBlock>("WorkspaceError").IsVisible);
        Settle();
        Require(!host.IsVisible && Math.Abs(Top(list) - listTop) <= 0.5, $"Once the status clears the Host block leaves no gap ({Top(list)}, {listTop}).");
    }

    private static void Layout(E2eWorkspace app, StackPanel header)
    {
        // The sync state arrives after the window's last layout pass; measure what the user sees once the rail has settled.
        Dispatcher.UIThread.RunJobs();
        app.Window.UpdateLayout();
        double Left(Control control) => control.TranslatePoint(default, header)!.Value.X;
        double Right(Control control) => Left(control) + control.Bounds.Width;
        var project = app.Find<Button>("ScopeProject");
        var branch = app.Find<Button>("ScopeBranch");
        var initial = project.GetLogicalDescendants().OfType<Border>().Single(border => border.Classes.Contains("location-initial"));
        var icon = app.Find<ContentControl>("BranchIcon");
        Require(Math.Abs(Left(initial) - Left(icon)) < 0.5 && Math.Abs(Left(project) - Left(branch)) < 0.5 &&
            Math.Abs(Left(app.Find<TextBlock>("ProjectLabel")) - Left(app.Find<TextBlock>("BranchLabel"))) < 0.5,
            "The project and branch values share one icon column and one text column.");
        var columns = app.Find<Grid>("BranchColumns");
        var remoteBlock = app.Find<Grid>("RemoteBlock");
        // The value row ends one segment inset (8) before the rail's edge.
        var rowEnd = Right(columns);
        Require(Math.Abs(header.Bounds.Width - 8 - rowEnd) <= 1, $"The branch row ends one inset before the rail's edge ({rowEnd}, {header.Bounds.Width}).");
        var branchBlock = (Control)branch.Parent!;
        if (remoteBlock.IsVisible)
        {
            // With a remote status the row is two equal columns an 8 gap apart: Branch, then Remote with its own caption.
            Require(Math.Abs(Right(remoteBlock) - rowEnd) <= 1, $"The Remote block sits flush at the right end of the branch row ({Right(remoteBlock)}, {rowEnd}; column {Grid.GetColumn(remoteBlock)} of {columns.ColumnDefinitions.Count}; remote {Left(remoteBlock)}+{remoteBlock.Bounds.Width}, branch {Left(branchBlock)}+{branchBlock.Bounds.Width}, columns {Left(columns)}+{columns.Bounds.Width}, widths {string.Join("/", columns.ColumnDefinitions.Select(column => column.ActualWidth))}).");
            Require(Math.Abs(branchBlock.Bounds.Width - remoteBlock.Bounds.Width) <= 1 && Math.Abs(Left(remoteBlock) - Right(branchBlock) - 8) <= 1,
                $"The Branch and Remote blocks are equal columns with an 8 gap ({branchBlock.Bounds.Width}, {remoteBlock.Bounds.Width}).");
            var caption = remoteBlock.GetLogicalDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("location-caption"));
            Require(caption.Text == "Remote" && Math.Abs(Left(caption) - Left(app.Find<ContentControl>("RemoteIcon"))) <= 1, "The Remote block has its caption, and its content starts at the caption's left edge.");
            Require(app.Find<Avalonia.Controls.DockPanel>("RemoteRow").GetLogicalChildren().OfType<Control>().All(part => Right(part) <= Right(remoteBlock) + 0.5), "The Remote content never overflows its block.");
        }
        else Require(Math.Abs(Right(branch) - rowEnd) <= 1, $"Without a remote status the branch block takes the full row ({Right(branch)}, {rowEnd}).");
        Require(!OperatingSystem.IsMacOS() || Math.Abs(Right(project) - rowEnd) <= 1 && project.Bounds.Width > header.Bounds.Width / 2, "The project value fills the rail's width.");
        var credits = app.Window.GetLogicalDescendants().OfType<Border>().FirstOrDefault(border => border.Name == "CreditsSegment");
        if (credits is { IsVisible: true })
            Require(credits.GetLogicalDescendants().OfType<Button>().Where(button => button.IsVisible).All(button => Math.Abs(Right(button) - rowEnd) <= 1), "The credits value fills the row like the others.");
        var more = app.Window.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => button.Name == "AppMenuButton");
        Require(OperatingSystem.IsMacOS() == (more is null), "The three-dots button is drawn in the browser only: a Mac has the native application menu.");
        Require(Math.Abs(header.Bounds.Width - Right(more ?? app.Find<Button>("AddProjectMenu"))) <= 1 || !OperatingSystem.IsMacOS(), "The last button of the first row ends at the rail's right edge.");
    }

    /// <summary>
    /// At every page zoom in a fixed window the Projects rail stays (never narrower than its minimum, so the traffic lights keep their
    /// inset and nothing sits under them), the main column keeps 320, and the right side gives way first: it shrinks to its minimum or
    /// folds away before the rail shrinks, and never grows back as the zoom rises.
    /// </summary>
    private static void ZoomedColumns(string directory)
    {
        UnifiedChrome.Override = true;
        using var app = OpenFixtureProject(directory);
        var window = app.Window;
        var (width, height) = (window.Width, window.Height);
        var previous = double.PositiveInfinity;
        try
        {
            window.Width = 1200; window.Height = 800;
            foreach (var zoom in InterfaceZoom.Factors.Where(factor => factor >= 1))
            {
                InterfaceZoom.Apply(Application.Current!, zoom);
                for (var round = 0; round < 3; round++) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
                var surface = app.Find<DockSurface>("WorkspaceWorkbench");
                var rail = app.Find<Border>("AuxiliaryRegion_left");
                var centre = app.Find<Border>("CenterRegion");
                var right = app.Find<Border>("AuxiliaryRegion_right");
                var shell = surface.Bounds.Width - DockSurface.RailWidth;
                var step = $"at {zoom:0.##}x ({shell:0} wide: rail {rail.Bounds.Width:0.#}, centre {centre.Bounds.Width:0.#}, right {right.Bounds.Width:0.#})";
                var minimum = Math.Min(.4 * shell, SideGeometry.MinPaneWidth);
                Require(rail.IsVisible && rail.Bounds.Width >= minimum - 1, $"The Projects rail keeps its minimum {step}.");
                Require(rail.Bounds.Width * zoom >= 80 - 1, $"The rail holds the traffic lights' inset {step}.");
                var inset = centre.TranslatePoint(default, surface)!.Value.X;
                Require(inset >= rail.Bounds.Width - 1, $"Nothing of the main column starts inside the rail's strip {step}.");
                Require(centre.Bounds.Width >= 320 - 1, $"The main column keeps 320 {step}.");
                var side = right.IsVisible ? right.Bounds.Width : 0;
                Require(side <= 1 || side >= SideGeometry.MinPaneWidth - 1, $"The right side is at its minimum or folded away, never a sliver {step}.");
                Require(side <= previous + 1, $"The right side does not grow as the zoom rises {step}.");
                var wanted = window.Layout.State.LeftWidth * shell;
                Require(rail.Bounds.Width >= wanted - 1 || side <= 1, $"The rail only shrinks below its width after the right side folded {step}.");
                previous = side;
            }
        }
        finally
        {
            InterfaceZoom.Apply(Application.Current!, 1);
            window.Width = width; window.Height = height;
            Dispatcher.UIThread.RunJobs();
            UnifiedChrome.Override = false;
        }
        Console.WriteLine("PASS unified chrome: at every zoom the rail stays, the main column keeps 320 and the right side folds first");
    }

    private static void Classic(string directory)
    {
        UnifiedChrome.Override = false;
        using var app = OpenFixtureProject(directory);
        ViewMenu(app, unified: false);
        Console.WriteLine("PASS classic chrome: the View menu opens twice from the top bar");
    }

    private static void Migration()
    {
        var state = DockState.Preset("balanced");
        var left = state.Groups.Single(group => group.Region == "left");
        left.Tools.Add(DockState.Tool("files"));
        state.Groups.Add(new DockGroup { Region = "left", Tools = [DockState.Tool("lsp")] });
        state.Groups.Single(group => group.Region == "right" && group.Tools.Any(tab => tab.Id == "files")).Tools.RemoveAll(tab => tab.Id == "files");
        state.LeftVisible = false;
        var legacy = new LayoutSession(state.Copy());
        Require(legacy.State.Groups.Count(group => group.Region == "left") == 2 && !legacy.State.LeftVisible, "Without unified chrome the left column keeps its tools.");
        var session = new LayoutSession(state.Copy(), fixedLeft: true);
        var lefts = session.State.Groups.Where(group => group.Region == "left").ToArray();
        var rights = session.State.Groups.Where(group => group.Region == "right").SelectMany(group => group.Tools).Select(tab => tab.Id).ToArray();
        Require(lefts.Length == 1 && lefts[0].Tools.Select(tab => tab.Id).SequenceEqual(["projects"]) && session.State.LeftVisible,
            "A saved layout's left column becomes the Projects rail alone, shown.");
        Require(rights.Contains("files") && rights.Contains("lsp"), "The tools the left column held move to the right side.");
        session.Visible("left", false);
        Require(session.State.LeftVisible, "The fixed rail cannot be hidden.");
        foreach (var name in new[] { "focus", "review", "balanced" })
        {
            session.ApplyPreset(DockState.Preset(name));
            Require(session.State.LeftVisible && session.State.Groups.Count(group => group.Region == "left") == 1, $"The {name} preset keeps the fixed rail.");
        }
        Console.WriteLine("PASS unified chrome: old layouts migrate, presets keep the rail, the rail cannot be hidden");
    }

    /// <summary>
    /// The Settings gear sits beside the add button in the rail header where there is no native application menu, and nowhere on
    /// a Mac. Under an Android desktop window's caption bar the rail header's first row clears the system's app chip, and the strip
    /// at the top right clears its buttons, so one row holds both the system's controls and ours.
    /// </summary>
    private static void CaptionRow(E2eWorkspace app, StackPanel header, Control[] all)
    {
        var gear = all.OfType<Button>().SingleOrDefault(button => button.Name == "RailSettingsButton");
        if (OperatingSystem.IsMacOS()) Require(gear is null, "A Mac keeps Settings in its application menu: the rail header has no gear.");
        else
        {
            var add = app.Find<Button>("AddProjectMenu");
            Require(gear is not null && header.IsLogicalAncestorOf(gear) && ReferenceEquals(gear.Parent, add.Parent), "The Settings gear sits beside the add button in the rail header.");
            Require(Math.Abs(gear!.TranslatePoint(default, app.Window)!.Value.Y - add.TranslatePoint(default, app.Window)!.Value.Y) <= 1 &&
                gear!.TranslatePoint(default, app.Window)!.Value.X > add.TranslatePoint(default, app.Window)!.Value.X, "The gear shares the add button's row, after it.");
        }
        var lights = app.Find<Grid>("RailLightsRow");
        var surface = app.Find<DockSurface>("WorkspaceWorkbench");
        app.Window.Caption = new CaptionBar(40, 140, 180);
        try
        {
            Dispatcher.UIThread.RunJobs(); app.Window.UpdateLayout();
            Require(lights.Height >= 40 && lights.Margin.Left == 140, "Under a caption bar the rail header's first row is as tall as it and clears the app chip.");
            Require(surface.TopRightReserve == (40, 180) && (surface.TopRightStrip is not { } strip || strip.Padding.Right >= 180) &&
                (surface.TopRightStrip is not null || surface.Margin.Top >= 0), "The top right clears the system's buttons.");
            Require(!app.Find<Border>("WindowTitleBar").IsVisible && surface.Bounds.Top == 0, "No separate top bar or strip appears under the caption.");
        }
        finally { app.Window.Caption = null; Dispatcher.UIThread.RunJobs(); }
        Require(surface.TopRightReserve == default, "Without a caption bar nothing is reserved.");
    }

    private static void Window(string directory)
    {
        UnifiedChrome.Override = true;
        try
        {
            using var app = OpenFixtureProject(directory);
            LocationBarChecks.Switch(app, app.Root);
            Require(!app.Find<Border>("WindowTitleBar").IsVisible && app.Find<DockSurface>("WorkspaceWorkbench").Bounds.Top == 0,
                "There is no top bar: the workbench starts at the top of the window.");
            var header = app.Find<StackPanel>("RailHeader");
            bool InHeader(Control control) => header.IsLogicalAncestorOf(control);
            Require((OperatingSystem.IsMacOS() || InHeader(app.Find<Button>("AppMenuButton"))) && InHeader(app.Find<Button>("ScopeProject")) && InHeader(app.Find<Button>("ScopeBranch")) &&
                InHeader(app.Find<Avalonia.Controls.DockPanel>("RemoteRow")), "The rail header holds the overflow menu, the project switcher and the branch block with its sync state.");
            Require(app.Window.Layout.State.Groups.Count(group => group.Region == "left") == 1 && app.Window.Layout.State.LeftVisible,
                "The left column is the Projects rail alone.");
            var all = app.Window.GetLogicalDescendants().OfType<Control>().ToArray();
            Require(!all.Any(control => control.Name is "Tab_projects" or "leftRail" or "PaneTitle" && control.GetLogicalAncestors().Any(a => (a as Control)?.Name == "AuxiliaryRegion_left")) &&
                !all.OfType<Button>().Any(button => button.Name == "Tab_projects"), "The rail has no title, icon or tool tabs.");
            Require(InHeader(app.Find<Button>("AddProjectMenu")) && !all.OfType<TextBlock>().Any(text => text.Text == "PROJECTS"),
                "The add button sits in the rail header and there is no caption row.");
            AddMenuUnderButton(app);
            CaptionRow(app, header, all);
            var panel = app.Find<Grid>("ProjectsPanel");
            Require(panel.RowDefinitions.Count == 1 && panel.Margin.Top == 0, "The project list starts right below the header blocks.");
            Require(all.OfType<StackPanel>().Where(panel => panel.Name == "ProjectWorkspaces").All(panel => panel.IsVisible == panel.Children.Any(child => child.IsVisible)),
                "A project with nothing visible under it takes no room beyond its own row, whether closed or expanded.");
            RightBar(app);
            HeaderDivider(app);
            Layout(app, header);
            HostBlock(app, header);
            // Without a title row for traffic lights (the browser), the header keeps its own inset from the window's top edge.
            Require(OperatingSystem.IsMacOS() || app.Find<Border>("ProjectSegment").TranslatePoint(default, app.Window)!.Value.Y >= WorkbenchWindow.RailTopInset,
                "The rail header does not touch the window's top edge.");
            ViewMenu(app, unified: true);
            HeaderPopups(app);
            Console.WriteLine("PASS unified chrome: no top bar, the rail header, its menus and popups open, no Settings gear in the right bar");
        }
        finally { UnifiedChrome.Override = false; }
    }
}