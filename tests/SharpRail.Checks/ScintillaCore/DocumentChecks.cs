using System.Text;

using SharpRail.Scintilla.Core;

namespace SharpRail.Checks.ScintillaCore;

/// <summary>Port of Scintilla test/unit/testDocument.cxx (minus search/regex/DBCS). Not ported: searches, regex,
/// case folding, DBCS, brace matching, SafeSegment and DiscardLastCombinedCharacter (not used), and the PerLine
/// marker cases (no markers). UTF-8 navigation and word cases below are added for the managed replacements of
/// UniConversion and CharacterCategoryMap.</summary>
internal static class DocumentChecks
{
    private const string SText = "Scintilla";

    internal static void Run()
    {
        InsertOneLine();
        Style();
        UndoCheckDeleteForwards();
        UndoCheckDeleteBackwards();
        UndoCheckBothWays();
        UndoCheckInsert();
        UndoCheckGrouped();
        WordsInText();
        WordsAtEnds();
        Utf8Navigation();
        WordMovement();
        Notifications();
        ReadOnlyAndSavePoint();
        LineEnds();
        IndicatorsAndMargins();
        PerLineLineAnnotation();
        // Not ported (feature absent from the managed core): Search*/InsensitiveSearch*/Regex*/RESearch* (no FindText;
        // SharpRail searches managed text), GetCharacterAndWidth DBCS, NextPosition Valid DBCS, BraceMatch DBCS and
        // SafeSegment "DBCS Shift-JIS" (UTF-8 only), BraceMatch (no brace matching), SafeSegment and
        // DiscardLastCombinedCharacter sections (no SafeSegment/DiscardLastCombinedCharacter), PerLine LineMarkers (no markers).
    }

    private static Document Doc(string initial)
    {
        var document = new Document();
        document.InsertString(0, initial);
        return document;
    }

    private static string Contents(Document doc) => doc.Text;

    private static string Styles(Document doc)
    {
        var styles = new byte[doc.Length];
        doc.GetStyleRange(styles, 0, doc.Length);
        return Encoding.Latin1.GetString(styles);
    }

    private static void MoveGap(Document doc, int gapNew)
    {
        doc.InsertString(gapNew, "!");
        doc.DeleteChars(gapNew, 1);
    }

    private sealed class ModificationWatcher : IDocumentWatcher
    {
        internal int ModPos = -1;
        internal int ModLength = -1;
        internal readonly List<DocumentChange> Changes = [];
        internal readonly List<bool> SavePoints = [];

        public void Modified(DocumentChange change)
        {
            ModPos = change.Position;
            ModLength = change.Length;
            Changes.Add(change);
        }

        public void SavePointChanged(bool atSavePoint) => SavePoints.Add(atSavePoint);

        internal void Clear()
        {
            ModPos = -1;
            ModLength = -1;
            Changes.Clear();
        }
    }

    private static void InsertOneLine()
    {
        var doc = new Document();
        int length = doc.InsertString(0, SText);
        Expect.Equal(SText.Length, doc.Length, "InsertOneLine length");
        Expect.Equal(SText.Length, length, "InsertOneLine inserted");
        Expect.Equal(1, doc.LinesTotal, "InsertOneLine lines");
        Expect.Equal(0, doc.LineStart(0), "InsertOneLine LineStart(0)");
        Expect.Equal(0, doc.LineFromPosition(0), "InsertOneLine LineFromPosition(0)");
        Expect.Equal(0, doc.LineStartPosition(0), "InsertOneLine LineStartPosition(0)");
        Expect.Equal(SText.Length, doc.LineStart(1), "InsertOneLine LineStart(1)");
        Expect.Equal(0, doc.LineFromPosition(SText.Length), "InsertOneLine LineFromPosition(end)");
        Expect.True(doc.CanUndo && !doc.CanRedo, "InsertOneLine undo state");
    }

