using System.Diagnostics;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media.Imaging;

namespace Ghostty.Avalonia;

public sealed partial class GhosttySkiaView
{
    private const int Press = 1, Release = 0, Motion = 2;
    private InputClient? inputMethod;
    private string preedit = "";
    private int selecting = -1;
    private double wheel;

    private InputClient InputMethod => inputMethod ??= new InputClient(this);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (disposed || e.Handled) return;
        var mods = e.KeyModifiers;
        linkModifiers = mods;
        Redraw(false);
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
        // Match Ghostty's macOS word-navigation bindings before protocol encoding.
        if (OperatingSystem.IsMacOS() && mods == KeyModifiers.Alt && e.PhysicalKey is PhysicalKey.ArrowLeft or PhysicalKey.ArrowRight)
        {
            if (action == Press) Typed(e.PhysicalKey == PhysicalKey.ArrowLeft ? "\u001bb"u8 : "\u001bf"u8);
            return true;
        }
        var symbol = e.KeySymbol;
        var printable = !string.IsNullOrEmpty(symbol) && !char.IsControl(symbol[0]);
        var alt = mods.HasFlag(KeyModifiers.Alt) && (OptionAsAlt || !OperatingSystem.IsMacOS());
        // Plain text arrives through TextInput, so input methods and dead keys compose.
        if (action == Press && printable && !mods.HasFlag(KeyModifiers.Control) && !alt) return false;
        var unshifted = Unshifted(e.PhysicalKey);
        // With Option as Alt the composed character is not the key's text; send the key itself.
        string? text = alt && unshifted != 0 ? char.ConvertFromUtf32(mods.HasFlag(KeyModifiers.Shift) ? char.ToUpperInvariant((char)unshifted) : (int)unshifted)
            : printable ? symbol : null;
        if (unshifted == 0 && printable && symbol!.Length == 1) unshifted = char.ToLowerInvariant(symbol[0]);
        var consumed = (ushort)(printable && mods.HasFlag(KeyModifiers.Shift) ? 1 : 0);
        var bytes = vt.Key(action, Vt.KeyCode(e.PhysicalKey), Vt.Mods(mods), consumed, action == Release ? null : text, unshifted);
        if (bytes.IsEmpty) return false;
        // The legacy encoding of Shift+Enter is a bare CR; Kitty and modifyOtherKeys encode the modifier themselves.
        if (AgentNewline && action == Press && e.PhysicalKey == PhysicalKey.Enter && mods == KeyModifiers.Shift && bytes.SequenceEqual("\r"u8))
            bytes = "\u001b\r"u8;
        Typed(bytes);
        return true;
    }

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
        Typed(Encoding.UTF8.GetBytes(e.Text));
        e.Handled = true;
    }

    // Typing returns a scrolled-back viewport to the prompt.
    private void Typed(ReadOnlySpan<byte> bytes)
    {
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
        if (!disposed) Send(vt.Focus(false));
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
        var properties = e.GetCurrentPoint(this).Properties;
        var held = properties.IsLeftButtonPressed ? 1 : properties.IsRightButtonPressed ? 2 : properties.IsMiddleButtonPressed ? 3 : 0;
        Track(Motion, held, e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (disposed) return;
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
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (disposed) return;
        e.Handled = true;
        wheel += e.Delta.Y * 3;
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
        public override Visual TextViewVisual => owner;
        public override bool SupportsPreedit => true;
        public override bool SupportsSurroundingText => false;
        public override string SurroundingText => "";
        public override Rect CursorRectangle => owner.CursorRect();
        public override TextSelection Selection { get; set; }

        public override void SetPreeditText(string? preeditText)
        {
            owner.preedit = preeditText ?? "";
            owner.Redraw();
            RaiseCursorRectangleChanged();
        }
    }
}