using System.Diagnostics;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Ghostty.Avalonia;

public sealed partial class GhosttySkiaView
{
    private const int Press = 1, Release = 0, Motion = 2;
    private const double TapSlop = 8;
    private InputClient? inputMethod;
    private string preedit = "";
    private int selecting = -1;
    private double wheel;
    private Point? touch;
    private bool tapping;

    private InputClient InputMethod => inputMethod ??= new InputClient(this);

    /// <summary>Whether the input method edits a text field of the typed text, as Android's input connection requires.</summary>
    internal bool InputField { get; set; } = OperatingSystem.IsAndroid();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (disposed || e.Handled) return;
        var mods = e.KeyModifiers;
        linkModifiers = mods;
        Redraw(false);
        if (InputMethod.Erase(e)) { e.Handled = true; return; }
        var copyPaste = OperatingSystem.IsMacOS() ? mods == KeyModifiers.Meta : mods == (KeyModifiers.Control | KeyModifiers.Shift);
        if (copyPaste && e.Key is Key.C or Key.V)
        {
            _ = ClipboardAsync(e.Key == Key.V);
            e.Handled = true;
            return;
        }
        // Command shortcuts belong to the application, as in Ghostty's own macOS app.
        if (OperatingSystem.IsMacOS() && mods.HasFlag(KeyModifiers.Meta)) return;
        if (Encode(e, Press)) e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        linkModifiers = e.KeyModifiers;
        Redraw(false);
        if (disposed || e.Handled || OperatingSystem.IsMacOS() && e.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
        // Only the Kitty keyboard protocol reports releases; the encoder returns nothing otherwise.
        Encode(e, Release);
    }

    private bool Encode(KeyEventArgs e, int action)
    {
        var mods = e.KeyModifiers;
        var physical = Physical(e);
        // Match Ghostty's macOS word-navigation bindings before protocol encoding.
        if (OperatingSystem.IsMacOS() && mods == KeyModifiers.Alt && physical is PhysicalKey.ArrowLeft or PhysicalKey.ArrowRight)
        {
            if (action == Press) Typed(physical == PhysicalKey.ArrowLeft ? "\u001bb"u8 : "\u001bf"u8);
            return true;
        }
        var symbol = e.KeySymbol;
        var printable = !string.IsNullOrEmpty(symbol) && !char.IsControl(symbol[0]);
        var alt = mods.HasFlag(KeyModifiers.Alt) && (OptionAsAlt || !OperatingSystem.IsMacOS());
        // Plain text arrives through TextInput, so input methods and dead keys compose.
        if (action == Press && printable && !mods.HasFlag(KeyModifiers.Control) && !alt) return false;
        var unshifted = Unshifted(physical);
        // With Option as Alt the composed character is not the key's text; send the key itself.
        string? text = alt && unshifted != 0 ? char.ConvertFromUtf32(mods.HasFlag(KeyModifiers.Shift) ? char.ToUpperInvariant((char)unshifted) : (int)unshifted)
            : printable ? symbol : null;
        if (unshifted == 0 && printable && symbol!.Length == 1) unshifted = char.ToLowerInvariant(symbol[0]);
        var consumed = (ushort)(printable && mods.HasFlag(KeyModifiers.Shift) ? 1 : 0);
        var bytes = vt.Key(action, Vt.KeyCode(physical), Vt.Mods(mods), consumed, action == Release ? null : text, unshifted);
        if (bytes.IsEmpty) return false;
        // The legacy encoding of Shift+Enter is a bare CR; Kitty and modifyOtherKeys encode the modifier themselves.
        if (AgentNewline && action == Press && physical == PhysicalKey.Enter && mods == KeyModifiers.Shift && bytes.SequenceEqual("\r"u8))
            bytes = "\u001b\r"u8;
        Typed(bytes);
        return true;
    }

