using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.Host.Core;
using SharpRail.UI;
using SharpRail.UI.Docking;
using SharpRail.UI.State;

namespace SharpRail.Checks;

internal static class AppCommandChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Pump(Func<bool> done, string message, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!done() && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
        Require(done(), message);
    }

    private static void Wait(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    }

    internal static void Run(string root)
    {
        var mac = OperatingSystem.IsMacOS();
        var command = mac ? RawInputModifiers.Meta : RawInputModifiers.Control;
        var window = new WorkbenchWindow(new ProjectServices(root), root, new ProfileStore(root + "-app-commands"), E2E.E2eTerminals.Plain);
        var quits = 0;
        window.Commands.Shutdown = () => quits++;
        window.Show();
        Pump(() => window.WorkspaceMounted, "App command workspace did not mount.");
        var hint = window.GetLogicalDescendants().OfType<Border>().Single(item => item.Name == "QuitHintOverlay");
        var layout = window.Layout;
        void Press(Key key, RawInputModifiers modifiers, PhysicalKey physical) { window.KeyPress(key, modifiers, physical, null); Dispatcher.UIThread.RunJobs(); }
        void Release(Key key, RawInputModifiers modifiers, PhysicalKey physical) { window.KeyRelease(key, modifiers, physical, null); Dispatcher.UIThread.RunJobs(); }

        if (AppCommands.QuitChordAvailable)
        {
            window.Activate();
            Press(Key.Q, command, PhysicalKey.Q); Release(Key.Q, command, PhysicalKey.Q);
            Require(hint.IsVisible && quits == 0, "A quit tap must show the hint and leave the app running.");
            Wait(700);
            Require(!hint.IsVisible && quits == 0, "An unconfirmed quit tap must hide the hint without quitting.");

            Press(Key.Q, command, PhysicalKey.Q); Release(Key.Q, command, PhysicalKey.Q); Wait(80);
            Press(Key.Q, command, PhysicalKey.Q); Press(Key.Q, command, PhysicalKey.Q);
            Require(quits == 0, "Key repeat and an unreleased double press must not quit.");
            Release(Key.Q, command, PhysicalKey.Q);
            Pump(() => quits == 1, "Releasing a double press must quit once.");

            quits = 0; window.Commands.Reset();
            Wait(100);
            Press(Key.Q, command, PhysicalKey.Q);
            Pump(() => hint.IsVisible, "A held quit key must show its hint.");
            Wait(1300);
            Require(quits == 0, "A hold must not quit before release.");
            Release(Key.Q, command, PhysicalKey.Q);
            Pump(() => quits == 1, "Releasing a held quit must quit once.");
        }
        quits = 0; window.Commands.Reset();
        Press(Key.F4, RawInputModifiers.Alt, PhysicalKey.F4);
        Require(quits == 0 && !hint.IsVisible, "Alt+F4 must stay the ordinary close, outside the gesture.");
        window.Commands.QuitNow();
        Require(quits == 1, "Explicit quit must be direct.");

        bool Has(string id) => layout.Tabs(layout.View.FocusedCenter).Any(tab => tab.Id == id);
        var center = layout.View.FocusedCenter;
        layout.Open(new DockTab("close-a", "a.txt", "source", "hello.txt"), true, center);
        layout.Open(new DockTab("close-b", "b.txt", "source", "README.md"), true, center);
        layout.Focus(center);
        Press(Key.W, command, PhysicalKey.W);
        Require(!Has("close-b") && Has("close-a"), "Mod+W must close exactly the selected tab of the focused group.");

        var side = layout.State.Groups.First(group => group.Region == "left");
        layout.Focus(side.Id);
        var tools = layout.Tabs(side.Id).Count;
        Press(Key.W, command, PhysicalKey.W);
        Require(layout.Tabs(side.Id).Count == tools && Has("close-a"), "Mod+W on a tool must close nothing and not fall back to the center.");
        layout.Fold(side.Id); layout.Focus(side.Id);
        Press(Key.W, command, PhysicalKey.W);
        Require(layout.Tabs(side.Id).Count == tools && Has("close-a"), "Mod+W on a folded group must have no target.");
        layout.Fold(side.Id); layout.Visible("left", false); layout.Focus(side.Id);
        Press(Key.W, command, PhysicalKey.W);
        Require(layout.Tabs(side.Id).Count == tools && Has("close-a"), "Mod+W in a hidden region must have no target.");
        layout.Visible("left", true);
        layout.Focus(center);

        Press(Key.OemComma, command, PhysicalKey.Comma);
        Pump(() => window.OwnedWindows.Count == 1, "Settings did not open.");
        var settings = (Window)window.OwnedWindows[0];
        Press(Key.W, command, PhysicalKey.W);
        Require(Has("close-a") && window.IsVisible, "Mod+W behind a modal must not reach the workbench.");
        settings.KeyPress(Key.W, command, PhysicalKey.W, null); Dispatcher.UIThread.RunJobs();
        Pump(() => window.OwnedWindows.Count == 0, "Mod+W did not dismiss the modal.");
        Require(window.IsVisible && Has("close-a"), "Dismissing a modal with Mod+W must not close the window or a tab.");

        KeyEventArgs Event(Key key, PhysicalKey physical, string? symbol, KeyModifiers modifiers) =>
            new() { RoutedEvent = InputElement.KeyDownEvent, Key = key, PhysicalKey = physical, KeySymbol = symbol, KeyModifiers = modifiers };
        var modifier = mac ? KeyModifiers.Meta : KeyModifiers.Control;
        Require(AppCommands.Matches(Event(Key.Q, PhysicalKey.Q, "q", modifier), 'Q'), "A typed Latin Q must match.");
        Require(AppCommands.Matches(Event(Key.None, PhysicalKey.Q, "й", modifier), 'Q'), "A non-Latin layout must fall back to the physical Q key.");
        Require(!AppCommands.Matches(Event(Key.X, PhysicalKey.Q, "x", modifier), 'Q'), "A Latin layout that types another letter on the Q position must not match.");
        Require(!AppCommands.Matches(Event(Key.Q, PhysicalKey.Q, "q", modifier | KeyModifiers.Shift), 'Q'), "Extra modifiers must not match the chord.");
        window.Close();
    }
}