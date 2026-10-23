using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using Ghostty.Avalonia;

namespace SharpRail.UI.Terminal;

internal sealed partial class TerminalKeyBar : UserControl
{
    private readonly GhosttySkiaView terminal;
    private IInputPane? pane;
    private bool keyboardOpen;
    private bool retired;

    internal TerminalKeyBar(GhosttySkiaView terminal)
    {
        this.terminal = terminal;
        AvaloniaXamlLoader.Load(this);
        // Shown only while the soft keyboard is up for this focused terminal, like Termux's extra keys.
        terminal.GotFocus += (_, _) => Refresh();
        terminal.LostFocus += (_, _) => Refresh();
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

    /// <summary>Whether the soft keyboard is open; the input pane sets it, checks set it directly.</summary>
    internal bool KeyboardOpen
    {
        get => keyboardOpen;
        set { keyboardOpen = value; Refresh(); }
    }

    /// <summary>Hides the bar for good once its terminal has exited.</summary>
    internal void Retire() { retired = true; Refresh(); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        pane = TopLevel.GetTopLevel(this)?.InputPane;
        if (pane is null) return;
        pane.StateChanged += PaneChanged;
        KeyboardOpen = pane.State == InputPaneState.Open;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (pane is not null) pane.StateChanged -= PaneChanged;
        pane = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void PaneChanged(object? sender, InputPaneStateEventArgs e) => KeyboardOpen = e.NewState == InputPaneState.Open;

    private void Refresh() => IsVisible = !retired && keyboardOpen && terminal.IsFocused;
}