    private static void Style()
    {
        // Styling calls modify the correct bytes and notify a minimal span.
        var doc = Doc(SText);
        Expect.Equal(new string('\0', 9), Styles(doc), "Style initial");
        var mw = new ModificationWatcher();
        doc.AddWatcher(mw);
        doc.StartStyling(1);
        doc.SetStyles("\0abc\0"u8);
        Expect.Equal("\0\0abc\0\0\0\0", Styles(doc), "Style SetStyles");
        // The NULs are not changed so are not included in the modification range.
        Expect.Equal((2, 3), (mw.ModPos, mw.ModLength), "Style SetStyles span");

        // No change so no notification.
        mw.Clear();
        doc.StartStyling(1);
        doc.SetStyles("\0abc\0"u8);
        Expect.Equal((-1, -1), (mw.ModPos, mw.ModLength), "Style no change");

        // Just change one byte.
        doc.StartStyling(1);
        doc.SetStyles("\0bbc\0"u8);
        Expect.Equal((2, 1), (mw.ModPos, mw.ModLength), "Style one byte span");
        Expect.Equal("\0\0bbc\0\0\0\0", Styles(doc), "Style one byte");

        // Move the gap.
        MoveGap(doc, 1);
        doc.StartStyling(1);
        doc.SetStyles("\0cbc\0"u8);
        Expect.Equal((2, 1), (mw.ModPos, mw.ModLength), "Style gap 1 span");
        Expect.Equal("\0\0cbc\0\0\0\0", Styles(doc), "Style gap 1");

        MoveGap(doc, 2);
        doc.StartStyling(1);
        doc.SetStyles("\0dcc\0"u8);
        Expect.Equal((2, 2), (mw.ModPos, mw.ModLength), "Style gap 2 span");
        Expect.Equal("\0\0dcc\0\0\0\0", Styles(doc), "Style gap 2");

        // SetStyleFor.
        MoveGap(doc, 3);
        doc.StartStyling(3);
        doc.SetStyleFor(1, 0); // After gap.
        Expect.Equal((3, 1), (mw.ModPos, mw.ModLength), "SetStyleFor after gap span");
        Expect.Equal("\0\0d\0c\0\0\0\0", Styles(doc), "SetStyleFor after gap");

        doc.StartStyling(2);
        doc.SetStyleFor(3, 0); // Over gap.
        Expect.Equal((2, 3), (mw.ModPos, mw.ModLength), "SetStyleFor over gap span");
        Expect.Equal(new string('\0', 9), Styles(doc), "SetStyleFor over gap");

        doc.RemoveWatcher(mw);
    }

    // DocumentUndo: Undo reports the end of coalesced deletes.

    private static void UndoCheckDeleteForwards()
    {
        var doc = Doc(SText);
        doc.DeleteUndoHistory();
        doc.DeleteChars(1, 1);
        doc.DeleteChars(1, 1);
        doc.DeleteChars(1, 1);
        Expect.Equal(4, doc.Undo(), "CheckDeleteForwards end of reinsertion");
        Expect.True(!doc.CanUndo && doc.CanRedo, "CheckDeleteForwards exhausted");
    }

    private static void UndoCheckDeleteBackwards()
    {
        var doc = Doc(SText);
        doc.DeleteUndoHistory();
        doc.DeleteChars(5, 1);
        doc.DeleteChars(4, 1);
        doc.DeleteChars(3, 1);
        Expect.Equal(6, doc.Undo(), "CheckDeleteBackwards end of reinsertion");
        Expect.True(!doc.CanUndo, "CheckDeleteBackwards exhausted");
    }

    private static void UndoCheckBothWays()
    {
        var doc = Doc(SText);
        doc.DeleteUndoHistory();
        doc.DeleteChars(5, 1); // Del
        doc.DeleteChars(4, 1); // Backspace
        doc.DeleteChars(4, 1); // Del
        doc.DeleteChars(3, 1); // Backspace
        Expect.Equal(7, doc.Undo(), "CheckBothWays end of reinsertion");
        Expect.True(!doc.CanUndo, "CheckBothWays exhausted");
    }

    private static void UndoCheckInsert()
    {
        var doc = Doc(SText);
        doc.DeleteUndoHistory();
        doc.InsertString(1, "1");
        doc.InsertString(2, "2");
        doc.InsertString(3, "3");
        Expect.Equal("S123cintilla", Contents(doc), "CheckInsert contents");
        Expect.Equal(1, doc.Undo(), "CheckInsert start of insertions");
        Expect.True(!doc.CanUndo, "CheckInsert exhausted");
    }

    private static void UndoCheckGrouped()
    {
        // The position returned for a group is that at the end of the first deletion set; includes a container action.
        var doc = Doc(SText);
        doc.DeleteUndoHistory();
        doc.BeginUndoAction();
        doc.DeleteChars(1, 1); // 'c'
        doc.DeleteChars(1, 1); // 'i'
        doc.AddUndoAction(99, true);
        doc.InsertString(1, "1");
        doc.DeleteChars(4, 2); // 'il'
        doc.BeginUndoAction();
        Expect.Equal("S1ntla", Contents(doc), "CheckGrouped contents");
        Expect.Equal(3, doc.Undo(), "CheckGrouped position");
        Expect.True(!doc.CanUndo, "CheckGrouped exhausted");
    }

