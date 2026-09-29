using System.Text;
using Avalonia.Threading;
using SkiaSharp;

namespace SharpRail.UI.Editor;

internal sealed class ScintillaDocument : IDisposable
{
    private readonly SkiaSurface surface = new();
    private nint Handle { get; set; }

    internal ScintillaDocument(string text)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The Scintilla editor currently supports macOS only.");

        Dispatcher.UIThread.VerifyAccess();

        try
        {
            Handle = ScintillaNative.sr_create(surface.Callback, 0);
            surface.CheckError();
            if (Handle == 0) throw new InvalidOperationException("Unable to create the Scintilla editor.");

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
            // The default caches only the caret line, re-measuring every visible line per paint.
            Send(ScintillaMessage.SetLayoutCache, 2);
            SetText(text);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal nint Send(ScintillaMessage message, nint w = 0, nint l = 0)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(Handle == 0, this);
        var result = ScintillaNative.sr_send(Handle, (uint)message, (nuint)w, l);
        surface.CheckError();
        return result;
    }

    private unsafe void SendText(ScintillaMessage message, string text, bool withLength = false)
    {
        var bytes = Encoding.UTF8.GetBytes(text + '\0');
        fixed (byte* pointer = bytes) Send(message, withLength ? bytes.Length - 1 : 0, (nint)pointer);
    }

    internal void SetText(string text)
    {
        Send(ScintillaMessage.ClearAll);
        SendText(ScintillaMessage.AddText, text, true);
        Send(ScintillaMessage.SetEmptySelection);
        Send(ScintillaMessage.EmptyUndoBuffer);
        Send(ScintillaMessage.SetSavePoint);
    }

    internal unsafe string Text(bool selection = false)
    {
        var length = checked((int)Send(selection ? ScintillaMessage.GetSelText : ScintillaMessage.GetLength));
        var bytes = new byte[length + 1];
        fixed (byte* pointer = bytes)
        {
            Send(selection ? ScintillaMessage.GetSelText : ScintillaMessage.GetText, bytes.Length, (nint)pointer);
            return Encoding.UTF8.GetString(bytes, 0, length);
        }
    }

    internal unsafe string Text(nint start, nint end)
    {
        var bytes = new byte[end - start + 1];
        fixed (byte* pointer = bytes)
        {
            var range = new TextRange(start, end, pointer);
            Send(ScintillaMessage.GetTextRangeFull, 0, (nint)(&range));
        }
        return Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1);
    }

    private readonly unsafe struct TextRange(nint min, nint max, byte* text)
    {
        private readonly nint min = min, max = max;
        private readonly byte* text = text;
    }

    internal nint Revision => ScintillaNative.sr_revision(Handle);

    internal void Input(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ScintillaNative.sr_text(Handle, bytes, bytes.Length);
        surface.CheckError();
    }

    internal bool Key(int key, int modifiers)
    {
        var consumed = ScintillaNative.sr_key(Handle, key, modifiers) != 0;
        surface.CheckError();
        return consumed;
    }

    internal void Mouse(int kind, double x, double y, uint timestamp, int modifiers)
    {
        ScintillaNative.sr_mouse(Handle, kind, x, y, timestamp, modifiers);
        surface.CheckError();
    }

    internal void Focus(bool focused)
    {
        ScintillaNative.sr_focus(Handle, focused ? 1 : 0);
        surface.CheckError();
    }

    internal void Tick()
    {
        ScintillaNative.sr_tick(Handle);
        surface.CheckError();
    }

    /// <summary>Runs one slice of Scintilla's idle work; returns whether more remains.</summary>
    internal bool Idle()
    {
        var pending = ScintillaNative.sr_idle(Handle) != 0;
        surface.CheckError();
        return pending;
    }

    internal void Resize(double width, double height)
    {
        ScintillaNative.sr_resize(Handle, width, height);
        surface.CheckError();
    }

    internal SKPicture Record(float width, float height) => surface.Record(Handle, width, height);

    public void Dispose()
    {
        if (Handle != 0)
        {
            ScintillaNative.sr_destroy(Handle);
            Handle = 0;
        }

        surface.Dispose();
        GC.KeepAlive(surface.Callback);
    }
}