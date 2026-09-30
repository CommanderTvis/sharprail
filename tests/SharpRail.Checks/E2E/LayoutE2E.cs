using Avalonia.Controls;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Controls.Primitives;
using SharpRail.UI.Docking;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class LayoutE2E
{
    internal static void Run(string root)
    {
        WindowGesturesE2E.Run(root);
        GestureCancellationE2E.Run(root);
        DropHintsE2E.Run(root);
        NarrowViewport(root);
        PointerSplit(root);
        SideSplitTargets(root);
        DeferredOpens(root);
        IndependentPreviews(root);
        SideShortcuts(root);
        ZoomShortcuts(root);
        KeyboardTabsAndSeparators(root);
        OuterWidths(root);
        OverflowFades(root);
        LayoutFrameE2E.Run(root);
    }

    private static void SideSplitTargets(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "layout-side-splits"));
        var files = app.Find<Button>("Tab_files");
        app.Click(files, mouseButton: MouseButton.Right); Until(() => files.ContextMenu!.IsOpen);
        foreach (var title in new[] { "New group above", "New group below" })
            Require(files.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, title)).IsEnabled,
                "Side tabs must offer enabled above and below split actions.");
        files.ContextMenu!.Close();
        string[] Order() => app.Window.Layout.State.Groups.Where(group => group.Region == "right")
            .Select(group => app.Window.Layout.Tabs(group.Id).FirstOrDefault()?.Id ?? "empty").ToArray();
        void Drop(bool before)
        {
            Settle(550);
            var changes = app.Window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "changes"));
            var header = app.Find<Grid>("GroupHeader_" + changes.Id);
            var origin = header.TranslatePoint(default, app.Window)!.Value;
            var body = changes.Folded ? null : app.Find<Border>("DockBody_" + changes.Id);
            var panelHeight = body is null ? 27 : body.TranslatePoint(new Point(0, body.Bounds.Height), app.Window)!.Value.Y - origin.Y;
            var targetHeight = panelHeight / 2 - 4;
            var target = new Point(origin.X + header.Bounds.Width / 2,
                origin.Y + (before ? 4 + targetHeight / 2 : panelHeight / 2 + targetHeight / 2));
            var tab = app.Find<Button>("Tab_files");
            var start = tab.TranslatePoint(new Point(24, tab.Bounds.Height / 2), app.Window)!.Value;
            app.Window.MouseMove(start); app.Window.MouseDown(start, MouseButton.Left);
            app.Window.MouseMove(start + new Vector(12, 8)); Settle();
            Require(app.Find<DockSurface>("WorkspaceWorkbench").IsDragging, "Side split input must start a drag.");
            if (!changes.Folded) Require(targetHeight > 40, "Unfolded side split destinations must span more than a narrow edge.");
            app.Window.MouseMove(target); Settle(); app.Window.MouseUp(target, MouseButton.Left); Settle();
        }
        Drop(true);
        Require(Order().SequenceEqual(new[] { "specs", "files", "changes" }), "Dropping above Changes must insert Files between the existing groups.");
        Drop(false);
        Require(Order().SequenceEqual(new[] { "specs", "empty", "changes", "files" }), "Dropping below Changes must retain the emptied source group.");
        var changesId = app.Window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "changes")).Id;
        app.Find<Button>("FoldRestore_" + changesId).Focus();
        app.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Settle();
        Require(app.Window.Layout.Group(changesId).Folded, "Enter must fold the Changes group.");
        Drop(true);
        Require(Order().SequenceEqual(new[] { "specs", "empty", "files", "changes", "empty" }) && app.Window.Layout.Group(changesId).Folded,
            "A folded group's split destination must insert Files above it and preserve its folded state.");
        Console.WriteLine("PASS upstream layout.spec.ts: side groups expose broad per-panel above and below split targets");
    }

    private static void PointerSplit(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "layout-pointer-split"));
        app.Open("README.md", true); app.Open("notes.txt", true);
        var surface = app.Find<DockSurface>("WorkspaceWorkbench");
        void Start(string path)
        {
            Settle(550);
            var tab = app.Tab(path);
            var point = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), app.Window)!.Value;
            app.Window.MouseMove(point); app.Window.MouseDown(point, MouseButton.Left);
            app.Window.MouseMove(point + new Vector(12, 8)); Settle();
            Require(surface.IsDragging, "Pointer movement must expose the drag destinations.");
        }
        Start("README.md");
        var notes = app.Tab("notes.txt");
        var after = notes.TranslatePoint(new Point(notes.Bounds.Width - 8, notes.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(after); Settle();
        Require(surface.Children.OfType<Canvas>().Single().Children.OfType<Border>().Any(hint => hint.Width == 2),
            "The hovered reorder destination must display an insertion marker.");
        app.Window.MouseUp(after, MouseButton.Left); Settle();
        Require(app.Tabs.Select(tab => tab.Path).SequenceEqual(new[] { "notes.txt", "README.md" }),
            "Dropping after notes.txt must reorder the two existing tabs.");

        Start("notes.txt");
        var body = app.Find<Border>("DockBody_" + app.Center);
        var header = app.Find<Grid>("GroupHeader_" + app.Center);
        var origin = header.TranslatePoint(default, app.Window)!.Value;
        var height = body.TranslatePoint(new Point(0, body.Bounds.Height), app.Window)!.Value.Y - origin.Y;
        var target = new Point(origin.X + body.Bounds.Width * .9 - 4, origin.Y + height / 2);
        var hints = surface.Children.OfType<Canvas>().Single().Children.OfType<Border>().ToArray();
        Require(hints.Any(hint => Math.Abs(hint.Width - body.Bounds.Width / 5) < .01 && Math.Abs(hint.Height - height / 2) < .01),
            "A wide center pane must expose its right split destination before hovering it.");
        app.Window.MouseMove(target); Settle();
        app.Window.MouseUp(target, MouseButton.Left); Settle();
        var groups = app.Window.Layout.State.Center.Leaves().ToArray();
        Require(groups.Length == 2 && groups.SelectMany(app.Window.Layout.Tabs).Count() == 2 &&
            groups.Count(group => app.Window.Layout.Tabs(group).Any(tab => tab.Path == "notes.txt")) == 1,
            "A split drop must create two panes and move exactly one tab without duplication.");
        Console.WriteLine("PASS upstream layout.spec.ts: pointer drag exposes deterministic split targets and moves one tab");
    }

    private static void NarrowViewport(string root)
    {
        var directory = Path.Combine(root, "layout-narrow-viewport");
        string topology;
        using (var app = new E2eWorkspace(directory))
        {
            app.Open("README.md", true); app.Open("notes.txt", true);
            app.ContextAction(app.Tab("notes.txt"), "Split right");
            Until(() => app.Window.Layout.State.Center.Leaves().Count() == 2);
            topology = System.Text.Json.JsonSerializer.Serialize(app.Window.Layout.State.Center);
            app.Window.Width = 390; app.Window.Height = 844; Settle();
            var workbench = app.Find<DockSurface>("WorkspaceWorkbench");
            var position = workbench.TranslatePoint(default, app.Window)!.Value;
            Require(app.Window.Bounds.Width == 390 && position.X >= 0 && position.X + workbench.Bounds.Width <= 390,
                "The workbench must fit a 390-pixel viewport.");
            Require(app.Window.Layout.State.Center.Leaves().Count() == 2 &&
                app.Window.Layout.State.Groups.Count(group => group.Region == "bottom") == 1 &&
                System.Text.Json.JsonSerializer.Serialize(app.Window.Layout.State.Center) == topology,
                "Viewport compression must preserve recursive topology and the bottom group.");
            var tab = app.Tab("README.md");
            app.Click(tab, mouseButton: MouseButton.Right);
            Until(() => tab.ContextMenu!.IsOpen);
            Require(!tab.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Split right")).IsEnabled,
                "A narrow pane must disable further horizontal splits.");
            tab.ContextMenu.Close();
        }
        using (var app = new E2eWorkspace(directory, openFiles: false))
        {
            app.Window.Width = 390; app.Window.Height = 844; Settle();
            Require(app.Window.Layout.State.Center.Leaves().Count() == 2 &&
                System.Text.Json.JsonSerializer.Serialize(app.Window.Layout.State.Center) == topology,
                "Reloading must preserve recursive topology after viewport compression.");
        }
        Console.WriteLine("PASS upstream layout.spec.ts: a narrow viewport compresses locally without rewriting recursive topology");
    }

    private static void DeferredOpens(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "layout-deferred-opens"));
        app.Open("README.md", true); app.Open("notes.txt", true);
        app.ContextAction(app.Tab("notes.txt"), "Split right");
        Until(() => app.Window.Layout.State.Center.Leaves().Count() == 2);
        var notesGroup = app.Center;
        app.Click(app.Tab("README.md"));
        var origin = app.Center;
        var links = app.Host.Hold("LINKS.md");
        app.Click(app.FileRow("LINKS.md"));
        Until(() => app.Host.Reads.GetValueOrDefault("LINKS.md") == 1);
        app.Click(app.Tab("notes.txt")); links.SetResult();
        Until(() => app.Window.Layout.Tabs(origin).Any(tab => tab.Path == "LINKS.md"));
        Require(app.Window.Layout.Selected(origin)?.Path == "README.md" &&
            app.Center == notesGroup && app.Window.Layout.Selected(notesGroup)?.Path == "notes.txt",
            $"A delayed open must retain its request-time pane without stealing selection: origin={origin}, selected={app.Window.Layout.Selected(origin)?.Path}, focused={app.Center}, notes={notesGroup}, notesSelected={app.Window.Layout.Selected(notesGroup)?.Path}.");
        app.Click(app.Tab("README.md"));
        var alerts = app.Host.Hold("ALERTS.md");
        app.Click(app.FileRow("ALERTS.md"));
        Until(() => app.Host.Reads.GetValueOrDefault("ALERTS.md") == 1);
        foreach (var path in new[] { "README.md", "LINKS.md" })
        {
            var wrapper = app.Tab(path).GetLogicalAncestors().OfType<Grid>().First(item => item.Name?.StartsWith("DockTab_", StringComparison.Ordinal) == true);
            app.Click(wrapper.GetLogicalDescendants().OfType<Button>().Single(item => item.Name == "CloseTab"));
        }
        app.Click(app.Find<Button>("RemoveGroup_" + origin));
        Until(() => app.Window.Layout.State.Center.Leaves().Count() == 1);
        alerts.SetResult();
        Until(() => app.Window.Layout.Selected(app.Center)?.Path == "ALERTS.md");
        Console.WriteLine("PASS upstream layout.spec.ts: deferred opens stay with their request-time group and reroute only when it disappears");
    }

    private static void IndependentPreviews(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "layout-preview-groups"));
        app.Open("notes.txt", true); app.Open("README.md");
        var readmeGroup = app.Center;
        app.ContextAction(app.Tab("notes.txt"), "Split right");
        Until(() => app.Window.Layout.State.Center.Leaves().Count() == 2);
        var notesGroup = app.Center;
        Require(notesGroup != readmeGroup && app.Window.Layout.Tabs(readmeGroup).Single().Preview,
            "Splitting a kept tab must leave the other group's preview intact.");
        app.Open("LINKS.md");
        Require(app.Window.Layout.Tabs(notesGroup).Any(tab => tab.Path == "LINKS.md" && tab.Preview) &&
            app.Window.Layout.Tabs(readmeGroup).Single().Path == "README.md" &&
            app.Window.Layout.Tabs(readmeGroup).Single().Preview &&
            app.Window.Layout.State.Center.Leaves().SelectMany(app.Window.Layout.Tabs).Count(tab => tab.Preview) == 2,
            "Each center group must own an independent preview slot.");
        Console.WriteLine("PASS upstream layout.spec.ts: each center group owns an independent preview slot");
    }

    private static void SideShortcuts(string root)
    {
        var directory = Path.Combine(root, "layout-side-shortcuts");
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        bool bottom;
        using (var app = new E2eWorkspace(directory))
        {
            bottom = app.Window.Layout.State.BottomVisible;
            app.Window.KeyPress(Key.B, command, PhysicalKey.B, null); Settle();
            Require(!app.Window.Layout.State.LeftVisible && app.Find<Border>("leftHiddenSideRail").IsVisible,
                "Mod+B must hide the left side and expose its restore rail.");
            app.Window.KeyPress(Key.J, command, PhysicalKey.J, null); Settle();
            Require(!app.Window.Layout.State.RightVisible && app.Find<Border>("rightHiddenSideRail").IsVisible &&
                app.Window.Layout.State.BottomVisible == bottom, "Mod+J must hide the right side without changing the bottom.");
        }
        using (var app = new E2eWorkspace(directory, openFiles: false))
        {
            Require(!app.Window.Layout.State.LeftVisible && !app.Window.Layout.State.RightVisible && app.Window.Layout.State.BottomVisible == bottom,
                "Hidden sides must survive window recreation.");
            app.Window.KeyPress(Key.B, command, PhysicalKey.B, null);
            app.Window.KeyPress(Key.J, command, PhysicalKey.J, null); Settle();
            Require(app.Window.Layout.State.LeftVisible && app.Window.Layout.State.RightVisible && app.Window.Layout.State.BottomVisible == bottom,
                "The same shortcuts must restore both sides and preserve the bottom.");
        }
        Console.WriteLine("PASS upstream layout.spec.ts: Mod+B and Mod+J hide and restore local sides without affecting bottom");
    }

    private static void ZoomShortcuts(string root)
    {
        var directory = Path.Combine(root, "layout-zoom");
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        using (var app = new E2eWorkspace(directory))
        {
            app.Window.KeyPress(Key.OemPlus, command, PhysicalKey.Equal, null);
            app.Window.KeyPress(Key.OemPlus, command, PhysicalKey.Equal, null); Settle();
            Require(app.Window.Preferences.FontSize == 16 && app.Window.FontSize == 16, "Mod+= must step the interface size up.");
            app.Window.KeyPress(Key.OemMinus, command, PhysicalKey.Minus, null); Settle();
            Require(app.Window.Preferences.FontSize == 15, "Mod+- must step the interface size down.");
        }
        using (var app = new E2eWorkspace(directory, openFiles: false))
        {
            Require(app.Window.Preferences.FontSize == 15, "The zoomed interface size must persist.");
            for (var i = 0; i < 20; i++) app.Window.KeyPress(Key.OemMinus, command, PhysicalKey.Minus, null);
            Require(app.Window.Preferences.FontSize == 10, "Zooming out must stop at the minimum interface size.");
            app.Window.KeyPress(Key.D0, command, PhysicalKey.Digit0, null); Settle();
            Require(app.Window.Preferences.FontSize == 14 && app.Window.FontSize == 14, "Mod+0 must reset the interface size.");
        }
        Console.WriteLine("PASS page zoom: Mod+=, Mod+- and Mod+0 step, bound, persist and reset the interface size");
    }

    private static void KeyboardTabsAndSeparators(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "layout-keyboard"));
        app.Open("README.md", true); app.Open("notes.txt", true); app.Open("LINKS.md", true);
        app.Tab("LINKS.md").Focus();
        app.Window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        Until(() => app.Tab("README.md").IsFocused);
        var readmePeer = (ISelectionItemProvider)ControlAutomationPeer.CreatePeerForElement(app.Tab("README.md"))!;
        Require(readmePeer.IsSelected && !app.Tab("notes.txt").IsTabStop, "Tabs must expose one selected item and roving keyboard focus.");
        var body = ControlAutomationPeer.CreatePeerForElement(app.Find<Border>("DockBody_" + app.Center))!;
        Require(body.GetAutomationControlType() == AutomationControlType.Pane &&
            ReferenceEquals(body.GetLabeledBy(), readmePeer), "The accessible pane must be labelled by its selected tab.");
        app.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        Until(() => app.Tab("notes.txt").IsFocused);
        Require(((ISelectionItemProvider)ControlAutomationPeer.CreatePeerForElement(app.Tab("notes.txt"))!).IsSelected,
            "Arrow navigation must select the focused tab.");
        Require(body.GetLabeledBy()?.GetName() == "notes.txt", "The pane label must follow keyboard selection.");
        app.Window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        Until(() => app.Tab("LINKS.md").IsFocused);
        Require(app.Tabs.Count == 2 && ((ISelectionItemProvider)ControlAutomationPeer.CreatePeerForElement(app.Tab("LINKS.md"))!).IsSelected,
            "Closing the focused tab must focus and select its successor.");
        foreach (var name in new[] { "rightSeparator", "bottomSeparator" })
        {
            var separator = app.Find<ResizeHandle>(name);
            var peer = ControlAutomationPeer.CreatePeerForElement(separator)!;
            var range = (IRangeValueProvider)peer;
            Require(peer.GetAutomationControlType() == AutomationControlType.Separator && double.IsFinite(range.Value) &&
                range.Minimum <= range.Value && range.Value <= range.Maximum && range.Maximum > range.Minimum &&
                peer.GetName() == (name == "rightSeparator" ? "Vertical pane separator" : "Horizontal pane separator"),
                "Separators must expose their direction and a valid accessible range.");
        }
        var right = app.Window.Layout.State.Groups.First(group => group.Region == "right").Id;
        var before = app.Find<Grid>("GroupHeader_" + right).Bounds.Width;
        app.Find<ResizeHandle>("rightSeparator").Focus();
        app.Window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null); Settle();
        Require(Math.Abs(app.Find<Grid>("GroupHeader_" + right).Bounds.Width - before) > .5, "Arrow input must resize the right pane.");
        Console.WriteLine("PASS upstream layout.spec.ts: ARIA tabs use roving keyboard focus, recover after close, and expose keyboard separators");
    }

    private static void OuterWidths(string root)
    {
        var directory = Path.Combine(root, "layout-outer-widths");
        double before, resized;
        using (var app = new E2eWorkspace(directory))
        {
            var right = app.Window.Layout.State.Groups.First(group => group.Region == "right").Id;
            before = app.Find<Grid>("GroupHeader_" + right).Bounds.Width;
            var separator = app.Find<ResizeHandle>("rightSeparator");
            var start = separator.TranslatePoint(new Point(separator.Bounds.Width / 2, separator.Bounds.Height / 2), app.Window)!.Value;
            var canonical = app.Window.Layout.State.RightWidth;
            app.Window.MouseDown(start, MouseButton.Left);
            app.Window.MouseMove(start + new Vector(-120, 0)); Settle();
            Require(app.Window.Layout.State.RightWidth == canonical, "Pointer movement must preview without publishing the outer width.");
            app.Window.MouseUp(start + new Vector(-120, 0), MouseButton.Left); Settle();
            resized = app.Find<Grid>("GroupHeader_" + right).Bounds.Width;
            Require(resized > before + 70, "Dragging the right separator left must enlarge the right side.");
        }
        using (var app = new E2eWorkspace(directory, openFiles: false))
        {
            var right = app.Window.Layout.State.Groups.First(group => group.Region == "right").Id;
            var restored = app.Find<Grid>("GroupHeader_" + right).Bounds.Width;
            Require(restored > before + 50 && Math.Abs(restored - resized) < 24, "The committed outer width must survive window recreation.");
        }
        Console.WriteLine("PASS upstream layout.spec.ts: outer side widths publish on pointer-up and restore after reload");
    }

    private static void OverflowFades(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "layout-overflow"));
        app.Window.Width = 800; app.Window.Height = 720;
        foreach (var path in new[] { "README.md", "notes.txt", "ALERTS.md", "DIAGRAM.md", "LARGE.md", "LINKS.md" }) app.Open(path, true);
        var scroll = app.Tab("README.md").GetLogicalAncestors().OfType<ScrollViewer>().First();
        var group = app.Center;
        Require(scroll.Extent.Width > scroll.Viewport.Width && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Hidden &&
            scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled, "Overflowing tabs must scroll horizontally without visible scrollbars.");
        void Geometry()
        {
            Require(Math.Abs(app.Find<Border>("TabStrip_" + group).Bounds.Height - 32) < .1 &&
                Math.Abs(app.Tab("README.md").Bounds.Height - 28) < .1 && Math.Abs(app.Tab("LINKS.md").Bounds.Height - 28) < .1,
                "Overflow must retain the upstream strip and selectable-tab heights.");
        }
        Geometry();
        scroll.Offset = new Vector(0, 0); Settle();
        Require(!app.Find<Border>("TabOverflowBefore_" + group).IsVisible && app.Find<Border>("TabOverflowAfter_" + group).IsVisible,
            "At the start only the trailing overflow fade may be visible.");
        scroll.Offset = new Vector((scroll.Extent.Width - scroll.Viewport.Width) / 2, 0); Settle();
        Require(app.Find<Border>("TabOverflowBefore_" + group).IsVisible && app.Find<Border>("TabOverflowAfter_" + group).IsVisible,
            "In the middle both overflow fades must be visible.");
        app.Tab("README.md").Focus();
        app.Window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        Until(() => app.Tab("LINKS.md").IsFocused); Settle();
        var last = app.Tab("LINKS.md");
        var point = last.TranslatePoint(default, scroll)!.Value;
        Require(app.Find<Border>("TabOverflowBefore_" + group).IsVisible && !app.Find<Border>("TabOverflowAfter_" + group).IsVisible &&
            point.X >= 0 && point.X + last.Bounds.Width <= scroll.Viewport.Width + 1,
            "Keyboard End must reveal the last tab and leave only the leading fade.");
        Geometry();
        Console.WriteLine("PASS upstream layout.spec.ts: overflow uses directional fades without changing tab-strip geometry");
    }
}
