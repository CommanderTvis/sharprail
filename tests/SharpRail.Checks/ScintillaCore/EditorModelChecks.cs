using System.Text;

using SharpRail.Scintilla;
using SharpRail.Scintilla.Editing;

using SkiaSharp;

namespace SharpRail.Checks.ScintillaCore;

/// <summary>Managed-only checks (no upstream suite): message semantics of the editor model, with a real typeface.</summary>
internal static class EditorModelChecks
{
    internal static void Run()
    {
        Text();
        UndoRedo();
        ReadOnly();
        Selection();
        Words();
        LineCommands();
        Keys();
        EndOfLines();
        Styling();
        Indicators();
        Margins();
        Wrapping();
        Points();
        RevisionAndDirty();
        MouseSelection();
    }

    private static Editor Create(string text = "")
    {
        var editor = new Editor(SKTypeface.Default);
        editor.Send(ScintillaMessage.SetEOLMode, 2);
        editor.Resize(400, 200);
        if (text.Length > 0) editor.AddText(Encoding.UTF8.GetBytes(text));
        return editor;
    }

    private static string Text(Editor editor) => Encoding.UTF8.GetString(editor.GetText());

    private static void Text()
    {
        using var editor = Create("hello\nworld");
        Expect.Equal("hello\nworld", Text(editor), "AddText");
        Expect.Equal<nint>(11, editor.Send(ScintillaMessage.GetLength), "GetLength");
        Expect.Equal<nint>(11, editor.Send(ScintillaMessage.GetText), "GetText length");
        Expect.Equal<nint>(11, editor.Send(ScintillaMessage.GetCurrentPos), "caret after AddText");
        Expect.Equal("lo\nw", Encoding.UTF8.GetString(editor.GetTextRange(3, 7)), "GetTextRange");
        Expect.Equal("world", Encoding.UTF8.GetString(editor.GetTextRange(6, -1)), "GetTextRange to end");
        Expect.Equal<nint>(2, editor.Send(ScintillaMessage.GetLineCount), "line count");
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.LineFromPosition, 7), "LineFromPosition");
        Expect.Equal<nint>(6, editor.Send(ScintillaMessage.PositionFromLine, 1), "PositionFromLine");
        Expect.Equal<nint>(-1, editor.Send(ScintillaMessage.PositionFromLine, 5), "PositionFromLine past end");
        Expect.Equal<nint>(5, editor.Send(ScintillaMessage.GetLineEndPosition, 0), "GetLineEndPosition");
        editor.Send(ScintillaMessage.DeleteRange, 0, 6);
        Expect.Equal("world", Text(editor), "DeleteRange");
        editor.Send(ScintillaMessage.ClearAll);
        Expect.Equal("", Text(editor), "ClearAll");
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.GetLineCount), "line count after ClearAll");
        Expect.Throws<NotSupportedException>(() => editor.Send(ScintillaMessage.AddText, 1, 1), "pointer message");
    }

    private static void UndoRedo()
    {
        using var editor = Create("abc");
        editor.Send(ScintillaMessage.EmptyUndoBuffer);
        editor.Send(ScintillaMessage.SetSavePoint);
        Expect.Equal<nint>(0, editor.Send(ScintillaMessage.GetModify), "unmodified at save point");
        editor.InsertText("d");
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.GetModify), "modified after typing");
        Expect.Equal("abcd", Text(editor), "typed");
        editor.Send(ScintillaMessage.Undo);
        Expect.Equal("abc", Text(editor), "undo");
        Expect.Equal<nint>(3, editor.Send(ScintillaMessage.GetCurrentPos), "caret after undo");
        Expect.Equal<nint>(0, editor.Send(ScintillaMessage.GetModify), "back at save point");
        editor.Send(ScintillaMessage.Redo);
        Expect.Equal("abcd", Text(editor), "redo");
        Expect.Equal<nint>(4, editor.Send(ScintillaMessage.GetCurrentPos), "caret after redo");
        editor.Send(ScintillaMessage.EmptyUndoBuffer);
        editor.Send(ScintillaMessage.Undo);
        Expect.Equal("abcd", Text(editor), "nothing to undo");
    }

    private static void ReadOnly()
    {
        using var editor = Create("abc");
        editor.Send(ScintillaMessage.SetReadOnly, 1);
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.GetReadOnly), "GetReadOnly");
        editor.InsertText("x");
        editor.Key(EditorKey.Back, EditorModifiers.None);
        editor.Key(EditorKey.Return, EditorModifiers.None);
        editor.Send(ScintillaMessage.SelectAll);
        editor.ReplaceSelection("z"u8);
        editor.Send(ScintillaMessage.Clear);
        Expect.Equal("abc", Text(editor), "read-only refuses edits");
        editor.Send(ScintillaMessage.SetReadOnly, 0);
        editor.ReplaceSelection("z"u8);
        Expect.Equal("z", Text(editor), "writable again");
    }

    private static void Selection()
    {
        using var editor = Create("one two three");
        editor.Send(ScintillaMessage.SetSel, 4, 7);
        Expect.Equal<nint>(7, editor.Send(ScintillaMessage.GetCurrentPos), "SetSel caret");
        Expect.Equal<nint>(4, editor.Send(ScintillaMessage.GetAnchor), "SetSel anchor");
        Expect.Equal<nint>(3, editor.Send(ScintillaMessage.GetSelText), "GetSelText length");
        Expect.Equal("two", Encoding.UTF8.GetString(editor.GetSelectedText()), "selected text");
        editor.ReplaceSelection("2"u8);
        Expect.Equal("one 2 three", Text(editor), "ReplaceSelection");
        Expect.Equal<nint>(5, editor.Send(ScintillaMessage.GetCurrentPos), "caret after ReplaceSelection");
        editor.Send(ScintillaMessage.SetSel, 2, -1);
        Expect.Equal<nint>(11, editor.Send(ScintillaMessage.GetSelectionEnd), "SetSel -1 end");
        Expect.Equal<nint>(2, editor.Send(ScintillaMessage.GetSelectionStart), "selection start");
        editor.Send(ScintillaMessage.SetSel, 8, 3);
        Expect.Equal<nint>(3, editor.Send(ScintillaMessage.GetSelectionStart), "reversed start");
        Expect.Equal<nint>(8, editor.Send(ScintillaMessage.GetSelectionEnd), "reversed end");
        editor.Send(ScintillaMessage.SelectAll);
        Expect.Equal<nint>(0, editor.Send(ScintillaMessage.GetCurrentPos), "SelectAll caret");
        Expect.Equal<nint>(11, editor.Send(ScintillaMessage.GetAnchor), "SelectAll anchor");
        editor.Send(ScintillaMessage.SetEmptySelection, 4);
        Expect.Equal<nint>(4, editor.Send(ScintillaMessage.GetAnchor), "SetEmptySelection");
        editor.Send(ScintillaMessage.GotoPos, 100);
        Expect.Equal<nint>(11, editor.Send(ScintillaMessage.GetCurrentPos), "GotoPos clamps");
        editor.Send(ScintillaMessage.SetSel, 0, 4);
        editor.InsertText("1");
        Expect.Equal("12 three", Text(editor), "typing replaces selection");
        editor.Send(ScintillaMessage.Undo);
        Expect.Equal("one 2 three", Text(editor), "replacement undone as one step");
    }

    private static void Words()
    {
        using var editor = Create("one two three");
        Expect.Equal<nint>(4, editor.Send(ScintillaMessage.WordStartPosition, 5, 1), "WordStartPosition");
        Expect.Equal<nint>(7, editor.Send(ScintillaMessage.WordEndPosition, 5, 1), "WordEndPosition");
        editor.Send(ScintillaMessage.WordLeft);
        Expect.Equal<nint>(8, editor.Send(ScintillaMessage.GetCurrentPos), "WordLeft from end");
        editor.Send(ScintillaMessage.WordLeft);
        Expect.Equal<nint>(4, editor.Send(ScintillaMessage.GetCurrentPos), "WordLeft again");
        editor.Send(ScintillaMessage.WordRightExtend);
        Expect.Equal<nint>(8, editor.Send(ScintillaMessage.GetCurrentPos), "WordRightExtend caret");
        Expect.Equal<nint>(4, editor.Send(ScintillaMessage.GetAnchor), "WordRightExtend anchor");
        editor.Send(ScintillaMessage.WordRight);
        Expect.Equal<nint>(13, editor.Send(ScintillaMessage.GetCurrentPos), "WordRight to end");
        editor.Send(ScintillaMessage.DelWordLeft);
        Expect.Equal("one two ", Text(editor), "DelWordLeft");
        editor.Send(ScintillaMessage.GotoPos, 0);
        editor.Send(ScintillaMessage.DelWordRight);
        Expect.Equal("two ", Text(editor), "DelWordRight");
        editor.Key(EditorKey.Right, EditorModifiers.Ctrl);
        Expect.Equal<nint>(4, editor.Send(ScintillaMessage.GetCurrentPos), "Ctrl+Right");
        editor.Key(EditorKey.Back, EditorModifiers.Ctrl);
        Expect.Equal("", Text(editor), "Ctrl+Back");
    }

    private static void LineCommands()
    {
        using var editor = Create("  indented\nsecond");
        editor.Send(ScintillaMessage.GotoPos, 6);
        editor.Send(ScintillaMessage.VCHome);
        Expect.Equal<nint>(2, editor.Send(ScintillaMessage.GetCurrentPos), "VCHome to indentation");
        editor.Send(ScintillaMessage.VCHome);
        Expect.Equal<nint>(0, editor.Send(ScintillaMessage.GetCurrentPos), "VCHome toggles to line start");
        editor.Send(ScintillaMessage.LineEndExtend);
        Expect.Equal<nint>(10, editor.Send(ScintillaMessage.GetCurrentPos), "LineEndExtend caret");
        Expect.Equal<nint>(0, editor.Send(ScintillaMessage.GetAnchor), "LineEndExtend anchor");
        editor.Send(ScintillaMessage.DocumentEnd);
        Expect.Equal<nint>(17, editor.Send(ScintillaMessage.GetCurrentPos), "DocumentEnd");
        editor.Send(ScintillaMessage.DocumentStartExtend);
        Expect.Equal<nint>(0, editor.Send(ScintillaMessage.GetCurrentPos), "DocumentStartExtend caret");
        Expect.Equal<nint>(17, editor.Send(ScintillaMessage.GetAnchor), "DocumentStartExtend anchor");
        editor.Send(ScintillaMessage.DocumentEndExtend);
        Expect.Equal<nint>(17, editor.Send(ScintillaMessage.GetSelectionEnd), "DocumentEndExtend");
        editor.Send(ScintillaMessage.DocumentStart);
        Expect.Equal<nint>(0, editor.Send(ScintillaMessage.GetAnchor), "DocumentStart collapses");
        editor.Send(ScintillaMessage.LineEnd);
        Expect.Equal<nint>(10, editor.Send(ScintillaMessage.GetCurrentPos), "LineEnd");
        editor.Send(ScintillaMessage.VCHomeExtend);
        Expect.Equal<nint>(2, editor.Send(ScintillaMessage.GetCurrentPos), "VCHomeExtend");
        Expect.Equal<nint>(10, editor.Send(ScintillaMessage.GetAnchor), "VCHomeExtend anchor");
    }

    private static void Keys()
    {
        using var editor = Create("ab\ncd");
        editor.Send(ScintillaMessage.GotoPos, 1);
        Expect.True(editor.Key(EditorKey.Down, EditorModifiers.None), "Down handled");
        Expect.Equal<nint>(4, editor.Send(ScintillaMessage.GetCurrentPos), "Down keeps column");
        editor.Key(EditorKey.Up, EditorModifiers.Shift);
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.GetCurrentPos), "Shift+Up caret");
        Expect.Equal<nint>(4, editor.Send(ScintillaMessage.GetAnchor), "Shift+Up anchor");
        editor.Key(EditorKey.Left, EditorModifiers.None);
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.GetCurrentPos), "Left collapses to start");
        editor.Key(EditorKey.End, EditorModifiers.Ctrl);
        Expect.Equal<nint>(5, editor.Send(ScintillaMessage.GetCurrentPos), "Ctrl+End");
        editor.Key(EditorKey.Home, EditorModifiers.None);
        Expect.Equal<nint>(3, editor.Send(ScintillaMessage.GetCurrentPos), "Home");
        editor.Key(EditorKey.Back, EditorModifiers.None);
        Expect.Equal("abcd", Text(editor), "Back joins lines");
        editor.Key(EditorKey.Delete, EditorModifiers.None);
        Expect.Equal("abd", Text(editor), "Delete");
        editor.Key(EditorKey.Tab, EditorModifiers.None);
        Expect.Equal("ab\td", Text(editor), "Tab inserts a tab");
        editor.Send(ScintillaMessage.SetUseTabs, 0);
        editor.Send(ScintillaMessage.SetTabWidth, 4);
        editor.Key(EditorKey.Tab, EditorModifiers.None);
        Expect.Equal("ab\t    d", Text(editor), "Tab inserts spaces to the next stop");
        editor.Key(EditorKey.Insert, EditorModifiers.None);
        editor.InsertText("X");
        Expect.Equal("ab\t    X", Text(editor), "overtype");
        Expect.True(editor.Key(EditorKey.Escape, EditorModifiers.None), "Escape handled");
        Expect.True(!editor.Key(EditorKey.Insert, EditorModifiers.Shift), "Shift+Insert left to the control");
    }

    private static void EndOfLines()
    {
        foreach (var (mode, eol) in new[] { (0, "\r\n"), (1, "\r"), (2, "\n") })
        {
            using var editor = Create("ab");
            editor.Send(ScintillaMessage.SetEOLMode, mode);
            editor.Send(ScintillaMessage.GotoPos, 1);
            editor.Key(EditorKey.Return, EditorModifiers.None);
            Expect.Equal("a" + eol + "b", Text(editor), $"Return in EOL mode {mode}");
            Expect.Equal<nint>(1 + eol.Length, editor.Send(ScintillaMessage.GetCurrentPos), $"caret after Return {mode}");
        }
    }

    private static void Styling()
    {
        using var editor = Create("abcdef");
        editor.Send(ScintillaMessage.StartStyling, 1);
        editor.Send(ScintillaMessage.SetStyling, 2, 40);
        editor.SetStylingEx([41, 42]);
        Expect.Equal<nint>(0, editor.Send(ScintillaMessage.GetStyleAt, 0), "unstyled");
        Expect.Equal<nint>(40, editor.Send(ScintillaMessage.GetStyleAt, 2), "SetStyling");
        Expect.Equal<nint>(41, editor.Send(ScintillaMessage.GetStyleAt, 3), "SetStylingEx first");
        Expect.Equal<nint>(42, editor.Send(ScintillaMessage.GetStyleAt, 4), "SetStylingEx second");
        Expect.Equal<nint>(0, editor.Send(ScintillaMessage.GetStyleAt, 99), "past end");
        editor.Send(ScintillaMessage.StyleSetFore, 40, 0x332211);
        editor.Send(ScintillaMessage.StyleSetBack, 40, 0x665544);
        editor.Send(ScintillaMessage.StyleSetItalic, 40, 1);
        Expect.Equal<nint>(0x332211, editor.Send(ScintillaMessage.StyleGetFore, 40), "StyleGetFore");
        Expect.Equal<nint>(0x665544, editor.Send(ScintillaMessage.StyleGetBack, 40), "StyleGetBack");
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.StyleGetItalic, 40), "StyleGetItalic");
        var height = editor.Send(ScintillaMessage.TextHeight);
        editor.Send(ScintillaMessage.StyleSetSizeFractional, 32, 2600);
        editor.Send(ScintillaMessage.StyleClearAll);
        Expect.True(editor.Send(ScintillaMessage.TextHeight) > height, "larger font raises line height");
        Expect.Equal<nint>(0, editor.Send(ScintillaMessage.StyleGetItalic, 40), "StyleClearAll resets");
    }

    private static void Indicators()
    {
        using var editor = Create("abcdefgh");
        editor.Send(ScintillaMessage.IndicSetStyle, 8, 1);
        editor.Send(ScintillaMessage.SetIndicatorCurrent, 8);
        editor.Send(ScintillaMessage.IndicatorFillRange, 2, 4);
        Expect.Equal<nint>(0, editor.Send(ScintillaMessage.IndicatorValueAt, 8, 1), "before fill");
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.IndicatorValueAt, 8, 2), "fill start");
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.IndicatorValueAt, 8, 5), "fill end");
        Expect.Equal<nint>(0, editor.Send(ScintillaMessage.IndicatorValueAt, 8, 6), "after fill");
        editor.Send(ScintillaMessage.IndicatorClearRange, 3, 2);
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.IndicatorValueAt, 8, 2), "kept");
        Expect.Equal<nint>(0, editor.Send(ScintillaMessage.IndicatorValueAt, 8, 3), "cleared");
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.IndicatorValueAt, 8, 5), "kept after clear");
        editor.Send(ScintillaMessage.GotoPos, 0);
        editor.InsertText("zz");
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.IndicatorValueAt, 8, 4), "moves with insertion");
    }

    private static void Margins()
    {
        using var editor = Create("a\nb\nc");
        Expect.Equal<nint>(5, editor.Send(ScintillaMessage.GetMargins), "GetMargins");
        editor.Send(ScintillaMessage.SetMarginTypeN, 0, 5);
        editor.Send(ScintillaMessage.SetMarginWidthN, 0, 48);
        editor.Send(ScintillaMessage.SetMarginWidthN, 1, 0);
        editor.Send(ScintillaMessage.SetMarginLeft, 0, 8);
        editor.Send(ScintillaMessage.SetMarginRight, 0, 3);
        Expect.Equal<nint>(48, editor.Send(ScintillaMessage.GetMarginWidthN, 0), "margin width");
        Expect.Equal<nint>(8, editor.Send(ScintillaMessage.GetMarginLeft), "left margin");
        Expect.Equal<nint>(3, editor.Send(ScintillaMessage.GetMarginRight), "right margin");
        editor.MarginSetText(1, "12");
        editor.Send(ScintillaMessage.MarginSetStyle, 1, 33);
        Expect.Equal("12", editor.Document.MarginText.Text(1), "MarginSetText");
        Expect.Equal(33, editor.Document.MarginText.Style(1), "MarginSetStyle");
        editor.Send(ScintillaMessage.MarginTextClearAll);
        Expect.Equal<string?>(null, editor.Document.MarginText.Text(1), "MarginTextClearAll");
        Expect.True(editor.TextWidth(33, "1234") > editor.TextWidth(33, "12"), "TextWidth grows");
    }

    private static void Wrapping()
    {
        using var editor = Create(string.Join(' ', Enumerable.Repeat("word", 40)) + "\nshort");
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.WrapCount, 0), "unwrapped");
        editor.Send(ScintillaMessage.SetMarginWidthN, 0, 0);
        editor.Send(ScintillaMessage.SetMarginWidthN, 1, 0);
        editor.Resize(120, 400);
        editor.Send(ScintillaMessage.SetWrapMode, 1);
        while (editor.Idle()) { }
        var rows = editor.Send(ScintillaMessage.WrapCount, 0);
        Expect.True(rows > 3, $"long line wraps into rows, got {rows}");
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.WrapCount, 1), "short line");
        Expect.Equal(rows, editor.Send(ScintillaMessage.VisibleFromDocLine, 1), "VisibleFromDocLine after wrap");
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.DocLineFromVisible, rows), "DocLineFromVisible");
        Expect.Equal<nint>(0, editor.Send(ScintillaMessage.DocLineFromVisible, rows - 1), "sub-line maps to line");
        editor.Resize(4000, 400);
        while (editor.Idle()) { }
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.WrapCount, 0), "wide enough");
        editor.Resize(120, 400);
        editor.Send(ScintillaMessage.SetWrapMode, 0);
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.WrapCount, 0), "wrap off");
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.VisibleFromDocLine, 1), "one-to-one again");
    }

    private static void Points()
    {
        using var editor = Create("first line\nsecond line\nthird");
        editor.Send(ScintillaMessage.SetMarginWidthN, 0, 30);
        var height = editor.Send(ScintillaMessage.TextHeight);
        Expect.True(height > 0, "TextHeight");
        Expect.Equal(200 / height, editor.Send(ScintillaMessage.LinesOnScreen), "LinesOnScreen");
        foreach (var position in new nint[] { 0, 3, 11, 15, 23 })
        {
            var x = editor.Send(ScintillaMessage.PointXFromPosition, 0, position);
            var y = editor.Send(ScintillaMessage.PointYFromPosition, 0, position);
            Expect.Equal(position, editor.Send(ScintillaMessage.PositionFromPoint, x, y + height / 2), $"round trip {position}");
        }
        Expect.True(editor.Send(ScintillaMessage.PointXFromPosition, 0, 0) >= 30, "x includes margins");
        Expect.Equal(height, editor.Send(ScintillaMessage.PointYFromPosition, 0, 11), "second line y");
        Expect.Equal<nint>(-1, editor.Send(ScintillaMessage.PositionFromPointClose, 5, 2), "close: in margin");
        Expect.Equal<nint>(-1, editor.Send(ScintillaMessage.PositionFromPointClose, 390, 2), "close: past line end");
        Expect.Equal<nint>(-1, editor.Send(ScintillaMessage.PositionFromPointClose, 60, height * 5), "close: below text");
        Expect.Equal<nint>(28, editor.Send(ScintillaMessage.PositionFromPoint, 40, height * 5), "below text clamps");
        Expect.True(editor.Send(ScintillaMessage.PositionFromPointClose, 60, 2) >= 0, "close: inside text");

        editor.Send(ScintillaMessage.SetXOffset, 20);
        Expect.Equal<nint>(20, editor.Send(ScintillaMessage.GetXOffset), "xOffset");
        var shifted = editor.Send(ScintillaMessage.PointXFromPosition, 0, 3);
        editor.Send(ScintillaMessage.SetXOffset, 0);
        Expect.Equal(editor.Send(ScintillaMessage.PointXFromPosition, 0, 3) - 20, shifted, "xOffset shifts points");

        using var tall = Create(string.Join('\n', Enumerable.Range(0, 100)));
        tall.Send(ScintillaMessage.SetFirstVisibleLine, 10);
        Expect.Equal<nint>(10, tall.Send(ScintillaMessage.GetFirstVisibleLine), "SetFirstVisibleLine");
        tall.Send(ScintillaMessage.LineScroll, 0, 5);
        Expect.Equal<nint>(15, tall.Send(ScintillaMessage.GetFirstVisibleLine), "LineScroll");
        tall.Send(ScintillaMessage.SetFirstVisibleLine, 1000);
        Expect.Equal(100 - tall.Send(ScintillaMessage.LinesOnScreen), tall.Send(ScintillaMessage.GetFirstVisibleLine), "clamped to last page");
        tall.Send(ScintillaMessage.GotoPos, 0);
        Expect.Equal<nint>(0, tall.Send(ScintillaMessage.GetFirstVisibleLine), "GotoPos scrolls caret into view");
        Expect.Equal(height, tall.Send(ScintillaMessage.PointYFromPosition, 0, tall.Send(ScintillaMessage.PositionFromLine, 1)), "y relative to top");
    }

    private static void RevisionAndDirty()
    {
        using var editor = Create();
        var revision = editor.Revision;
        editor.InsertText("ab");
        Expect.Equal(revision + 2, editor.Revision, "one revision per inserted character");
        editor.Dirty = false;
        editor.Key(EditorKey.Back, EditorModifiers.None);
        Expect.Equal(revision + 3, editor.Revision, "delete bumps revision");
        Expect.True(editor.Dirty, "edit invalidates");
        editor.Dirty = false;
        editor.Send(ScintillaMessage.StartStyling, 0);
        editor.Send(ScintillaMessage.SetStyling, 1, 40);
        Expect.Equal(revision + 3, editor.Revision, "styling leaves the revision");
        Expect.True(editor.Dirty, "styling invalidates");
        editor.Dirty = false;
        editor.Send(ScintillaMessage.GetLength);
        Expect.True(!editor.Dirty, "queries do not invalidate");
        editor.Focus(true);
        editor.Dirty = false;
        editor.Tick();
        Expect.True(editor.Dirty, "caret blink invalidates");
    }

    private static void MouseSelection()
    {
        using var editor = Create("alpha beta gamma\nsecond");
        editor.Send(ScintillaMessage.SetMarginWidthN, 0, 0);
        editor.Send(ScintillaMessage.SetMarginWidthN, 1, 0);
        var height = editor.Send(ScintillaMessage.TextHeight);
        var y = height / 2f;
        float X(int position) => editor.Send(ScintillaMessage.PointXFromPosition, 0, position) + 1;

        editor.Mouse(MouseAction.Down, X(7), y, 1000, EditorModifiers.None);
        editor.Mouse(MouseAction.Up, X(7), y, 1010, EditorModifiers.None);
        Expect.Equal<nint>(7, editor.Send(ScintillaMessage.GetCurrentPos), "click places caret");
        editor.Mouse(MouseAction.Down, X(7), y, 1100, EditorModifiers.None);
        editor.Mouse(MouseAction.Up, X(7), y, 1110, EditorModifiers.None);
        Expect.Equal("beta", Encoding.UTF8.GetString(editor.GetSelectedText()), "double-click selects word");
        editor.Mouse(MouseAction.Down, X(7), y, 1200, EditorModifiers.None);
        editor.Mouse(MouseAction.Up, X(7), y, 1210, EditorModifiers.None);
        Expect.Equal("alpha beta gamma\n", Encoding.UTF8.GetString(editor.GetSelectedText()), "triple-click selects line");

        editor.Mouse(MouseAction.Down, X(1), y, 5000, EditorModifiers.None);
        editor.Mouse(MouseAction.Move, X(4), y, 5010, EditorModifiers.None);
        editor.Mouse(MouseAction.Up, -1, -1, 0, EditorModifiers.None);
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.GetAnchor), "drag anchor");
        Expect.Equal<nint>(4, editor.Send(ScintillaMessage.GetCurrentPos), "drag caret");
        editor.Mouse(MouseAction.Move, X(10), y, 5020, EditorModifiers.None);
        Expect.Equal<nint>(4, editor.Send(ScintillaMessage.GetCurrentPos), "no drag after capture loss");
        editor.Mouse(MouseAction.Down, X(9), y, 9000, EditorModifiers.Shift);
        editor.Mouse(MouseAction.Up, X(9), y, 9010, EditorModifiers.Shift);
        Expect.Equal<nint>(1, editor.Send(ScintillaMessage.GetAnchor), "shift-click keeps anchor");
        Expect.Equal<nint>(9, editor.Send(ScintillaMessage.GetCurrentPos), "shift-click extends");
    }
}