using Avalonia;
using Avalonia.Input;
using Avalonia.Input.TextInput;

namespace Ghostty.Avalonia;

public sealed partial class GhosttyTextureView
{
    private TextureInputClient? inputMethod;
    private TextureInputClient InputMethod => inputMethod ??= new(this);
    private uint? pendingTextKey;
    private KeyModifiers pendingTextModifiers;
    private Point linkPointer = new(-1, -1);

    private void RefreshLinkModifiers(KeyModifiers modifiers)
    {
        if (!disposed) Native.Mouse(terminal.Handle, linkPointer.X, linkPointer.Y, Vt.Mods(modifiers), -1, 0);
        if (!disposed) Cursor = new Cursor(Native.OverLink(terminal.Handle) ? StandardCursorType.Hand : StandardCursorType.Ibeam);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (!disposed) Native.Focus(terminal.Handle, true);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        pendingTextKey = null;
        if (!disposed) Native.Focus(terminal.Handle, false);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (disposed || e.Handled) return;
        RefreshLinkModifiers(e.KeyModifiers);
        pendingTextKey = null;
        if (e.KeyModifiers == KeyModifiers.Meta && e.Key is Key.C or Key.V)
        {
            Native.Action(terminal.Handle, e.Key == Key.C ? "copy_to_clipboard" : "paste_from_clipboard");
            e.Handled = true;
            return;
        }
        if (AgentNewline && e.PhysicalKey == PhysicalKey.Enter && e.KeyModifiers == KeyModifiers.Shift && terminal.SendAgentNewline())
        {
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta) || !KeyCodes.TryGetValue(e.PhysicalKey, out var code)) return;
        var text = e.KeySymbol;
        var printable = !string.IsNullOrEmpty(text) && !char.IsControl(text[0]);
        if (printable && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            pendingTextKey = code;
            pendingTextModifiers = e.KeyModifiers;
            return;
        }
        var unshifted = Unshifted(e.PhysicalKey);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && unshifted != 0)
            text = char.ConvertFromUtf32((int)(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? char.ToUpperInvariant((char)unshifted) : unshifted));
        e.Handled = Native.Key(terminal.Handle, 1, code, Vt.Mods(e.KeyModifiers), 0, printable ? text : null, unshifted);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        RefreshLinkModifiers(e.KeyModifiers);
        pendingTextKey = null;
        if (disposed || e.KeyModifiers.HasFlag(KeyModifiers.Meta) || !KeyCodes.TryGetValue(e.PhysicalKey, out var code)) return;
        Native.Key(terminal.Handle, 0, code, Vt.Mods(e.KeyModifiers), 0, null, Unshifted(e.PhysicalKey));
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (disposed || string.IsNullOrEmpty(e.Text)) return;
        Native.Preedit(terminal.Handle, null);
        if (pendingTextKey is { } code)
            Native.Key(terminal.Handle, 1, code, Vt.Mods(pendingTextModifiers),
                pendingTextModifiers.HasFlag(KeyModifiers.Shift) ? 1 : 0, e.Text, 0);
        else Native.Text(terminal.Handle, e.Text);
        pendingTextKey = null;
        e.Handled = true;
    }

    private void Mouse(PointerEventArgs e, int action, int button)
    {
        if (disposed) return;
        var point = e.GetPosition(this);
        linkPointer = point;
        Native.Mouse(terminal.Handle, point.X, point.Y, Vt.Mods(e.KeyModifiers), action, button);
        Cursor = new Cursor(Native.OverLink(terminal.Handle) ? StandardCursorType.Hand : StandardCursorType.Ibeam);
    }

    private static int Button(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 1,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 2,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 3,
        _ => 0
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (disposed) return;
        Focus();
        e.Pointer.Capture(this);
        Mouse(e, 1, Button(e.GetCurrentPoint(this).Properties.PointerUpdateKind));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); Mouse(e, -1, 0); }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        linkPointer = new(-1, -1);
        RefreshLinkModifiers(KeyModifiers.None);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Mouse(e, 0, Button(e.GetCurrentPoint(this).Properties.PointerUpdateKind));
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (disposed) return;
        Mouse(e, -1, 0);
        e.Handled = true;
        // A trackpad gesture arrives as dozens of fractional events, and Ghostty reports each one to a program tracking
        // the mouse as a whole wheel click; only whole steps go through, as the Skia view's accumulator does.
        wheel += e.Delta;
        var step = new Vector(Math.Truncate(wheel.X), Math.Truncate(wheel.Y));
        if (step == default) return;
        wheel -= step;
        Native.Scroll(terminal.Handle, step.X, step.Y);
    }

    private Vector wheel;

    private sealed class TextureInputClient(GhosttyTextureView owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => "";
        public override TextSelection Selection { get => new(0, 0); set { } }
        public override Rect CursorRectangle
        {
            get
            {
                if (owner.disposed) return default;
                Native.ImePoint(owner.terminal.Handle, out var x, out var y, out var width, out var height);
                return new Rect(x, y, width, height);
            }
        }
        public override void SetPreeditText(string? text)
        {
            if (owner.disposed) return;
            owner.pendingTextKey = null;
            Native.Preedit(owner.terminal.Handle, text);
        }
        internal void NotifyCursor() => RaiseCursorRectangleChanged();
    }

    private static uint Unshifted(PhysicalKey key) => key switch
    {
        >= PhysicalKey.A and <= PhysicalKey.Z => (uint)('a' + (key - PhysicalKey.A)),
        >= PhysicalKey.Digit0 and <= PhysicalKey.Digit9 => (uint)('0' + (key - PhysicalKey.Digit0)),
        PhysicalKey.Space => ' ',
        PhysicalKey.Minus => '-',
        PhysicalKey.Equal => '=',
        PhysicalKey.BracketLeft => '[',
        PhysicalKey.BracketRight => ']',
        PhysicalKey.Backslash => '\\',
        PhysicalKey.Semicolon => ';',
        PhysicalKey.Quote => '\'',
        PhysicalKey.Backquote => '`',
        PhysicalKey.Comma => ',',
        PhysicalKey.Period => '.',
        PhysicalKey.Slash => '/',
        _ => 0
    };

    // macOS virtual key codes consumed by libghostty's macOS input backend.
    private static readonly Dictionary<PhysicalKey, uint> KeyCodes = new()
    {
        [PhysicalKey.A] = 0,
        [PhysicalKey.S] = 1,
        [PhysicalKey.D] = 2,
        [PhysicalKey.F] = 3,
        [PhysicalKey.H] = 4,
        [PhysicalKey.G] = 5,
        [PhysicalKey.Z] = 6,
        [PhysicalKey.X] = 7,
        [PhysicalKey.C] = 8,
        [PhysicalKey.V] = 9,
        [PhysicalKey.B] = 11,
        [PhysicalKey.Q] = 12,
        [PhysicalKey.W] = 13,
        [PhysicalKey.E] = 14,
        [PhysicalKey.R] = 15,
        [PhysicalKey.Y] = 16,
        [PhysicalKey.T] = 17,
        [PhysicalKey.Digit1] = 18,
        [PhysicalKey.Digit2] = 19,
        [PhysicalKey.Digit3] = 20,
        [PhysicalKey.Digit4] = 21,
        [PhysicalKey.Digit6] = 22,
        [PhysicalKey.Digit5] = 23,
        [PhysicalKey.Equal] = 24,
        [PhysicalKey.Digit9] = 25,
        [PhysicalKey.Digit7] = 26,
        [PhysicalKey.Minus] = 27,
        [PhysicalKey.Digit8] = 28,
        [PhysicalKey.Digit0] = 29,
        [PhysicalKey.BracketRight] = 30,
        [PhysicalKey.O] = 31,
        [PhysicalKey.U] = 32,
        [PhysicalKey.BracketLeft] = 33,
        [PhysicalKey.I] = 34,
        [PhysicalKey.P] = 35,
        [PhysicalKey.Enter] = 36,
        [PhysicalKey.L] = 37,
        [PhysicalKey.J] = 38,
        [PhysicalKey.Quote] = 39,
        [PhysicalKey.K] = 40,
        [PhysicalKey.Semicolon] = 41,
        [PhysicalKey.Backslash] = 42,
        [PhysicalKey.Comma] = 43,
        [PhysicalKey.Slash] = 44,
        [PhysicalKey.N] = 45,
        [PhysicalKey.M] = 46,
        [PhysicalKey.Period] = 47,
        [PhysicalKey.Tab] = 48,
        [PhysicalKey.Space] = 49,
        [PhysicalKey.Backquote] = 50,
        [PhysicalKey.Backspace] = 51,
        [PhysicalKey.Escape] = 53,
        [PhysicalKey.IntlBackslash] = 10,
        [PhysicalKey.IntlRo] = 94,
        [PhysicalKey.IntlYen] = 93,
        [PhysicalKey.NumPadDecimal] = 65,
        [PhysicalKey.NumPadMultiply] = 67,
        [PhysicalKey.NumPadAdd] = 69,
        [PhysicalKey.NumLock] = 71,
        [PhysicalKey.NumPadDivide] = 75,
        [PhysicalKey.NumPadEnter] = 76,
        [PhysicalKey.NumPadSubtract] = 78,
        [PhysicalKey.NumPadEqual] = 81,
        [PhysicalKey.NumPad0] = 82,
        [PhysicalKey.NumPad1] = 83,
        [PhysicalKey.NumPad2] = 84,
        [PhysicalKey.NumPad3] = 85,
        [PhysicalKey.NumPad4] = 86,
        [PhysicalKey.NumPad5] = 87,
        [PhysicalKey.NumPad6] = 88,
        [PhysicalKey.NumPad7] = 89,
        [PhysicalKey.NumPad8] = 91,
        [PhysicalKey.NumPad9] = 92,
        [PhysicalKey.NumPadComma] = 95,
        [PhysicalKey.F1] = 122,
        [PhysicalKey.F2] = 120,
        [PhysicalKey.F3] = 99,
        [PhysicalKey.F4] = 118,
        [PhysicalKey.F5] = 96,
        [PhysicalKey.F6] = 97,
        [PhysicalKey.F7] = 98,
        [PhysicalKey.F8] = 100,
        [PhysicalKey.F9] = 101,
        [PhysicalKey.F10] = 109,
        [PhysicalKey.F11] = 103,
        [PhysicalKey.F12] = 111,
        [PhysicalKey.F13] = 105,
        [PhysicalKey.F14] = 107,
        [PhysicalKey.F15] = 113,
        [PhysicalKey.F16] = 106,
        [PhysicalKey.F17] = 64,
        [PhysicalKey.F18] = 79,
        [PhysicalKey.F19] = 80,
        [PhysicalKey.F20] = 90,
        [PhysicalKey.Insert] = 114,
        [PhysicalKey.Home] = 115,
        [PhysicalKey.PageUp] = 116,
        [PhysicalKey.Delete] = 117,
        [PhysicalKey.End] = 119,
        [PhysicalKey.PageDown] = 121,
        [PhysicalKey.ArrowLeft] = 123,
        [PhysicalKey.ArrowRight] = 124,
        [PhysicalKey.ArrowDown] = 125,
        [PhysicalKey.ArrowUp] = 126
    };
}