    private static void WordsInText()
    {
        var doc = Doc(" abc ");
        Expect.True(doc.IsWordAt(1, 4) && !doc.IsWordAt(0, 1) && !doc.IsWordAt(1, 2), "WordsInText abc");
        var docPunct = Doc(" [!] ");
        Expect.True(docPunct.IsWordAt(1, 4) && !docPunct.IsWordAt(0, 1) && !docPunct.IsWordAt(1, 2), "WordsInText punct");
        var docMixed = Doc(" -ab "); // '-' is punctuation, 'ab' is word.
        Expect.True(docMixed.IsWordAt(2, 4) && docMixed.IsWordAt(1, 4) && docMixed.IsWordAt(1, 2), "WordsInText mixed");
        Expect.True(!docMixed.IsWordAt(1, 3), "WordsInText between a and b");
        // Scintilla's word definition just examines the ends.
        Expect.True(Doc(" a b ").IsWordAt(1, 4), "WordsInText over space");
    }

    private static void WordsAtEnds()
    {
        var doc = Doc("a c");
        Expect.True(doc.IsWordAt(0, 1) && doc.IsWordAt(2, 3), "WordsAtEnds");
        var docEndSpace = Doc(" a c ");
        Expect.True(!docEndSpace.IsWordAt(0, 2) && !docEndSpace.IsWordAt(3, 5), "WordsAtEnds with spaces");
    }

    private static void Utf8Navigation()
    {
        // " a" + GREEK CAPITAL GAMMA (2 bytes) + U+1F600 (4 bytes) + "z" + CR LF + "x"
        var doc = Doc(" aΓ\U0001F600z\r\nx");
        Expect.Equal(12, doc.Length, "UTF-8 length");
        Expect.Equal(4, doc.NextPosition(2, 1), "NextPosition over 2-byte");
        Expect.Equal(8, doc.NextPosition(4, 1), "NextPosition over 4-byte");
        Expect.Equal(4, doc.NextPosition(8, -1), "NextPosition back over 4-byte");
        Expect.Equal(2, doc.NextPosition(4, -1), "NextPosition back over 2-byte");
        Expect.Equal(0, doc.NextPosition(0, -1), "NextPosition at start");
        Expect.Equal(12, doc.NextPosition(11, 1), "NextPosition at end");
        Expect.Equal(2, doc.MovePositionOutsideChar(3, -1), "Outside 2-byte backward");
        Expect.Equal(4, doc.MovePositionOutsideChar(3, 1), "Outside 2-byte forward");
        Expect.Equal(4, doc.MovePositionOutsideChar(6, -1), "Outside 4-byte backward");
        Expect.Equal(8, doc.MovePositionOutsideChar(5, 1), "Outside 4-byte forward");
        Expect.Equal(4, doc.MovePositionOutsideChar(4, 1), "Boundary unchanged");
        Expect.Equal(11, doc.MovePositionOutsideChar(10, 1), "Outside CR LF forward");
        Expect.Equal(9, doc.MovePositionOutsideChar(10, -1), "Outside CR LF backward");
        Expect.Equal(10, doc.MovePositionOutsideChar(10, -1, false), "CR LF unchecked");
        Expect.Equal(8, doc.MovePositionByCharacters(1, 3), "MovePositionByCharacters");
        Expect.Equal(0x1F600, doc.CharacterAfter(4).Character, "CharacterAfter 4-byte");
        Expect.Equal(4, doc.CharacterBefore(8).WidthBytes, "CharacterBefore 4-byte");

        // Invalid UTF-8 moves byte by byte.
        var bad = new Document();
        bad.InsertString(0, new byte[] { (byte)'a', 0x80, 0xC3, (byte)'b' });
        Expect.Equal(2, bad.NextPosition(1, 1), "Isolated trail byte");
        Expect.Equal(3, bad.NextPosition(2, 1), "Truncated lead byte");
        Expect.Equal(2, bad.NextPosition(3, -1), "Back over truncated lead");
        Expect.Equal(2, bad.MovePositionOutsideChar(2, 1), "Invalid stays");
    }

