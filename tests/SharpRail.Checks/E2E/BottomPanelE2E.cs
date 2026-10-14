using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;

using SharpRail.UI.Docking;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

// The bottom zone was retired; retain regression coverage for empty startup and explicit group terminals.
internal static class BottomPanelE2E
{
    internal static void Run(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "without-bottom"), openFiles: false);
        Until(() => app.Workbench.TerminalCatalog?.Workspaces.ContainsKey(app.Root) == true);
        Require(app.Window.Layout.State.Groups.All(group => group.Region != "bottom") &&
            !app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name is "AuxiliaryRegion_bottom" or "bottomSeparator" or "BottomAlignment") &&
            app.Workbench.TerminalCatalog!.Workspaces[app.Root].Count == 0 && app.Terminals.StartedIn(app.Root) == 0,
            "A fresh workspace has neither a bottom zone nor an automatic terminal.");
        var rightVisible = app.Window.Layout.State.RightVisible;
        var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
        app.Window.KeyPress(Key.J, command | RawInputModifiers.Shift, PhysicalKey.J, null);
        app.Window.KeyRelease(Key.J, command | RawInputModifiers.Shift, PhysicalKey.J, null); Settle();
        Require(app.Window.Layout.State.RightVisible == rightVisible,
            "The retired bottom shortcut must not toggle another region.");
        var center = app.Center;
        app.Click(app.Find<Button>("NewTerminal_" + center));
        Until(() => app.Workbench.TerminalCatalog!.Workspaces[app.Root].Count == 1 && app.Terminals.StartedIn(app.Root) == 1);
        var terminal = app.Window.Layout.Tabs(center).Single(tab => tab.Kind == "terminal");
        Require(app.Window.Layout.Move(terminal.Id, center, center, 0, "bottom"), "An explicit terminal can move to a split group below.");
        var group = app.Window.Layout.State.Center.Leaves().Single(id => id != center);
        Require(app.Window.Layout.Selected(group)?.Id == terminal.Id && app.Window.Layout.State.Groups.All(item => item.Region != "bottom"),
            "A split below belongs to the center tree and cannot recreate the bottom zone.");
        Console.WriteLine("PASS bottom-zone removal: empty startup and explicit terminal group splits");
        NarrowSideResizing(root);
    }
    private static void NarrowSideResizing(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "narrow-sides"), openFiles: false);
        (double Left, double Right) Durable() => (app.Window.Layout.State.LeftWidth, app.Window.Layout.State.RightWidth);
        foreach (var side in new[] { "left", "right" })
            foreach (var keyboard in new[] { false, true })
            {
                app.Window.Width = 1200; app.Window.Height = 844; Settle();
                app.Window.Layout.Geometry(state => (state.LeftWidth, state.RightWidth) = (.18, .28));
                var before = Durable();
                app.Window.Width = 560; Settle();
                var handle = app.Find<ResizeHandle>(side + "Separator");
                if (keyboard)
                {
                    handle.Focus();
                    app.Window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null); Settle();
                }
                else
                {
                    var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), app.Window)!.Value;
                    var target = start + new Vector(side == "left" ? -20 : 20, 0);
                    app.Window.MouseMove(start); app.Window.MouseDown(start, MouseButton.Left);
                    app.Window.MouseMove(target); app.Window.MouseUp(target, MouseButton.Left); Settle();
                }
                app.Window.Width = 1200; Settle();
                var after = Durable();
                Require(Math.Abs((side == "left" ? after.Left : after.Right) - (side == "left" ? before.Left : before.Right)) > .001,
                    $"Resizing the {side} side at a narrow width must persist that side.");
                Require(Math.Abs((side == "left" ? after.Right : after.Left) - (side == "left" ? before.Right : before.Left)) < .005,
                    "Resizing one side must not persist its compressed neighbour.");
            }
        Console.WriteLine("PASS upstream bottom-panel.spec.ts: narrow side resizing persists only the side whose separator moved");
    }

}