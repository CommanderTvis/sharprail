using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.UI.Rendering;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.Design;

/// <summary>Roles that states and containers depend on, read back from real controls under every bundled theme.</summary>
internal static class RoleChecks
{
    internal static void Run()
    {
        var original = Ui.Theme;
        foreach (var theme in Themes.All)
        {
            Ui.Apply(theme);
            Require(Ui.Workspace.Color == theme["background"] && Ui.Terminal.Color == theme["sidebar"] && Ui.Surface.Color == theme["content"] &&
                Ui.Selected.Color == theme["hover"] && Ui.ControlFill.Color == theme["input"] && Ui.ControlBorder.Color == theme["border"] &&
                Ui.ControlBorderActive.Color == theme["borderStrong"],
                $"{theme.Id}: container and control roles must read their own palette keys.");
            Require(Ui.ControlDisabledText.Color == Ui.Alpha(theme["text"], 60) && Ui.ControlDisabledFill.Color == Ui.Alpha(theme["input"], 60) &&
                Ui.ControlDisabledBorder.Color == Ui.Alpha(theme["border"], 60) && Ui.PrimaryDisabledFill.Color == Ui.Alpha(theme["accent"], 60) &&
                Ui.PrimaryDisabledText.Color == Ui.Alpha(theme["onAccent"], 60) && Ui.PrimarySoft.Color == Ui.Alpha(theme["accent"], 20),
                $"{theme.Id}: a disabled role is its enabled colour at the 60% step.");
        }
        Ui.Apply(original);

        var labelled = Ui.Button("Save", () => { }, "check");
        var plain = new Button { Content = "Plain" };
        var input = new TextBox { Text = "value" };
        var window = new Window { Width = 320, Height = 200, Content = new StackPanel { Children = { labelled, plain, input } } };
        window.Show(); Settle();
        var row = (StackPanel)labelled.Content!;
        var icon = (Border)row.Children[0];
        var label = (TextBlock)row.Children[1];
        static ContentPresenter Presenter(Button button) => button.GetVisualDescendants().OfType<ContentPresenter>().First(part => part.Name == "PART_ContentPresenter");
        Require(ReferenceEquals(label.Foreground, labelled.Foreground) && ReferenceEquals(icon.Background, labelled.Foreground), "An enabled button's label and icon rest on their own colour.");
        Require(ReferenceEquals(input.GetVisualDescendants().OfType<Border>().First(part => part.Name == "PART_BorderElement").Background, Ui.ControlFill),
            "A resting input must sit on the control fill.");

        labelled.IsEnabled = false; plain.IsEnabled = false; input.IsEnabled = false; Settle();
        Require(ReferenceEquals(label.Foreground, Ui.ControlDisabledText) && ReferenceEquals(icon.Background, Ui.ControlDisabledText),
            "A disabled button must repaint its label and icon from the disabled text role.");
        foreach (var button in new[] { labelled, plain })
            Require(ReferenceEquals(Presenter(button).Background, Ui.ControlDisabledFill) && ReferenceEquals(Presenter(button).BorderBrush, Ui.ControlDisabledBorder),
                "A disabled button must take the disabled fill and border roles rather than Fluent's.");
        Require(ReferenceEquals(Presenter(plain).Foreground, Ui.ControlDisabledText), "A disabled button's own text must take the disabled text role.");
        var border = input.GetVisualDescendants().OfType<Border>().First(part => part.Name == "PART_BorderElement");
        Require(ReferenceEquals(border.Background, Ui.ControlDisabledFill) && ReferenceEquals(border.BorderBrush, Ui.ControlDisabledBorder),
            "A disabled input must take the disabled fill and border roles.");

        labelled.IsEnabled = true; input.IsEnabled = true; Settle();
        Require(ReferenceEquals(label.Foreground, labelled.Foreground) && ReferenceEquals(icon.Background, labelled.Foreground), "Re-enabling a button must restore its label and icon colours.");
        input.Focus(); Settle();
        Require(ReferenceEquals(border.BorderBrush, Ui.ControlBorderActive) && ReferenceEquals(border.Background, Ui.ControlFill),
            "A focused input strengthens its border neutrally and keeps the control fill.");
        window.Close();
        Console.WriteLine("PASS container, selected, disabled and active roles reach buttons and inputs under every theme");
        SwitchControl();
    }

    private static void SwitchControl()
    {
        var toggle = new Switch { Label = "Limit line width" };
        toggle.CheckedChange += requested => toggle.IsChecked = requested;
        var window = new Window { Width = 320, Height = 120, Content = toggle };
        window.Show(); Settle();
        Border Part(string name) => toggle.GetVisualDescendants().OfType<Border>().Single(part => part.Name == name);
        var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(toggle);
        Avalonia.Automation.Provider.IToggleProvider state() => peer.GetProvider<Avalonia.Automation.Provider.IToggleProvider>()!;
        Require(Part("Track").Bounds.Size == new Avalonia.Size(36, 20) && ReferenceEquals(Part("Track").Background, Ui.BorderBrush) &&
            Part("Thumb").HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Left && state().ToggleState == Avalonia.Automation.Provider.ToggleState.Off,
            "An unchecked switch rests its thumb at the left of a border-role track and reports Off to assistive technology.");
        var point = toggle.TranslatePoint(new Avalonia.Point(10, toggle.Bounds.Height / 2), window)!.Value;
        Avalonia.Headless.HeadlessWindowExtensions.MouseDown(window, point, Avalonia.Input.MouseButton.Left);
        Avalonia.Headless.HeadlessWindowExtensions.MouseUp(window, point, Avalonia.Input.MouseButton.Left); Settle();
        Require(toggle.IsChecked == true && ReferenceEquals(Part("Track").Background, Ui.Accent) &&
            Part("Thumb").Margin.Left == 18 && ReferenceEquals(Part("Thumb").Background, Ui.OnPrimary) &&
            state().ToggleState == Avalonia.Automation.Provider.ToggleState.On,
            "Clicking the track checks the switch: primary track, thumb at the right, On for assistive technology.");
        Require(toggle.GetVisualDescendants().OfType<TextBlock>().All(text => text.Text is not ("On" or "Off")),
            "A switch never spells its state out as On or Off.");
        toggle.IsEnabled = false; Settle();
        Require(ReferenceEquals(Part("Track").Background, Ui.PrimaryDisabledFill) && ReferenceEquals(Part("Thumb").Background, Ui.ControlDisabledText),
            "A disabled switch paints from the disabled roles.");
        window.Close();
        Console.WriteLine("PASS the shared switch toggles by pointer, exposes its state accessibly and paints from roles");
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}