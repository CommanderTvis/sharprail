using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using SharpRail.UI.Docking;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class DropHintsE2E
{
    internal static void Run(string root)
    {
        Destinations(root);
        HiddenBottom(root);
    }

    private static Rect Bounds(Control control, Visual relative) =>
        new(control.TranslatePoint(default, relative)!.Value, control.Bounds.Size);

    private static Rect PaintedBounds(Border hint) => new(Canvas.GetLeft(hint), Canvas.GetTop(hint), hint.Width, hint.Height);
    private static bool Same(Rect first, Rect second) => Math.Abs(first.X - second.X) < .01 && Math.Abs(first.Y - second.Y) < .01 &&
        Math.Abs(first.Width - second.Width) < .01 && Math.Abs(first.Height - second.Height) < .01;
    private static bool Tint(IBrush? brush, byte alpha) => brush is ISolidColorBrush color && color.Color.A == alpha;
    private static Canvas Overlay(DockSurface surface) => surface.Children.OfType<Canvas>().Single();

    private static void Start(E2eWorkspace app, Button tab)
    {
        Settle(550);
        var start = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(start); app.Window.MouseDown(start, MouseButton.Left);
        app.Window.MouseMove(start + new Vector(12, 8)); Settle();
        Require(app.Find<DockSurface>("WorkspaceWorkbench").IsDragging, "Pointer movement must start a drag and reveal its legal destinations.");
    }

    private static Rect Section(E2eWorkspace app, DockSurface surface)
    {
        var header = Bounds(app.Find<Grid>("GroupHeader_" + app.Center), surface);
        var body = Bounds(app.Find<Border>("DockBody_" + app.Center), surface);
        return new Rect(body.Left, header.Top, body.Width, body.Bottom - header.Top);
    }

    private static void Move(E2eWorkspace app, DockSurface surface, Point point)
    {
        app.Window.MouseMove(surface.TranslatePoint(point, app.Window)!.Value); Settle();
    }

    private static void Destinations(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "drop-hint-destinations"));
        app.Open("README.md", true); app.Open("notes.txt", true);
        var surface = app.Find<DockSurface>("WorkspaceWorkbench");
        Require(Overlay(surface).Children.Count == 0, "No drop hints may remain before a drag starts.");
        Start(app, app.Tab("README.md"));
        var section = Section(app, surface);
        Move(app, surface, section.Center);
        var header = Bounds(app.Find<Grid>("GroupHeader_" + app.Center), surface);
        var rightId = app.Window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "specs")).Id;
        var rightHeader = Bounds(app.Find<Grid>("GroupHeader_" + rightId), surface);
        var hints = Overlay(surface).Children.OfType<Border>().ToArray();
        Require(hints.Any(hint => Same(PaintedBounds(hint), header) && Tint(hint.BorderBrush, 51) && Tint(hint.Background, 0)) &&
            !hints.Any(hint => Same(PaintedBounds(hint), rightHeader)) && !hints.Any(hint => hint.Height == 24),
            "A file drag must hint its legal center strip, exclude side tools and omit the hidden-bottom target.");
        var edge = new Rect(section.Right - 4 - section.Width / 5, section.Top + section.Height / 4, section.Width / 5, section.Height / 2);
        Require(hints.Any(hint => Same(PaintedBounds(hint), edge) && Tint(hint.Background, 26) && Tint(hint.BorderBrush, 51)),
            "The unhovered right split destination must use the reference subtle tint.");
        Move(app, surface, edge.Center);
        hints = Overlay(surface).Children.OfType<Border>().ToArray();
        var half = new Rect(section.Center.X, section.Top, section.Width / 2, section.Height);
        Require(hints.Any(hint => Same(PaintedBounds(hint), half) && Tint(hint.Background, 51) && Tint(hint.BorderBrush, 255) && hint.BorderThickness == new Thickness(2)) &&
            hints.Any(hint => Same(PaintedBounds(hint), header) && Tint(hint.BorderBrush, 51)),
            "Hovering the split must emphasize its half-pane preview while other valid destinations remain subtle.");
        app.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        app.Window.MouseUp(surface.TranslatePoint(edge.Center, app.Window)!.Value, MouseButton.Left); Settle();
        Require(!surface.IsDragging && Overlay(surface).Children.Count == 0 && app.Window.Layout.State.Center.Leaves().Count() == 1 && app.Tabs.Count == 2,
            "Escape and release must remove every hint without changing the frame or resource placement.");
        Console.WriteLine("PASS upstream layout.spec.ts: a tab drag reveals every valid destination subtly, then emphasizes the one under the pointer");
    }

    private static void HiddenBottom(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "drop-hint-hidden-bottom"));
        var bottom = app.Window.Layout.State.Groups.Single(group => group.Region == "bottom");
        var add = app.Find<Button>("AddToGroup_" + bottom.Id);
        app.Click(add); Until(() => add.ContextMenu!.IsOpen);
        app.Click(add.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "New terminal")), freshGesture: false);
        Until(() => app.Window.Layout.Tabs(bottom.Id).Any(tab => tab.Kind == "terminal"));
        var terminal = app.Window.Layout.Tabs(bottom.Id).Single();
        var name = terminal.Id.Replace(':', '_');
        var body = app.Find<Border>("TerminalSurface_" + name);
        var tab = app.Find<Button>("Tab_" + name);
        app.ContextAction(tab, "Move to pane");
        var move = tab.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Move to pane"));
        Until(() => move.IsSubMenuOpen);
        app.Click(move.Items.OfType<MenuItem>().Single(item => ((string)item.Header!).StartsWith("center:", StringComparison.Ordinal)), freshGesture: false);
        Until(() => app.Tabs.Any(tab => tab.Id == terminal.Id));
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        app.Window.KeyPress(Key.J, command | RawInputModifiers.Shift, PhysicalKey.J, null);
        app.Window.KeyRelease(Key.J, command | RawInputModifiers.Shift, PhysicalKey.J, null); Settle();
        Require(!app.Window.Layout.State.BottomVisible, "The bottom shortcut must hide its empty frame group.");
        var surface = app.Find<DockSurface>("WorkspaceWorkbench");
        Start(app, app.Find<Button>("Tab_" + name));
        var zone = Overlay(surface).Children.OfType<Border>().Single(hint => hint.Height == 24 && hint.BorderThickness == new Thickness(0, 1, 0, 0));
        var zoneBounds = PaintedBounds(zone);
        Require(Tint(zone.Background, 26), "The hidden-bottom target must initially display its subtle hint.");
        var section = Section(app, surface);
        var split = new Rect(section.Left + section.Width / 4, section.Bottom - 4 - section.Height / 5, section.Width / 2, section.Height / 5);
        Require(split.Contains(zoneBounds.Center), "The chosen point must overlap a legal center-bottom split destination.");
        Move(app, surface, zoneBounds.Center);
        zone = Overlay(surface).Children.OfType<Border>().Single(hint => Same(PaintedBounds(hint), zoneBounds));
        Require(zone.BorderThickness == new Thickness(0, 2, 0, 0) && Tint(zone.Background, 51),
            "The hidden-bottom target must win the overlap and display its active state.");
        app.Window.MouseUp(surface.TranslatePoint(zoneBounds.Center, app.Window)!.Value, MouseButton.Left); Settle();
        Require(app.Window.Layout.State.BottomVisible && app.Window.Layout.State.Groups.Count(group => group.Region == "bottom") == 1 &&
            app.Window.Layout.State.Center.Leaves().Count() == 1 && app.Window.Layout.Tabs(bottom.Id).Single().Id == terminal.Id &&
            !app.Tabs.Any(tab => tab.Id == terminal.Id) && ReferenceEquals(body, app.Find<Border>("TerminalSurface_" + name)) && Overlay(surface).Children.Count == 0,
            "The winning drop must reveal the retained bottom group and move the one terminal body without splitting the center.");
        Console.WriteLine("PASS upstream layout.spec.ts: the hidden bottom drop zone wins overlapping terminal targets and reveals its frame group");
    }
}
