using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using SharpRail.UI.Docking;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class GestureCancellationE2E
{
    internal static void Run(string root)
    {
        QuietTransitions(root);
        InterruptedResize(root);
    }

    private static (ResizeHandle Handle, Point Start) StartResize(E2eWorkspace app)
    {
        var handle = app.Find<ResizeHandle>("rightSeparator");
        var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), app.Window)!.Value;
        app.Window.MouseMove(start); app.Window.MouseDown(start, MouseButton.Left);
        app.Window.MouseMove(start + new Vector(-60, 0)); Settle();
        Require(handle.IsActive, "Pointer input must start a live side resize.");
        return (handle, start);
    }

    private static void QuietTransitions(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "gesture-quiet-transitions"));
        var before = app.Find<Grid>("AuxiliaryStack_right").Bounds.Width;
        var (_, start) = StartResize(app);
        app.Window.MouseUp(start + new Vector(-60, 0), MouseButton.Left); Settle();
        Require(app.Find<Grid>("AuxiliaryStack_right").Bounds.Width > before + 30,
            "The completed resize must commit its larger width.");
        var files = app.Window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "files")).Id;
        app.Click(app.Find<Button>("FoldRestore_" + files));
        Require(app.Window.Layout.Group(files).Folded, "The Files group must fold.");
        app.Click(app.Find<Button>("FoldRestore_" + files));
        Require(!app.Window.Layout.Group(files).Folded, "The Files group must unfold.");
        app.Click(app.Find<Button>("Tab_changes"));
        var changes = app.Window.Layout.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == "changes")).Id;
        Require(app.Window.Layout.Selected(changes)?.Id == "changes" && !app.Find<Border>("GestureToast").IsVisible,
            "Ordinary transitions after a committed gesture must never announce a cancellation.");
        Console.WriteLine("PASS upstream layout.spec.ts: local layout transitions with no gesture in progress never announce a canceled drag");
    }

    private static void InterruptedResize(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "gesture-interrupted-resize"));
        var canonical = app.Window.Layout.State.RightWidth;
        var (handle, start) = StartResize(app);
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        app.Window.KeyPress(Key.J, command, PhysicalKey.J, null);
        app.Window.KeyRelease(Key.J, command, PhysicalKey.J, null); Settle();
        Require(!app.Window.Layout.State.RightVisible && !handle.IsActive && app.Window.Layout.State.RightWidth == canonical &&
            app.Find<Border>("GestureToast").IsVisible && app.Find<TextBlock>("GestureToastMessage").Text == "The layout changed. Your drag was canceled.",
            "The local transition must abort the resize draft and explain its cancellation.");
        app.Window.MouseUp(start + new Vector(-60, 0), MouseButton.Left); Settle();
        Require(!app.Window.Layout.State.RightVisible && app.Window.Layout.State.RightWidth == canonical,
            "A stale pointer release must not commit or reveal the interrupted side.");
        Require(app.Find<Border>("GestureToast").Bounds.Width == 356,
            "The desktop notification must fit the reference 380px viewport with 12px outer padding.");
        app.Window.Width = 500; Settle();
        Require(app.Find<Border>("GestureToast").Bounds.Width == 476,
            "Below the reference breakpoint the notification must span the window minus its outer padding.");
        app.Window.Width = 1352; Settle();
        app.Click(app.Find<Button>("DismissGestureToast"));
        Require(!app.Find<Border>("GestureToast").IsVisible, "Dismiss must hide the cancellation notification.");
        app.Click(app.Find<Button>("rightRestoreRail"));
        var (_, nextStart) = StartResize(app);
        app.Window.KeyPress(Key.J, command, PhysicalKey.J, null);
        app.Window.KeyRelease(Key.J, command, PhysicalKey.J, null); Settle();
        Require(app.Find<Border>("GestureToast").IsVisible, "A subsequent interrupted gesture must show a new notification.");
        app.Window.MouseUp(nextStart + new Vector(-60, 0), MouseButton.Left); Settle();
        Until(() => !app.Find<Border>("GestureToast").IsVisible);
        Console.WriteLine("PASS upstream layout.spec.ts: a local transition during a side resize cancels the gesture and says so");
    }
}
