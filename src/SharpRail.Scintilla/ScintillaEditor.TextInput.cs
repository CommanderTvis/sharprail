using System.Text;

using Avalonia;
using Avalonia.Input;
using Avalonia.Input.TextInput;
using Avalonia.Threading;

namespace SharpRail.Scintilla;

public sealed partial class ScintillaEditor
{
    internal TextInputMethodClient InputMethodClient => inputClient;
    /// <summary>Whether the input method sees the caret line's neighbours too, as Android's does.</summary>
    internal bool InputSpansNeighbours { get => inputClient.Neighbours; set => inputClient.Neighbours = value; }

    private sealed class InputClient(ScintillaEditor owner) : TextInputMethodClient
    {
        // Android's input connection edits through offsets into the surrounding text: Backspace at a line start needs
        // the previous line's break inside it, and one edit applies several offsets to the text it started with. There
        // the text also spans the neighbouring lines, a line break counts as one character whatever the document uses,
        // and the first line stays put until the edit that moved the caret has finished.
        internal bool Neighbours { get; set; } = OperatingSystem.IsAndroid();
        private nint first = -1;
        private bool settling;
        public override Visual TextViewVisual => owner;
        public override bool SupportsPreedit => false;
        public override bool SupportsSurroundingText => true;
        public override string SurroundingText => Window().Text.Replace("\r\n", "\n");
        public override Rect CursorRectangle
        {
            get
            {
                var position = owner.document.Send(ScintillaMessage.GetCurrentPos);
                var x = owner.document.Send(ScintillaMessage.PointXFromPosition, 0, position);
                var y = owner.document.Send(ScintillaMessage.PointYFromPosition, 0, position);
                return new Rect(x, y - owner.SubLine, 1, owner.document.Send(ScintillaMessage.TextHeight));
            }
        }
        // Like Avalonia's TextBox, expose only the caret line so IME queries stay cheap in large files.
        public override TextSelection Selection
        {
            get
            {
                var (start, text) = Window();
                var bytes = Encoding.UTF8.GetBytes(text);
                return new(Chars(owner.document.Send(ScintillaMessage.GetAnchor)), Chars(owner.document.Send(ScintillaMessage.GetCurrentPos)));
                int Chars(nint position)
                {
                    var chars = Encoding.UTF8.GetCharCount(bytes.AsSpan(0, (int)Math.Clamp(position - start, 0, bytes.Length)));
                    return chars - text.AsSpan(0, Math.Min(chars, text.Length)).Count("\r\n");
                }
            }
            set
            {
                var (start, text) = Window();
                owner.document.Send(ScintillaMessage.SetSel, Position(value.Start), Position(value.End));
                owner.InvalidateVisual(); Notify();
                nint Position(int offset)
                {
                    var index = 0;
                    for (; index < text.Length && offset > 0; index++, offset--)
                        if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                    return start + Encoding.UTF8.GetByteCount(text.AsSpan(0, index));
                }
            }
        }
        private (nint Start, string Text) Window()
        {
            var document = owner.document;
            var line = document.Send(ScintillaMessage.LineFromPosition, document.Send(ScintillaMessage.GetCurrentPos));
            var last = line;
            if (!Neighbours) first = line;
            else
            {
                var wanted = Math.Max(0, line - 1);
                last = Math.Min(line + 1, document.Send(ScintillaMessage.GetLineCount) - 1);
                if (first < 0 || line < first) first = wanted;
                else if (first != wanted && !settling)
                {
                    settling = true;
                    Dispatcher.UIThread.Post(() =>
                    {
                        settling = false; first = -1;
                        if (!owner.disposed) { RaiseSurroundingTextChanged(); RaiseSelectionChanged(); }
                    });
                }
            }
            var start = document.Send(ScintillaMessage.PositionFromLine, first);
            return (start, document.Text(start, document.Send(ScintillaMessage.GetLineEndPosition, last)));
        }
        public override void ExecuteContextMenuAction(ContextMenuAction action)
        {
            if (action == ContextMenuAction.SelectAll) owner.SelectAll();
            else _ = owner.ClipboardAsync(action switch { ContextMenuAction.Copy => Key.C, ContextMenuAction.Cut => Key.X, _ => Key.V });
        }
        internal void Notify()
        { RaiseCursorRectangleChanged(); RaiseSurroundingTextChanged(); RaiseSelectionChanged(); owner.SyncSelection(); }
        internal void NotifyScrolled() => RaiseCursorRectangleChanged();
        internal void ShowPanel() => RaiseInputPaneActivationRequested();
    }
}