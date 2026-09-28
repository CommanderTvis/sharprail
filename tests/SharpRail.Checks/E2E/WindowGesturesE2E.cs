using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using SharpRail.UI.Docking;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class WindowGesturesE2E
{
    internal static void Run(string root)
    {
        TabDrag(root);
        SideResize(root);
    }

    private static void Toggle(E2eWorkspace app, Key key, PhysicalKey physical)
    {
        Require(app.Find<Button>("Tab_files").Focus(), "The shortcut target window must own keyboard focus.");
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        app.Window.KeyPress(key, command, physical, null);
        app.Window.KeyRelease(key, command, physical, null);
        Settle();
    }

    private static void TabDrag(string root)
    {
        var directory = Path.Combine(root, "window-tab-drag");
        using var first = new E2eWorkspace(directory);
        first.Open("README.md", true); first.Open("notes.txt", true);
        using var second = new E2eWorkspace(directory, profileRoot: directory + "-second-profile");
        var surface = first.Find<DockSurface>("WorkspaceWorkbench");
        var tab = first.Tab("notes.txt");
        var start = tab.TranslatePoint(new Point(tab.Bounds.Width / 2, tab.Bounds.Height / 2), first.Window)!.Value;
        first.Window.MouseMove(start); first.Window.MouseDown(start, MouseButton.Left);
        first.Window.MouseMove(start + new Vector(12, 8)); Settle();
        Require(surface.IsDragging, "The first window must have an active tab drag.");
        var epoch = first.Window.Layout.Epoch;
        Toggle(second, Key.B, PhysicalKey.B);
        Require(!second.Window.Layout.State.LeftVisible && first.Window.Layout.State.LeftVisible &&
            surface.IsDragging && first.Window.Layout.Epoch == epoch && !first.Find<Border>("GestureToast").IsVisible && !first.Find<TextBlock>("WorkspaceError").IsVisible,
            $"A second window's layout transition must not cancel or rearrange the first window's drag: second left={second.Window.Layout.State.LeftVisible}, first left={first.Window.Layout.State.LeftVisible}, drag={surface.IsDragging}, epoch={first.Window.Layout.Epoch}/{epoch}, error={first.Find<TextBlock>("WorkspaceError").IsVisible}.");
        var header = first.Find<Grid>("GroupHeader_" + first.Center);
        var body = first.Find<Border>("DockBody_" + first.Center);
        var origin = header.TranslatePoint(default, first.Window)!.Value;
        var height = body.TranslatePoint(new Point(0, body.Bounds.Height), first.Window)!.Value.Y - origin.Y;
        var target = new Point(origin.X + body.Bounds.Width * .9 - 4, origin.Y + height / 2);
        first.Window.MouseMove(target); Settle(); first.Window.MouseUp(target, MouseButton.Left); Settle();
        Require(first.Window.Layout.State.Center.Leaves().Count() == 2 &&
            first.Window.Layout.State.Center.Leaves().SelectMany(first.Window.Layout.Tabs).Count(tab => tab.Path == "notes.txt") == 1 &&
            second.Window.Layout.State.Center.Leaves().Count() == 1 && !second.Window.Layout.State.LeftVisible,
            "The original drag must still split its own center without changing the other window.");
        Console.WriteLine("PASS upstream layout.spec.ts: another window cannot cancel or rearrange an active tab drag");
    }

    private static void SideResize(string root)
    {
        var directory = Path.Combine(root, "window-side-resize");
        using var first = new E2eWorkspace(directory);
        using var second = new E2eWorkspace(directory, profileRoot: directory + "-second-profile");
        var separator = first.Find<ResizeHandle>("rightSeparator");
        var before = first.Find<Grid>("AuxiliaryStack_right").Bounds.Width;
        var canonical = first.Window.Layout.State.RightWidth;
        var secondWidth = second.Window.Layout.State.RightWidth;
        var start = separator.TranslatePoint(new Point(separator.Bounds.Width / 2, separator.Bounds.Height / 2), first.Window)!.Value;
        first.Window.MouseMove(start); first.Window.MouseDown(start, MouseButton.Left);
        first.Window.MouseMove(start + new Vector(-60, 0)); Settle();
        Require(separator.IsActive && first.Window.Layout.State.RightWidth == canonical,
            "The first window's resize must remain an uncommitted draft.");
        Toggle(second, Key.J, PhysicalKey.J);
        Require(!second.Window.Layout.State.RightVisible && first.Window.Layout.State.RightVisible &&
            separator.IsActive && first.Window.Layout.State.RightWidth == canonical && !first.Find<Border>("GestureToast").IsVisible && !first.Find<TextBlock>("WorkspaceError").IsVisible,
            "A second window's layout transition must not cancel or adopt the first window's resize.");
        first.Window.MouseUp(start + new Vector(-60, 0), MouseButton.Left); Settle();
        Require(first.Find<Grid>("AuxiliaryStack_right").Bounds.Width > before + 30 &&
            first.Window.Layout.State.RightWidth > canonical && !second.Window.Layout.State.RightVisible &&
            second.Window.Layout.State.RightWidth == secondWidth,
            "Pointer release must commit only the first window's width and retain the other window's hidden side.");
        Console.WriteLine("PASS upstream layout.spec.ts: another window cannot cancel or adopt an active side resize");
    }
}
