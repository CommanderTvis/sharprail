using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Controls;

namespace SharpRail.UI.Editor;

public sealed partial class ScintillaEditor
{
    private bool draggingSelection;
    private Vector wheel;
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (disposed || string.IsNullOrEmpty(e.Text)) return;
        document.Input(e.Text); Changed(); e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (disposed) return;
        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool option = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (command && e.Key is Key.C or Key.X or Key.V)
        { _ = ClipboardAsync(e.Key); e.Handled = true; return; }
        ScintillaMessage? message = (command, option, e.Key) switch
        {
            (true, _, Key.A) => ScintillaMessage.SelectAll,
            (true, _, Key.Z) => shift ? ScintillaMessage.Redo : ScintillaMessage.Undo,
            (true, _, Key.Left) => shift ? ScintillaMessage.VCHomeExtend : ScintillaMessage.VCHome,
            (true, _, Key.Right) => shift ? ScintillaMessage.LineEndExtend : ScintillaMessage.LineEnd,
            (true, _, Key.Up) => shift ? ScintillaMessage.DocumentStartExtend : ScintillaMessage.DocumentStart,
            (true, _, Key.Down) => shift ? ScintillaMessage.DocumentEndExtend : ScintillaMessage.DocumentEnd,
            (false, true, Key.Left) => shift ? ScintillaMessage.WordLeftExtend : ScintillaMessage.WordLeft,
            (false, true, Key.Right) => shift ? ScintillaMessage.WordRightExtend : ScintillaMessage.WordRight,
            (false, true, Key.Back) => ScintillaMessage.DelWordLeft,
            (false, true, Key.Delete) => ScintillaMessage.DelWordRight,
            _ => null
        };
        if (message is { } action) { document.Send(action); Changed(); e.Handled = true; return; }
        if (command) return;
        int key = e.Key switch
        {
            Key.Down => 300,
            Key.Up => 301,
            Key.Left => 302,
            Key.Right => 303,
            Key.Home => 304,
            Key.End => 305,
            Key.PageUp => 306,
            Key.PageDown => 307,
            Key.Delete => 308,
            Key.Insert => 309,
            Key.Escape => 7,
            Key.Back => 8,
            Key.Tab => 9,
            Key.Enter => 13,
            _ => 0
        };
        if (key != 0 && document.Key(key, Modifiers(e.KeyModifiers)))
        { Changed(); e.Handled = true; }
    }

    private async Task ClipboardAsync(Key key)
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) return;
            if (key == Key.V)
            {
                var previous = revision;
                var anchor = document.Send(ScintillaMessage.GetAnchor);
                var caret = document.Send(ScintillaMessage.GetCurrentPos);
                var text = await clipboard.TryGetTextAsync();
                if (!disposed && IsFocused && text is not null && revision == previous &&
                    document.Send(ScintillaMessage.GetAnchor) == anchor && document.Send(ScintillaMessage.GetCurrentPos) == caret)
                { document.Input(text); Changed(); }
            }
            else
            {
                var selected = document.Text(true);
                if (selected.Length == 0) return;
                var anchor = document.Send(ScintillaMessage.GetAnchor);
                var caret = document.Send(ScintillaMessage.GetCurrentPos);
                await clipboard.SetTextAsync(selected);
                if (key == Key.X && !disposed && IsFocused &&
                    document.Send(ScintillaMessage.GetAnchor) == anchor && document.Send(ScintillaMessage.GetCurrentPos) == caret && document.Text(true) == selected)
                { document.Send(ScintillaMessage.Clear); Changed(); }
            }
        }
        catch (Exception error) { OperationFailed?.Invoke(this, error); }
    }

    private static int Modifiers(KeyModifiers modifiers) =>
        (modifiers.HasFlag(KeyModifiers.Shift) ? 1 : 0) |
        (modifiers.HasFlag(KeyModifiers.Control) ? 2 : 0) |
        (modifiers.HasFlag(KeyModifiers.Alt) ? 4 : 0);

    private void Mouse(PointerEventArgs e, int kind)
    {
        var point = e.GetPosition(this);
        document.Mouse(kind, point.X, point.Y, (uint)e.Timestamp, Modifiers(e.KeyModifiers));
        InvalidateVisual(); inputClient.Notify(); e.Handled = true;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (disposed || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus(); draggingSelection = true; e.Pointer.Capture(this); Mouse(e, 0);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    { base.OnPointerMoved(e); if (!disposed && e.Pointer.Captured == this) Mouse(e, 1); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (disposed || e.InitialPressMouseButton != MouseButton.Left) return;
        Mouse(e, 2); draggingSelection = false; e.Pointer.Capture(null);
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!disposed && draggingSelection) { draggingSelection = false; document.Mouse(2, -1, -1, 0, 0); }
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (disposed) return;
        // Trackpads report fractional deltas on both axes; carry remainders so slow
        // gestures still scroll and slight sideways drift does not swallow vertical motion.
        var delta = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? new Vector(e.Delta.Y, 0) : e.Delta;
        wheel += new Vector(delta.X * 40, delta.Y * 3);
        var (pixels, lines) = (Math.Truncate(wheel.X), Math.Truncate(wheel.Y));
        wheel -= new Vector(pixels, lines);
        if (pixels != 0) document.Send(ScintillaMessage.SetXOffset, Math.Max(0, document.Send(ScintillaMessage.GetXOffset) - (nint)pixels));
        if (lines != 0) document.Send(ScintillaMessage.LineScroll, 0, (nint)(-lines));
        InvalidateVisual(); inputClient.NotifyScrolled(); e.Handled = true;
    }
}