    private static void WordMovement()
    {
        var doc = Doc("foo.bar  bazΓΔ été");
        Expect.Equal(CharacterClass.Word, doc.WordCharacterClass(0x0393), "Greek letter is word");
        Expect.Equal(CharacterClass.Punctuation, doc.WordCharacterClass(0x2014), "Em dash is punctuation");
        Expect.Equal(CharacterClass.Space, doc.WordCharacterClass(0x00A0), "NBSP is space");
        Expect.Equal(CharacterClass.NewLine, doc.WordCharacterClass(0x2028), "LS is new line");
        Expect.Equal(CharacterClass.Word, doc.WordCharacterClass(0x0301), "Combining mark is word");
        Expect.Equal(3, doc.NextWordStart(0, 1), "NextWordStart to punctuation");
        Expect.Equal(4, doc.NextWordStart(3, 1), "NextWordStart to word");
        Expect.Equal(9, doc.NextWordStart(4, 1), "NextWordStart skips spaces");
        Expect.Equal(4, doc.NextWordStart(9, -1), "NextWordStart backward");
        Expect.Equal(7, doc.NextWordEnd(4, 1), "NextWordEnd");
        Expect.Equal(16, doc.NextWordEnd(7, 1), "NextWordEnd over spaces and Greek");
        Expect.Equal(9, doc.WordStartPosition(14, true), "WordStartPosition over Greek");
        Expect.Equal(16, doc.WordEndPosition(10, true), "WordEndPosition over Greek");
        Expect.Equal(doc.Length, doc.WordEndPosition(17, true), "WordEndPosition over accented");
        Expect.Equal(7, doc.WordStartPosition(8, false), "WordStartPosition over spaces");
        Expect.Equal(3, doc.WordEndPosition(3, true), "WordEndPosition only word chars at punctuation");
        Expect.Equal(4, doc.ExtendWordSelect(3, 1, false), "ExtendWordSelect punctuation");
    }

    private static void Notifications()
    {
        var doc = Doc("ab");
        var mw = new ModificationWatcher();
        doc.AddWatcher(mw);
        doc.InsertString(1, "\n\n");
        Expect.Equal(2, mw.Changes.Count, "Insert notifies before and after");
        Expect.Equal(new DocumentChange(ModificationFlags.BeforeInsert | ModificationFlags.User, 1, 2, 0), mw.Changes[0],
            "BeforeInsert");
        Expect.Equal(new DocumentChange(ModificationFlags.InsertText | ModificationFlags.User, 1, 2, 2), mw.Changes[1],
            "InsertText with lines added");
        mw.Clear();
        doc.DeleteChars(1, 2);
        Expect.Equal(new DocumentChange(ModificationFlags.DeleteText | ModificationFlags.User, 1, 2, -2), mw.Changes[1],
            "DeleteText with lines removed");
        mw.Clear();
        int pos = doc.Undo();
        Expect.Equal(3, pos, "Undo of delete returns end of reinsertion");
        var flags = mw.Changes[^1].Flags;
        Expect.True((flags & (ModificationFlags.Undo | ModificationFlags.InsertText | ModificationFlags.LastStepInUndoRedo))
            == (ModificationFlags.Undo | ModificationFlags.InsertText | ModificationFlags.LastStepInUndoRedo), "Undo flags");
        Expect.Equal(3, doc.LinesTotal, "Undo restored lines");
        mw.Clear();
        Expect.Equal(1, doc.Redo(), "Redo of delete returns position");
        Expect.True((mw.Changes[^1].Flags & ModificationFlags.Redo) != 0, "Redo flags");
        Expect.Equal("ab", Contents(doc), "Redo contents");
    }

    private static void ReadOnlyAndSavePoint()
    {
        var doc = Doc("abc");
        var mw = new ModificationWatcher();
        doc.AddWatcher(mw);
        Expect.True(doc.IsModified, "Modified after insert");
        doc.SetSavePoint();
        Expect.True(!doc.IsModified && mw.SavePoints[^1], "Save point set");
        doc.InsertString(3, "d");
        Expect.True(doc.IsModified && !mw.SavePoints[^1], "Left save point");
        doc.Undo();
        Expect.True(!doc.IsModified && mw.SavePoints[^1], "Undo back to save point");
        doc.ReadOnly = true;
        Expect.Equal(0, doc.InsertString(0, "x"), "Read-only insert");
        Expect.True(!doc.DeleteChars(0, 1), "Read-only delete");
        Expect.Equal(-1, doc.Undo(), "Read-only undo");
        doc.ReadOnly = false;
        doc.ClearAll();
        Expect.Equal(0, doc.Length, "ClearAll");
        doc.Undo();
        Expect.Equal("abc", Contents(doc), "ClearAll undoable");
        Expect.True(!doc.DeleteChars(2, 5), "Delete out of range");
    }

