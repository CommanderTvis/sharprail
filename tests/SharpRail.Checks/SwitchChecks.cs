using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

using SharpRail.Plugins.UI.Kit;
using SharpRail.UI.Rendering;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static class SwitchChecks
{
    internal static void Run()
    {
        var theme = Ui.Theme;
        var fixture = new SwitchFixture();
        var changes = fixture.Changes;
        var control = fixture.FindControl<Switch>("FeatureSwitch")!;
        var convenience = new Switch("Convenience switch", true, changes.Add);
        Require(convenience.Label == "Convenience switch" && convenience.IsChecked && changes.Count == 0,
            "The convenience constructor initializes controlled state without requesting a change.");
        var window = new Window
        {
            Width = 120,
            Height = 90,
            Content = new Border { Child = fixture, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        try
        {
            window.Show();
            Settle();
            var peer = ControlAutomationPeer.CreatePeerForElement(control)!;
            var toggle = peer.GetProvider<IToggleProvider>()!;
            Require(peer.GetName() == "Enable feature" && peer.GetAutomationControlType() == AutomationControlType.CheckBox &&
                toggle.ToggleState == ToggleState.Off, "The switch exposes its accessible label and unchecked state.");
            Require(!control.GetVisualDescendants().OfType<TextBlock>().Any(), "The switch has no visible On/Off text.");
            Require(control.Bounds.Size == new Size(40, 24), "The switch retains the fork's target dimensions.");
            var track = control.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "Track");
            var thumb = control.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "Thumb");
            Require(track.Bounds.Size == new Size(36, 20) && thumb.Bounds.Size == new Size(16, 16) &&
                thumb.TranslatePoint(default, track)!.Value.X == 2, "The unchecked switch retains the track and thumb geometry.");

            void Click()
            {
                var point = control.TranslatePoint(new Point(20, 12), window)!.Value;
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Settle();
            }
            Click();
            Require(changes.SequenceEqual([true]) && !control.IsChecked, "One mouse activation requests the next controlled state once.");
            var automationChanges = 0;
            peer.PropertyChanged += (_, _) => automationChanges++;
            control.IsChecked = true;
            Settle();
            Require(changes.Count == 1 && automationChanges == 1 && toggle.ToggleState == ToggleState.On && thumb.TranslatePoint(default, track)!.Value.X == 18,
                "Updating the controlled state moves the thumb and updates automation without a callback.");
            control.Label = "Updated label";
            Require(peer.GetName() == "Updated label", "A changed bound label updates accessibility.");
            control.Focus();
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            Settle();
            Require(changes.SequenceEqual([true, false]), "Space requests the next state once.");
            toggle.Toggle();
            Require(changes.SequenceEqual([true, false, false]) && control.IsChecked, "Automation requests the controlled next state once.");
            control.IsEnabled = false;
            Click();
            toggle.Toggle();
            Require(changes.Count == 3, "Disabled mouse and automation activation make no request.");
            foreach (var palette in Themes.All)
            {
                Ui.Apply(palette);
                Require(ReferenceEquals(track.Background, Ui.PrimaryDisabled) && ReferenceEquals(thumb.Background, Ui.ControlDisabledText) &&
                    Ui.PrimaryDisabled.Color == Ui.Alpha(palette["accent"], 60), "Disabled checked colors follow each theme's exact alpha tokens.");
                control.IsChecked = false;
                Require(ReferenceEquals(track.Background, Ui.ControlDisabledBorder), "The disabled unchecked track uses the border alpha token.");
                control.IsChecked = true;
            }
            control.IsEnabled = true;
            control.IsChecked = false;
            EventHandler<RoutedEventArgs> update = (_, _) => control.IsChecked = true;
            control.Click += update;
            Click();
            Require(changes.Count == 4 && changes[^1], "The request uses the activation's original state even if a click handler updates it.");
            control.Click -= update;
            control.Click += (_, args) => args.Handled = true;
            Click();
            Require(changes.Count == 4, "A handled click prevents the change request.");
            Console.WriteLine("PASS fork shared Switch: controlled state, geometry, mouse/keyboard/automation, disabled themes and handled clicks");
        }
        finally
        {
            window.Close();
            Ui.Apply(theme);
        }
    }
}