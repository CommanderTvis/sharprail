using System.Globalization;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.GestureRecognizers;
using Avalonia.Input.Platform;

namespace SharpRail.Scintilla;

public sealed partial class ScintillaEditor
{
    private bool draggingSelection;
    private bool tapping;
    private Vector wheel;
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (disposed || string.IsNullOrEmpty(e.Text)) return;
        // A soft keyboard commits Enter as text; the key inserts the document's own line ending.
        if (e.Text is "\n" or "\r" or "\r\n") document.Key(13, 0);
        else document.Input(e.Text);
        Changed(); e.Handled = true;
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
        if (!option && !e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.Left or Key.Right or Key.Back or Key.Delete && EditByGrapheme(e.Key, shift))
        { Changed(); e.Handled = true; return; }
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

    // Scintilla steps by code point; the caret keys and deletion step over whole grapheme clusters
    // instead. Selections, line ends and document edges keep Scintilla's behaviour.
    private bool EditByGrapheme(Key key, bool extend)
    {
        var caret = document.Send(ScintillaMessage.GetCurrentPos);
        var anchor = document.Send(ScintillaMessage.GetAnchor);
        var deleting = key is Key.Back or Key.Delete;
        if (deleting ? extend || caret != anchor : caret != anchor && !extend) return false;
        var target = GraphemeBoundary(caret, key is Key.Right or Key.Delete);
        if (target == caret) return false;
        if (deleting) document.Send(ScintillaMessage.DeleteRange, Math.Min(caret, target), Math.Abs(target - caret));
        else if (extend) document.Send(ScintillaMessage.SetSel, anchor, target);
        else document.Send(ScintillaMessage.GotoPos, target);
        return true;
    }

    // The nearest grapheme boundary after or before a position on its own line.
    private nint GraphemeBoundary(nint position, bool forward)
    {
        var line = document.Send(ScintillaMessage.LineFromPosition, position);
        var start = document.Send(ScintillaMessage.PositionFromLine, line);
        var end = document.Send(ScintillaMessage.GetLineEndPosition, line);
        if (forward ? position >= end : position <= start) return position;
        var text = document.Text(start, end);
        // Invalid UTF-8 decodes to replacement characters whose byte counts differ.
        if (Encoding.UTF8.GetByteCount(text) != end - start) return position;
        nint boundary = start, previous = start;
        for (var i = 0; i < text.Length;)
        {
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(i));
            boundary += Encoding.UTF8.GetByteCount(text.AsSpan(i, length));
            i += length;
            if (forward && boundary > position) return boundary;
            if (!forward && boundary >= position) return previous;
            previous = boundary;
        }
        return forward ? end : previous;
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
                { document.Replace(text); Changed(); }
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
        document.Mouse(kind, point.X, point.Y + SubLine, (uint)e.Timestamp, Modifiers(e.KeyModifiers));
        InvalidateVisual(); inputClient.Notify(); e.Handled = true;
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (disposed || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // A finger scrolls by dragging, so it places the caret only once it lifts without having moved.
        if (e.Pointer.Type == PointerType.Touch) { tapping = true; e.Handled = true; return; }
        Focus(); draggingSelection = true; e.Pointer.Capture(this); Mouse(e, 0);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    { base.OnPointerMoved(e); if (!disposed && e.Pointer.Type != PointerType.Touch && e.Pointer.Captured == this) Mouse(e, 1); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (disposed || e.InitialPressMouseButton != MouseButton.Left) return;
        if (e.Pointer.Type == PointerType.Touch)
        {
            if (!tapping) return;
            tapping = false;
            Focus(); Mouse(e, 0); Mouse(e, 2); SoftKeyboard(true); inputClient.ShowPanel();
            return;
        }
        Mouse(e, 2); draggingSelection = false; e.Pointer.Capture(null);
    }
    protected override void OnHolding(HoldingRoutedEventArgs e)
    {
        base.OnHolding(e);
        if (disposed || e.HoldingState != HoldingState.Started || e.PointerType != PointerType.Touch) return;
        tapping = false;
        var position = document.Send(ScintillaMessage.PositionFromPoint, (nint)Math.Round(e.Position.X), (nint)Math.Round(e.Position.Y + SubLine));
        document.Send(ScintillaMessage.SetSel, document.Send(ScintillaMessage.WordStartPosition, position, 1), document.Send(ScintillaMessage.WordEndPosition, position, 1));
        Focus(); InvalidateVisual(); inputClient.Notify();
    }

    // Touch pointers only: mouse and pen drags keep selecting.
    private sealed class TouchScroll : ScrollGestureRecognizer
    {
        public TouchScroll() { CanVerticallyScroll = true; CanHorizontallyScroll = true; }
        protected override void PointerPressed(PointerPressedEventArgs e)
        { if (e.Pointer.Type == PointerType.Touch) base.PointerPressed(e); }
    }

    private void OnTouchScroll(object? sender, ScrollGestureEventArgs e)
    {
        if (disposed) return;
        tapping = false;
        ScrollHorizontally(e.Delta.X);
        if (e.Delta.Y != 0) ScrollPixels(e.Delta.Y);
        InvalidateVisual(); inputClient.NotifyScrolled(); e.Handled = true;
    }

    private void ScrollHorizontally(double pixels)
    {
        wheel += new Vector(pixels, 0);
        var whole = Math.Truncate(wheel.X);
        wheel -= new Vector(whole, 0);
        if (whole != 0) ScrollToX(document.Send(ScintillaMessage.GetXOffset) + whole);
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
        ScrollHorizontally(-delta.X * 40);
        if (delta.Y != 0) ScrollPixels(-delta.Y * 50);
        InvalidateVisual(); inputClient.NotifyScrolled(); e.Handled = true;
    }
}