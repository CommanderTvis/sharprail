using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using Ghostty.Avalonia;

namespace SharpRail.UI.Terminal;

internal sealed partial class TerminalKeyBar : UserControl
{
    internal TerminalKeyBar(GhosttySkiaView terminal)
    {
        AvaloniaXamlLoader.Load(this);
        var ctrl = this.FindControl<ToggleButton>("CtrlKey")!;
        var alt = this.FindControl<ToggleButton>("AltKey")!;
        void Modifiers() => terminal.PendingModifiers =
            (ctrl.IsChecked == true ? KeyModifiers.Control : KeyModifiers.None) |
            (alt.IsChecked == true ? KeyModifiers.Alt : KeyModifiers.None);
        ctrl.Click += (_, _) => Modifiers();
        alt.Click += (_, _) => Modifiers();
        terminal.PendingModifiersConsumed += (_, _) => { ctrl.IsChecked = false; alt.IsChecked = false; };
        this.FindControl<Button>("EscapeKey")!.Click += (_, _) => terminal.SendKey(Key.Escape);
        this.FindControl<Button>("TabKey")!.Click += (_, _) => terminal.SendKey(Key.Tab);
        this.FindControl<Button>("LeftKey")!.Click += (_, _) => terminal.SendKey(Key.Left);
        this.FindControl<Button>("DownKey")!.Click += (_, _) => terminal.SendKey(Key.Down);
        this.FindControl<Button>("UpKey")!.Click += (_, _) => terminal.SendKey(Key.Up);
        this.FindControl<Button>("RightKey")!.Click += (_, _) => terminal.SendKey(Key.Right);
        this.FindControl<Button>("HomeKey")!.Click += (_, _) => terminal.SendKey(Key.Home);
        this.FindControl<Button>("EndKey")!.Click += (_, _) => terminal.SendKey(Key.End);
        this.FindControl<Button>("PageUpKey")!.Click += (_, _) => terminal.SendKey(Key.PageUp);
        this.FindControl<Button>("PageDownKey")!.Click += (_, _) => terminal.SendKey(Key.PageDown);
    }
}