    // A soft keyboard's keys carry no scan code, so only the logical key says which one it was.
    private static PhysicalKey Physical(KeyEventArgs e) => e.PhysicalKey != PhysicalKey.None ? e.PhysicalKey : e.Key switch
    {
        Key.Enter => PhysicalKey.Enter,
        Key.Back => PhysicalKey.Backspace,
        Key.Tab => PhysicalKey.Tab,
        Key.Escape => PhysicalKey.Escape,
        Key.Delete => PhysicalKey.Delete,
        Key.Left => PhysicalKey.ArrowLeft,
        Key.Right => PhysicalKey.ArrowRight,
        Key.Up => PhysicalKey.ArrowUp,
        Key.Down => PhysicalKey.ArrowDown,
        Key.Home => PhysicalKey.Home,
        Key.End => PhysicalKey.End,
        Key.PageUp => PhysicalKey.PageUp,
        Key.PageDown => PhysicalKey.PageDown,
        >= Key.A and <= Key.Z => PhysicalKey.A + (e.Key - Key.A),
        _ => PhysicalKey.None
    };

    // The US-layout character of a physical key, which terminals report as the unshifted code point.
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

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (disposed || string.IsNullOrEmpty(e.Text)) return;
        preedit = "";
        // A soft keyboard commits Enter as a line feed.
        var text = e.Text.Replace("\r\n", "\r").Replace('\n', '\r');
        InputMethod.Typed(text);
        Typed(Encoding.UTF8.GetBytes(text), text: true);
        e.Handled = true;
    }

    // Typing returns a scrolled-back viewport to the prompt.
    private void Typed(ReadOnlySpan<byte> bytes, bool text = false)
    {
        if (!text) InputMethod.Clear();
        if (last.ScrollTotal > last.ScrollOffset + last.ScrollLength) vt.Scroll(0);
        Send(bytes);
        cursorOn = true;
        Redraw();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (!disposed) Send(vt.Focus(true));
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        SoftKeyboard(false);
        if (!disposed) Send(vt.Focus(false));
    }

    // Android raises the keyboard for whichever focused control takes text. Focus reaches a terminal without
    // the user asking to type (a tab chosen elsewhere, a restored layout), so it takes text only once tapped.
    private void SoftKeyboard(bool wanted)
    {
        if (OperatingSystem.IsAndroid()) global::Avalonia.Input.InputMethod.SetIsInputMethodEnabled(this, wanted);
    }

    // Grid-relative device pixels, the space Ghostty's mouse and selection encoders work in.
    private Point Grid(PointerEventArgs e)
    {
        var point = e.GetPosition(this);
        return new Point((point.X - Padding.Left) * fontKey.Scale, (point.Y - Padding.Top) * fontKey.Scale);
    }

    private static int Button(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => 1,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => 2,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => 3,
        _ => 0
    };

    private bool Track(int action, int button, PointerEventArgs e)
    {
        // Shift bypasses mouse reporting so a tracking program's text can still be selected.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return false;
        var point = Grid(e);
        // Ghostty's mouse actions use the opposite press/release values from its key actions.
        var mouseAction = action == Press ? 0 : action == Release ? 1 : 2;
        var bytes = vt.Mouse(mouseAction, button, Vt.Mods(e.KeyModifiers), (float)point.X, (float)point.Y, out var tracked);
        Send(bytes);
        return tracked;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (disposed) return;
        Focus();
        var properties = e.GetCurrentPoint(this).Properties;
        var button = Button(properties.PointerUpdateKind);
        e.Pointer.Capture(this);
        e.Handled = true;
        // A finger scrolls by dragging and selects after a hold, so it clicks only once it lifts without having moved.
        if (e.Pointer.Type == PointerType.Touch)
        {
            touch = e.GetPosition(this);
            tapping = true;
            return;
        }
        RefreshLink(e);
        if (button == 1 && hoveredUrl is { } url)
        {
            pressedUrl = url;
            linkPressPointer = e.GetPosition(this);
            return;
        }
        if (Track(Press, button, e) || button != 1) return;
        var point = Grid(e);
        selecting = 0;
        vt.Select(0, point.X, point.Y, Now());
        Redraw();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (disposed) return;
        RefreshLink(e);
        if (selecting >= 0)
        {
            var point = Grid(e);
            selecting = 1;
            vt.Select(1, point.X, point.Y, Now());
            Redraw();
            return;
        }
        if (e.Pointer.Type == PointerType.Touch)
        {
            if (touch is not { } from || fonts is null) return;
            var to = e.GetPosition(this);
            if (tapping && Math.Abs(to.Y - from.Y) < TapSlop) return;
            tapping = false;
            touch = to;
            Scroll((to.Y - from.Y) / fonts.CellHeight, e);
            return;
        }
        var properties = e.GetCurrentPoint(this).Properties;
        var held = properties.IsLeftButtonPressed ? 1 : properties.IsRightButtonPressed ? 2 : properties.IsMiddleButtonPressed ? 3 : 0;
        Track(Motion, held, e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (disposed) return;
        if (touch is not null)
        {
            // Releasing the capture ends the gesture, so read what it was first.
            var (held, tap) = (selecting >= 0, tapping);
            e.Pointer.Capture(null);
            if (held)
            {
                var lifted = Grid(e);
                vt.Select(2, lifted.X, lifted.Y, Now());
                Redraw();
                ShowClipboardMenu();
            }
            else if (tap)
            {
                vt.ClearSelection();
                Redraw();
                if (Track(Press, 1, e)) Track(Release, 1, e);
                SoftKeyboard(true);
                InputMethod.ShowPanel();
            }
            return;
        }
        var link = pressedUrl;
        pressedUrl = null;
        RefreshLink(e);
        e.Pointer.Capture(null);
        if (link is not null)
        {
            var delta = e.GetPosition(this) - linkPressPointer;
            if (hoveredUrl == link && delta.X * delta.X + delta.Y * delta.Y <= 16) _ = OpenLinkAsync(link);
            e.Handled = true;
            return;
        }
        if (selecting >= 0)
        {
            var point = Grid(e);
            selecting = -1;
            vt.Select(2, point.X, point.Y, Now());
            Redraw();
            return;
        }
        Track(Release, Button(e.GetCurrentPoint(this).Properties.PointerUpdateKind), e);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        selecting = -1;
        pressedUrl = null;
        touch = null;
        tapping = false;
    }

    // A held finger selects the word under it and extends the selection as it drags; lifting offers the clipboard.
    protected override void OnHolding(HoldingRoutedEventArgs e)
    {
        base.OnHolding(e);
        if (disposed || !tapping || e.HoldingState != HoldingState.Started || e.PointerType != PointerType.Touch) return;
        tapping = false;
        var x = (e.Position.X - Padding.Left) * fontKey.Scale;
        var y = (e.Position.Y - Padding.Top) * fontKey.Scale;
        var now = Now();
        // A second press in place is Ghostty's double click, which selects by word.
        vt.Select(0, x, y, now);
        vt.Select(2, x, y, now);
        vt.Select(0, x, y, now);
        selecting = 0;
        Redraw();
        e.Handled = true;
    }

    internal ContextMenu? ClipboardMenu { get; private set; }

    private void ShowClipboardMenu()
    {
        var copy = new MenuItem { Header = "Copy", IsEnabled = SelectedText.Length > 0 };
        copy.Click += (_, _) => _ = ClipboardAsync(false);
        var paste = new MenuItem { Header = "Paste" };
        paste.Click += (_, _) => _ = ClipboardAsync(true);
        var all = new MenuItem { Header = "Select All" };
        all.Click += (_, _) => { SelectAll(); Dispatcher.UIThread.Post(ShowClipboardMenu); };
        ClipboardMenu = new ContextMenu { Items = { copy, paste, all } };
        ClipboardMenu.Open(this);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (disposed) return;
        e.Handled = true;
        Scroll(e.Delta.Y * 3, e);
    }

    // Lines toward the history when positive; fractions carry over to the next gesture.
    private void Scroll(double delta, PointerEventArgs e)
    {
        wheel += delta;
        var lines = (int)Math.Truncate(wheel);
        if (lines == 0) return;
        wheel -= lines;
        var up = lines > 0;
        var count = Math.Min(Math.Abs(lines), 50);
        // Programs that track the mouse get wheel buttons; the alternate screen gets arrow keys, as Ghostty's
        // default alternate-scroll mode sends; the primary screen scrolls its history.
        if (Track(Press, up ? 4 : 5, e))
        {
            for (var i = 1; i < count; i++) Track(Press, up ? 4 : 5, e);
            return;
        }
        if (last.AlternateScreen != 0)
        {
            var key = Vt.KeyCode(up ? PhysicalKey.ArrowUp : PhysicalKey.ArrowDown);
            for (var i = 0; i < count; i++) Send(vt.Key(Press, key, 0, 0, null, 0));
            return;
        }
        vt.Scroll(up ? -count : count);
        Redraw();
    }

    private static ulong Now() => (ulong)(Stopwatch.GetTimestamp() * (1_000_000_000.0 / Stopwatch.Frequency));

    private async Task CopyAsync(string text)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            if (text.Length == 0) await clipboard.ClearAsync(); else await clipboard.SetTextAsync(text);
        }
        catch (Exception error) { OperationFailed?.Invoke(this, error); }
    }

    private async Task ClipboardAsync(bool paste)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            if (!paste)
            {
                var selected = SelectedText;
                if (selected.Length > 0) await clipboard.SetTextAsync(selected);
                return;
            }
            string? text = null;
            if (ClipboardImageDirectory is { } directory && await clipboard.TryGetBitmapAsync() is { } image)
            {
                using (image)
                {
                    Directory.CreateDirectory(directory);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    var path = Path.Combine(directory, Guid.NewGuid().ToString("D").ToUpperInvariant() + ".png");
                    image.Save(path, PngBitmapEncoderOptions.Default);
                    text = "'" + path.Replace("'", "'\\''") + "'";
                }
            }
            text ??= await clipboard.TryGetTextAsync();
            if (disposed || string.IsNullOrEmpty(text)) return;
            Typed(vt.Paste(text));
        }
        catch (Exception error) { OperationFailed?.Invoke(this, error); }
    }

    private Rect CursorRect()
    {
        if (fonts is null) return default;
        return new Rect(Padding.Left + last.CursorX * fonts.CellWidth, Padding.Top + last.CursorY * fonts.CellHeight, fonts.CellWidth, fonts.CellHeight);
    }

    private sealed class InputClient(GhosttySkiaView owner) : TextInputMethodClient
    {
        // Android's input connection edits a text field: it reads the text around the caret and replaces what it is
        // composing by selecting it, pressing Delete and typing again. A terminal has no field, so this is the text
        // typed since the last key that was not text, and its Delete erases with Backspace.
        private readonly StringBuilder typed = new();
        private TextSelection selection;

        public override Visual TextViewVisual => owner;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => owner.InputField;
        public override string SurroundingText => typed.ToString();
        public override Rect CursorRectangle => owner.CursorRect();
        public override TextSelection Selection
        {
            get => selection;
            set => selection = new(Math.Clamp(Math.Min(value.Start, value.End), 0, typed.Length), Math.Clamp(Math.Max(value.Start, value.End), 0, typed.Length));
        }

        public override void SetPreeditText(string? preeditText)
        {
            owner.preedit = preeditText ?? "";
            owner.Redraw();
            RaiseCursorRectangleChanged();
        }

        public override void ExecuteContextMenuAction(ContextMenuAction action)
        {
            if (action == ContextMenuAction.SelectAll) owner.SelectAll();
            else if (action is ContextMenuAction.Copy or ContextMenuAction.Paste) _ = owner.ClipboardAsync(action == ContextMenuAction.Paste);
        }

        internal void ShowPanel() => RaiseInputPaneActivationRequested();

        internal void Typed(string text)
        {
            if (!owner.InputField) return;
            if (text.Contains('\r')) { Clear(); return; }
            typed.Insert(selection.Start, text);
            selection = new(selection.Start + text.Length, selection.Start + text.Length);
            RaiseSelectionChanged();
        }

        internal void Clear()
        {
            if (typed.Length == 0) return;
            typed.Clear();
            selection = default;
            RaiseSelectionChanged();
        }

        // The input connection's own Delete carries no scan code, unlike a keyboard's.
        internal bool Erase(KeyEventArgs e)
        {
            if (!owner.InputField || e.Key != Key.Delete || e.PhysicalKey != PhysicalKey.None) return false;
            var erased = typed.ToString(selection.Start, selection.End - selection.Start);
            typed.Remove(selection.Start, erased.Length);
            selection = new(selection.Start, selection.Start);
            foreach (var _ in erased.EnumerateRunes())
                owner.Typed(owner.vt.Key(Press, Vt.KeyCode(PhysicalKey.Backspace), 0, 0, null, 0), text: true);
            return true;
        }
    }
}