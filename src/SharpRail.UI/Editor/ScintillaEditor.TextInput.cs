using System.Text;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.TextInput;

namespace SharpRail.UI.Editor;

public sealed partial class ScintillaEditor
{
    private sealed class InputClient(ScintillaEditor owner) : TextInputMethodClient
    {
        public override Visual TextViewVisual => owner;
        public override bool SupportsPreedit => false;
        public override bool SupportsSurroundingText => true;
        public override string SurroundingText => Line().Text;
        public override Rect CursorRectangle
        {
            get
            {
                var position = owner.document.Send(ScintillaMessage.GetCurrentPos);
                var x = owner.document.Send(ScintillaMessage.PointXFromPosition, 0, position);
                var y = owner.document.Send(ScintillaMessage.PointYFromPosition, 0, position);
                return new Rect(x, y, 1, owner.document.Send(ScintillaMessage.TextHeight));
            }
        }
        // Like Avalonia's TextBox, expose only the caret line so IME queries stay cheap in large files.
        public override TextSelection Selection
        {
            get
            {
                var (start, text) = Line();
                var bytes = Encoding.UTF8.GetBytes(text);
                return new(Chars(owner.document.Send(ScintillaMessage.GetAnchor)), Chars(owner.document.Send(ScintillaMessage.GetCurrentPos)));
                int Chars(nint position) => Encoding.UTF8.GetCharCount(bytes.AsSpan(0, (int)Math.Clamp(position - start, 0, bytes.Length)));
            }
            set
            {
                var (start, text) = Line();
                owner.document.Send(ScintillaMessage.SetSel, Position(value.Start), Position(value.End));
                owner.InvalidateVisual(); Notify();
                nint Position(int chars) => start + Encoding.UTF8.GetByteCount(text.AsSpan(0, Math.Clamp(chars, 0, text.Length)));
            }
        }
        private (nint Start, string Text) Line()
        {
            var document = owner.document;
            var line = document.Send(ScintillaMessage.LineFromPosition, document.Send(ScintillaMessage.GetCurrentPos));
            var start = document.Send(ScintillaMessage.PositionFromLine, line);
            return (start, document.Text(start, document.Send(ScintillaMessage.GetLineEndPosition, line)));
        }
        public override void ExecuteContextMenuAction(ContextMenuAction action)
        {
            if (action == ContextMenuAction.SelectAll) owner.SelectAll();
            else _ = owner.ClipboardAsync(action switch { ContextMenuAction.Copy => Key.C, ContextMenuAction.Cut => Key.X, _ => Key.V });
        }
        internal void Notify()
        { RaiseCursorRectangleChanged(); RaiseSurroundingTextChanged(); RaiseSelectionChanged(); }
        internal void NotifyScrolled() => RaiseCursorRectangleChanged();
    }
}
