using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.UI;
using SharpRail.UI.Android;
using SharpRail.UI.Panels;
using SharpRail.UI.State;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static class SettingsTouchChecks
{
    internal static void Run(string root)
    {
        using var touch = TouchClicks.Install();
        var directory = Path.Combine(root, "settings-touch");
        Directory.CreateDirectory(directory);
        var host = new ProjectServices(directory);
        var window = new WorkbenchWindow(host, directory, new ProfileStore(directory + "-profile"), E2eTerminals.Plain, compact: true);
        window.Width = 400; window.Height = 600; window.Show();
        Until(() => window.WorkspaceMounted);
        var settings = new SettingsWindow(window, () => { }) { Width = 400, Height = 320 };
        settings.Show(window);
        try
        {
            Dispatcher.UIThread.RunJobs(); settings.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var navigation = settings.GetLogicalDescendants().OfType<Border>().Single(control => control.Name == "SettingsNavigationPane");
            var scroller = navigation.GetLogicalDescendants().OfType<ScrollViewer>().Single();
            var appearance = settings.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "Settings_Appearance");
            var label = appearance.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Text == "Appearance");
            // The window's own project registration, or any other projects broadcast, may land while the list is up; it must not open a section.
            var other = Path.Combine(root, "settings-touch-other");
            Directory.CreateDirectory(other);
            Until(() => window.Workbench.State.Current.Projects.Contains(window.ProjectRoot));
            var opened = Task.Run(() => window.Workbench.State.ChangeAsync(HostStateChange.OpenProject(other)));
            Until(() => opened.IsCompleted && window.Workbench.State.Current.Projects.Contains(other));
            Require(navigation.IsVisible && settings.Section == "Appearance", "A projects broadcast must leave the phone's Settings section list in front.");
            var start = Center(label);
            var finger = settings.TouchBegin(start, RawInputModifiers.None);
            settings.TouchMove(finger, start - new Vector(0, 30), RawInputModifiers.None);
            Require(scroller.Offset.Y > 0, "A first touch movement over a Settings button must scroll immediately, without waiting for a hold.");
            settings.TouchEnd(finger, start - new Vector(0, 30), RawInputModifiers.None);
            Require(navigation.IsVisible, "Releasing a drag over a Settings button must not open its section.");

            scroller.Offset = default;
            Dispatcher.UIThread.RunJobs(); settings.UpdateLayout();
            start = Center(label);
            finger = settings.TouchBegin(start, RawInputModifiers.None);
            Require(navigation.IsVisible, "A touch press alone must not activate a Settings section.");
            settings.TouchEnd(finger, start, RawInputModifiers.None);
            Require(!navigation.IsVisible && settings.Section == "Appearance", "A tap without dragging must still open the section on release.");
            Dispatcher.UIThread.RunJobs(); settings.UpdateLayout();
            var body = settings.GetLogicalDescendants().OfType<ContentControl>().Single(control => control.Name == "SettingsBody");
            var pageScroll = body.GetLogicalAncestors().OfType<ScrollViewer>().First();
            var choice = body.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ThemeMode_fixed");
            var clicks = 0;
            choice.Click += (_, _) => clicks++;
            var choiceLabel = choice.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Text == "Fixed");
            start = Center(choiceLabel);
            finger = settings.TouchBegin(start, RawInputModifiers.None);
            settings.TouchMove(finger, start - new Vector(0, 30), RawInputModifiers.None);
            Require(pageScroll.Offset.Y > 0, "The first touch move over a Settings page choice must scroll without holding.");
            settings.TouchEnd(finger, start - new Vector(0, 30), RawInputModifiers.None);
            Require(clicks == 0, "A Settings page choice must not click after dragging its label.");

            pageScroll.Offset = default;
            Dispatcher.UIThread.RunJobs(); settings.UpdateLayout();
            var heading = body.GetLogicalDescendants().OfType<TextBlock>().First(text => text.IsEffectivelyVisible);
            start = Center(heading);
            finger = settings.TouchBegin(start, RawInputModifiers.None);
            settings.TouchMove(finger, start - new Vector(0, 30), RawInputModifiers.None);
            Require(pageScroll.Offset.Y > 0, "A drag starting on page text must also scroll immediately.");
            settings.TouchEnd(finger, start - new Vector(0, 30), RawInputModifiers.None);
            pageScroll.Offset = default;
            Dispatcher.UIThread.RunJobs(); settings.UpdateLayout();
            start = Center(choiceLabel);
            settings.MouseDown(start, MouseButton.Left);
            settings.MouseUp(start, MouseButton.Left);
            Require(clicks == 1, "The Android touch rule must leave desktop mouse activation unchanged.");
            var close = settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "SettingsClose");
            start = Center(close);
            finger = settings.TouchBegin(start, RawInputModifiers.None);
            settings.TouchMove(finger, start - new Vector(30, 0), RawInputModifiers.None);
            settings.TouchMove(finger, start, RawInputModifiers.None);
            settings.TouchEnd(finger, start, RawInputModifiers.None);
            Require(settings.IsVisible, "A finger that drags away and returns must not click a button outside a scroller.");
            Console.WriteLine("PASS Settings touch: immediate drag over navigation/page buttons and text, no drag click, release tap and mouse activation");

            Point Center(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), settings)!.Value;
        }
        finally { settings.Close(); window.Close(); }
    }
}