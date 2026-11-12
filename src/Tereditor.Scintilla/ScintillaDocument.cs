using System.Text;

using Avalonia.Threading;

using SharpRail.Scintilla.Editing;

using SkiaSharp;

namespace SharpRail.Scintilla;

internal sealed class ScintillaDocument : IDisposable
{
    private Editor? editor;

    internal ScintillaDocument(string text, SKTypeface typeface)
    {
        Dispatcher.UIThread.VerifyAccess();
        editor = new Editor(typeface);
        try
        {
            Send(ScintillaMessage.SetEOLMode,
                text.Contains("\r\n", StringComparison.Ordinal) ? 0 : text.Contains('\r') ? 1 : 2);
            Send(ScintillaMessage.SetTabWidth, 4);
            Send(ScintillaMessage.SetUseTabs);
            Send(ScintillaMessage.SetMarginTypeN, 0, 1);
            Send(ScintillaMessage.SetMarginWidthN, 0, 48);
            Send(ScintillaMessage.SetMarginWidthN, 1);
            Send(ScintillaMessage.SetMarginLeft, 0, 8);
            Send(ScintillaMessage.SetScrollWidth, 1);
            Send(ScintillaMessage.SetScrollWidthTracking, 1);
            Send(ScintillaMessage.SetLayoutCache, 2);
            SetText(text);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private Editor Editor
    {
        get
        {
            Dispatcher.UIThread.VerifyAccess();
            ObjectDisposedException.ThrowIf(editor is null, this);
            return editor;
        }
    }

    internal nint Send(ScintillaMessage message, nint w = 0, nint l = 0) => Editor.Send(message, w, l);

    /// <summary>Sends a message whose lParam is a string and whose wParam is <paramref name="w"/>.</summary>
    internal nint Send(ScintillaMessage message, nint w, string text)
    {
        switch (message)
        {
            case ScintillaMessage.MarginSetText:
                Editor.MarginSetText(checked((int)w), text);
                return 0;
            case ScintillaMessage.TextWidth:
                return Editor.TextWidth(checked((int)w), text);
            default:
                throw new ArgumentOutOfRangeException(nameof(message), message, "The message takes no string.");
        }
    }

    internal void SetText(string text)
    {
        Send(ScintillaMessage.ClearAll);
        // The history is emptied below, so loading records no undo copy of the text.
        Editor.LoadText(Encoding.UTF8.GetBytes(text));
        Send(ScintillaMessage.SetEmptySelection);
        // Scintilla's SCI_ADDTEXT leaves the view where SCI_CLEARALL put it, at the top.
        Send(ScintillaMessage.SetFirstVisibleLine);
        Send(ScintillaMessage.EmptyUndoBuffer);
        Send(ScintillaMessage.SetSavePoint);
    }

    /// <summary>Replaces the selection as one edit, as a paste does.</summary>
    internal void Replace(string text) => Editor.ReplaceSelection(Encoding.UTF8.GetBytes(text));

    internal string Text(bool selection = false) =>
        Encoding.UTF8.GetString(selection ? Editor.GetSelectedText() : Editor.GetText());

    internal string Text(nint start, nint end) =>
        Encoding.UTF8.GetString(Editor.GetTextRange(checked((int)start), checked((int)end)));

    /// <summary>Applies one style per byte from the styling position, like SCI_SETSTYLINGEX.</summary>
    internal void SetStyling(ReadOnlySpan<byte> styles) => Editor.SetStylingEx(styles);

    /// <summary>Whether typed text takes its neighbour's style until the container restyles it.</summary>
    internal bool InheritInsertionStyle { set => Editor.Document.InheritInsertionStyle = value; }

    internal nint Revision => (nint)Editor.Revision;

    /// <summary>Whether the editor invalidated anything since the flag was last cleared.</summary>
    internal bool Dirty
    {
        get => Editor.Dirty;
        set => Editor.Dirty = value;
    }

    internal void Input(string text) => Editor.InsertText(text);

    internal bool Key(int key, int modifiers) => Editor.Key((EditorKey)key, (EditorModifiers)modifiers);

    internal void Mouse(int kind, double x, double y, uint timestamp, int modifiers) =>
        Editor.Mouse((MouseAction)kind, (float)x, (float)y, timestamp, (EditorModifiers)modifiers);

    internal void Focus(bool focused) => Editor.Focus(focused);

    internal void Tick() => Editor.Tick();

    /// <summary>Runs one slice of idle work; returns whether more remains.</summary>
    internal bool Idle() => Editor.Idle();

    internal void SetDirection(ScintillaTextDirection direction) => Editor.SetDirection(direction);

    internal void Resize(double width, double height) => Editor.Resize((float)width, (float)height);

    internal SKPicture Record(float width, float height)
    {
        var target = Editor;
        using var recorder = new SKPictureRecorder();
        target.Paint(recorder.BeginRecording(new SKRect(0, 0, width, height)));
        return recorder.EndRecording();
    }

    public void Dispose()
    {
        editor?.Dispose();
        editor = null;
    }
}