    private static void LineEnds()
    {
        var doc = Doc("a\r\nb\rc\nd");
        Expect.Equal(4, doc.LinesTotal, "Mixed line ends");
        Expect.Equal(1, doc.LineEnd(0), "LineEnd CR LF");
        Expect.Equal(4, doc.LineEnd(1), "LineEnd CR");
        Expect.Equal(6, doc.LineEnd(2), "LineEnd LF");
        Expect.Equal(6, doc.LineEndPosition(5), "LineEndPosition");
        doc.ConvertLineEnds(EndOfLine.Lf);
        Expect.Equal("a\nb\nc\nd", Contents(doc), "ConvertLineEnds LF");
        doc.ConvertLineEnds(EndOfLine.CrLf);
        Expect.Equal("a\r\nb\r\nc\r\nd", Contents(doc), "ConvertLineEnds CR LF");
        doc.ConvertLineEnds(EndOfLine.Cr);
        Expect.Equal("a\rb\rc\rd", Contents(doc), "ConvertLineEnds CR");
        Expect.Equal(4, doc.LinesTotal, "Lines after conversion");
        Expect.Equal("x\r\ny\r\nz\r\n", Encoding.UTF8.GetString(Document.TransformLineEnds("x\ny\rz\r\n"u8, EndOfLine.CrLf)),
            "TransformLineEnds");
        var home = Doc("  \tab");
        Expect.Equal(3, home.VCHomePosition(5), "VCHome to text");
        Expect.Equal(0, home.VCHomePosition(3), "VCHome to line start");
    }

    private static void IndicatorsAndMargins()
    {
        var doc = Doc("0123456789\nline");
        var mw = new ModificationWatcher();
        doc.AddWatcher(mw);
        doc.SetIndicatorCurrent(1);
        doc.DecorationFillRange(2, 5, 3);
        Expect.Equal(new DocumentChange(ModificationFlags.ChangeIndicator | ModificationFlags.User, 2, 3, 0), mw.Changes[^1],
            "Indicator fill notification");
        Expect.Equal(5, doc.Decorations.ValueAt(1, 3), "Indicator value");
        doc.InsertString(0, "ab");
        Expect.Equal(5, doc.Decorations.ValueAt(1, 5), "Indicator moves with insertion");
        Expect.Equal(0, doc.Decorations.ValueAt(1, 3), "Indicator not at old position");
        mw.Clear();
        doc.DecorationFillRange(0, 0, doc.Length);
        Expect.Equal(0, doc.Decorations.ValueAt(1, 5), "Indicator cleared");

        doc.MarginSetText(1, "E");
        doc.MarginSetStyle(1, 40);
        Expect.True((mw.Changes[^1].Flags & ModificationFlags.ChangeMargin) != 0, "Margin notification");
        Expect.Equal("E", doc.MarginText.Text(1), "Margin text");
        doc.InsertString(0, "new\n");
        Expect.Equal("E", doc.MarginText.Text(2), "Margin text follows its line");
        Expect.Equal(40, doc.MarginText.Style(2), "Margin style follows its line");
        doc.MarginClearAll();
        Expect.Equal(null, doc.MarginText.Text(2), "Margin cleared");
    }

    // testDocument.cxx PerLine "LineAnnotation", using the margin-text LineAnnotation (the port has no separate annotations).
    private static void PerLineLineAnnotation()
    {
        var doc = Doc("1\n2\n");
        Expect.Equal(3, doc.LinesTotal, "PerLine annotation lines total");
        int length = doc.Length;
        doc.MarginSetText(0, "1");
        doc.MarginSetText(1, "1\n2");
        doc.MarginSetText(2, "1\n2\n3");
        Expect.Equal(1, doc.MarginText.Lines(0), "Annotation lines 0");
        Expect.Equal(2, doc.MarginText.Lines(1), "Annotation lines 1");
        Expect.Equal(3, doc.MarginText.Lines(2), "Annotation lines 2");
        Expect.Equal(0, doc.MarginText.Lines(3), "Annotation lines 3");

        // Deleting the last line moves its 3-line annotation to the previous line.
        length -= 1;
        doc.DeleteChars(length, 1);
        Expect.Equal(2, doc.LinesTotal, "Delete last line total");
        Expect.Equal(1, doc.MarginText.Lines(0), "Delete last line 0");
        Expect.Equal(3, doc.MarginText.Lines(1), "Delete last line 1");
        Expect.Equal(0, doc.MarginText.Lines(2), "Delete last line 2");

        length -= 1;
        Expect.Equal((byte)'2', doc.CharAt(length), "Last character");
        doc.DeleteChars(length, 1);
        Expect.Equal(2, doc.LinesTotal, "Delete last character total");
        Expect.Equal(1, doc.MarginText.Lines(0), "Delete last character 0");
        Expect.Equal(3, doc.MarginText.Lines(1), "Delete last character 1");
        Expect.Equal(0, doc.MarginText.Lines(2), "Delete last character 2");
    }
}