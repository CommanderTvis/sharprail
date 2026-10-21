using System.Text;

using SharpRail.Scintilla.Core;

namespace SharpRail.Checks.ScintillaCore;

/// <summary>Port of Scintilla test/unit/testCellBuffer.cxx (CellBuffer and UndoHistory). Not ported: ScrapStack,
/// ScaledVector and UndoActions (replaced by a List of actions), tentative undo (IME only), Unicode line ends,
/// CharacterIndex (no UTF-16/32 line indices), ChangeHistory and undo-history loading (not used).</summary>
internal static class CellBufferChecks
{
    private static readonly byte[] SText = "Scintilla"u8.ToArray();

    internal static void Run()
    {
        InsertOneLine();
        InsertTwoLines();
        LineEnds();
        LineEndEdits();
        UndoOff();
        UndoRedo();
        ReadOnly();
        Styles();
        UndoBasics();
        EnsureTruncationAfterUndo();
        Coalesce();
        SimpleContainer();
        CoalesceContainer();
        Grouping();
        DeepGroup();
        RandomEdits();
    }

    // Skipped upstream sections (feature not in the managed core):
    // ScrapStack "Push": no ScrapStack; undo text is stored per action.
    // CellBuffer "LineEndTypes": no Get/SetLineEndTypes; only CR, LF and CR LF are line ends (no NEL/LS/PS).
    // ScaledVector "ScalingUp": no ScaledVector; UndoActions is a List of UndoAction.
    // CharacterIndex "Setup", "Insertion", "Deletion", "Insert Complex", "Delete Multiple lines", "Delete Complex",
    // "Insert separates new line bytes": no UTF-16/UTF-32 line character index.
    // ChangeHistory "Start", "Some Space", "An insert", "A delete", "Insert, delete, and undo", "Deletes",
    // "Deletes 101", "Deletes Stack", "Delete Contiguous Backward", "Delete Contiguous Forward": no ChangeHistory.
    // CellBufferWithChangeHistory "StraightUndoRedoSaveRevertRedo", "Detached": no ChangeHistory.
    // CellBufferLoadUndoHistory "Basics", "Detached": no undo-history loading API.
    // UndoHistory "Tentative": tentative undo (IME) not ported.

    private static string Str(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static void InsertOneLine()
    {
        var cb = new CellBuffer();
        byte[] change = cb.InsertString(0, SText, out bool startSequence);
        Expect.True(startSequence, "InsertOneLine startSequence");
        Expect.Equal(SText.Length, cb.Length, "InsertOneLine length");
        Expect.Equal("Scintilla", Str(change), "InsertOneLine change");
        Expect.Equal(1, cb.Lines, "InsertOneLine lines");
        Expect.Equal(0, cb.LineStart(0), "InsertOneLine LineStart(0)");
        Expect.Equal(0, cb.LineFromPosition(0), "InsertOneLine LineFromPosition(0)");
        Expect.Equal(SText.Length, cb.LineStart(1), "InsertOneLine LineStart(1)");
        Expect.Equal(0, cb.LineFromPosition(SText.Length), "InsertOneLine LineFromPosition(end)");
        Expect.True(cb.CanUndo, "InsertOneLine CanUndo");
        Expect.True(!cb.CanRedo, "InsertOneLine !CanRedo");
    }

    private static void InsertTwoLines()
    {
        var cb = new CellBuffer();
        byte[] text = "Two\nLines"u8.ToArray();
        byte[] change = cb.InsertString(0, text, out bool startSequence);
        Expect.True(startSequence, "InsertTwoLines startSequence");
        Expect.Equal(text.Length, cb.Length, "InsertTwoLines length");
        Expect.Equal("Two\nLines", Str(change), "InsertTwoLines change");
        Expect.Equal(2, cb.Lines, "InsertTwoLines lines");
        Expect.Equal(0, cb.LineStart(0), "InsertTwoLines LineStart(0)");
        Expect.Equal(0, cb.LineFromPosition(0), "InsertTwoLines LineFromPosition(0)");
        Expect.Equal(4, cb.LineStart(1), "InsertTwoLines LineStart(1)");
        Expect.Equal(1, cb.LineFromPosition(5), "InsertTwoLines LineFromPosition(5)");
        Expect.Equal(text.Length, cb.LineStart(2), "InsertTwoLines LineStart(2)");
        Expect.Equal(1, cb.LineFromPosition(text.Length), "InsertTwoLines LineFromPosition(end)");
        Expect.True(cb.CanUndo, "InsertTwoLines CanUndo");
        Expect.True(!cb.CanRedo, "InsertTwoLines !CanRedo");
    }

    private static void LineEnds()
    {
        // Various line ends produce the correct LineEnd. Unicode NEL/LS cases are not ported.
        var cb = new CellBuffer();
        foreach (string s in new[] { "Two\nLines", "Two\r\nLines", "Two\rLines" })
        {
            byte[] text = Encoding.UTF8.GetBytes(s);
            cb.InsertString(0, text, out _);
            Expect.Equal(3, cb.LineEnd(0), $"LineEnd(0) of {s.ReplaceLineEndings("|")}");
            Expect.Equal(text.Length, cb.LineEnd(1), $"LineEnd(1) of {s.ReplaceLineEndings("|")}");
            cb.DeleteChars(0, text.Length, out _);
        }
        cb.InsertString(0, "Two\nLines"u8, out _);
        Expect.True(cb.LineEnd(-1) < 0, "LineEnd before start");
    }

    // Not upstream: the CR LF joining and splitting paths of insertion and deletion.
    private static void LineEndEdits()
    {
        var cb = new CellBuffer();
        cb.InsertString(0, "a\rb"u8, out _);
        Expect.Equal(2, cb.Lines, "CR line");
        cb.InsertString(2, "\n"u8, out _); // a\r\nb: LF joins the CR.
        Expect.Equal(2, cb.Lines, "LF after CR joins");
        Expect.Equal(3, cb.LineStart(1), "Line start after CR LF");
        Expect.Equal(1, cb.LineEnd(0), "LineEnd before CR LF");
        cb.InsertString(2, "x"u8, out _); // a\rx\nb: splits the pair.
        Expect.Equal(3, cb.Lines, "Splitting CR LF");
        Expect.Equal(2, cb.LineStart(1), "Split line 1");
        Expect.Equal(4, cb.LineStart(2), "Split line 2");
        cb.DeleteChars(2, 1, out _); // Back to a\r\nb.
        Expect.Equal(2, cb.Lines, "Deleting between CR and LF rejoins");
        Expect.Equal(3, cb.LineStart(1), "Rejoined line start");
        cb.DeleteChars(1, 1, out _); // a\nb
        Expect.Equal(2, cb.Lines, "Deleting the CR keeps the LF line");
        Expect.Equal(2, cb.LineStart(1), "LF line start");
        cb.InsertString(1, "\r"u8, out _); // a\r\nb: CR before LF.
        Expect.Equal(2, cb.Lines, "Inserting CR before LF");
        Expect.Equal(3, cb.LineStart(1), "CR LF line start after inserting CR");
        cb.InsertString(4, "1\r\n2\n3\r"u8, out _);
        Expect.Equal(5, cb.Lines, "Inserting several line ends");
        cb.DeleteChars(0, cb.Length, out _);
        Expect.Equal(1, cb.Lines, "Deleting all");
    }

    private static void UndoOff()
    {
        var cb = new CellBuffer();
        Expect.True(cb.IsCollectingUndo, "UndoOff initially collecting");
        cb.SetUndoCollection(false);
        Expect.True(!cb.IsCollectingUndo, "UndoOff not collecting");
        byte[] change = cb.InsertString(0, SText, out bool startSequence);
        Expect.True(!startSequence, "UndoOff startSequence");
        Expect.Equal(SText.Length, cb.Length, "UndoOff length");
        Expect.Equal("Scintilla", Str(change), "UndoOff change");
        Expect.True(!cb.CanUndo, "UndoOff !CanUndo");
        Expect.True(!cb.CanRedo, "UndoOff !CanRedo");
    }

    private static void UndoRedo()
    {
        var cb = new CellBuffer();
        byte[] change = cb.InsertString(0, SText, out bool startSequence);
        Expect.True(startSequence, "UndoRedo insert startSequence");
        Expect.Equal("Scintilla", Str(change), "UndoRedo change");
        Expect.Equal("Scintilla", Str(cb.ToArray()), "UndoRedo buffer");
        Expect.True(cb.CanUndo && !cb.CanRedo, "UndoRedo after insert");
        byte[] deletion = cb.DeleteChars(1, 2, out startSequence);
        Expect.True(startSequence, "UndoRedo delete startSequence");
        Expect.Equal("ci", Str(deletion), "UndoRedo deletion");
        Expect.Equal("Sntilla", Str(cb.ToArray()), "UndoRedo after deletion");
        Expect.True(cb.CanUndo && !cb.CanRedo, "UndoRedo after delete");

        Expect.Equal(1, cb.StartUndo(), "UndoRedo undo 1 steps");
        cb.PerformUndoStep();
        Expect.Equal("Scintilla", Str(cb.ToArray()), "UndoRedo undo 1");
        Expect.True(cb.CanUndo && cb.CanRedo, "UndoRedo undo 1 state");

        Expect.Equal(1, cb.StartUndo(), "UndoRedo undo 2 steps");
        cb.PerformUndoStep();
        Expect.Equal(0, cb.Length, "UndoRedo undo 2");
        Expect.True(!cb.CanUndo && cb.CanRedo, "UndoRedo undo 2 state");

        Expect.Equal(1, cb.StartRedo(), "UndoRedo redo 1 steps");
        cb.PerformRedoStep();
        Expect.Equal("Scintilla", Str(cb.ToArray()), "UndoRedo redo 1");
        Expect.True(cb.CanUndo && cb.CanRedo, "UndoRedo redo 1 state");

        Expect.Equal(1, cb.StartRedo(), "UndoRedo redo 2 steps");
        cb.PerformRedoStep();
        Expect.Equal("Sntilla", Str(cb.ToArray()), "UndoRedo redo 2");
        Expect.True(cb.CanUndo && !cb.CanRedo, "UndoRedo redo 2 state");

        cb.DeleteUndoHistory();
        Expect.True(!cb.CanUndo && !cb.CanRedo, "UndoRedo history deleted");
    }

    private static void ReadOnly()
    {
        var cb = new CellBuffer();
        Expect.True(!cb.ReadOnly, "ReadOnly initially off");
        cb.ReadOnly = true;
        Expect.True(cb.ReadOnly, "ReadOnly set");
        cb.InsertString(0, SText, out _);
        Expect.Equal(0, cb.Length, "ReadOnly blocks insertion");
    }

    // Not upstream as a section: style bytes follow insertion and deletion.
    private static void Styles()
    {
        var cb = new CellBuffer();
        cb.InsertString(0, SText, out _);
        Expect.True(cb.SetStyleAt(2, 7), "SetStyleAt changes");
        Expect.True(!cb.SetStyleAt(2, 7), "SetStyleAt same value");
        Expect.True(cb.SetStyleFor(4, 3, 5), "SetStyleFor changes");
        Expect.True(!cb.SetStyleFor(4, 3, 5), "SetStyleFor same value");
        cb.InsertString(0, "ab"u8, out _);
        Expect.Equal((byte)0, cb.StyleAt(0), "Inserted text unstyled");
        Expect.Equal((byte)7, cb.StyleAt(4), "Style moved with text");
        cb.DeleteChars(0, 5, out _);
        var styles = new byte[4];
        cb.GetStyleRange(styles, 0, 4);
        Expect.Equal("0,5,5,5", string.Join(",", styles), "Styles after deletion");
    }

    private static bool Equal(UndoAction a, ActionType at, int position, string value) =>
        a.Type == at && a.Position == position && Encoding.UTF8.GetString(a.Data ?? []) == value;

    private static bool EqualContainerAction(UndoAction a, int token) =>
        a.Type == ActionType.Container && a.Position == token && a.Length == 0 && a.Data is null;

    private static void UndoBasics()
    {
        var uh = new UndoHistory();
        Expect.True(uh.IsSavePoint && uh.AfterSavePoint && !uh.BeforeSavePoint && !uh.BeforeReachableSavePoint,
            "Basics initial save point");
        Expect.True(!uh.CanUndo && !uh.CanRedo, "Basics initial undo");
        byte[]? val = uh.AppendAction(ActionType.Insert, 0, "ab"u8, out bool startSequence);
        Expect.Equal("ab", Str(val!), "Basics insert data");
        Expect.True(startSequence, "Basics insert startSequence");
        Expect.True(!uh.IsSavePoint && uh.AfterSavePoint && uh.CanUndo && !uh.CanRedo, "Basics after insert");
        val = uh.AppendAction(ActionType.Remove, 0, "ab"u8, out startSequence);
        Expect.Equal("ab", Str(val!), "Basics remove data");
        Expect.True(startSequence, "Basics remove startSequence");

        Expect.Equal(1, uh.StartUndo(), "Basics undo 1 steps");
        Expect.True(Equal(uh.GetUndoStep(), ActionType.Remove, 0, "ab"), "Basics undo 1 action");
        uh.CompletedUndoStep();
        Expect.Equal(1, uh.StartUndo(), "Basics undo 2 steps");
        Expect.True(Equal(uh.GetUndoStep(), ActionType.Insert, 0, "ab"), "Basics undo 2 action");
        uh.CompletedUndoStep();
        Expect.True(uh.IsSavePoint, "Basics back at save point");

        Expect.Equal(1, uh.StartRedo(), "Basics redo 1 steps");
        Expect.True(Equal(uh.GetRedoStep(), ActionType.Insert, 0, "ab"), "Basics redo 1 action");
        uh.CompletedRedoStep();
        Expect.Equal(1, uh.StartRedo(), "Basics redo 2 steps");
        Expect.True(Equal(uh.GetRedoStep(), ActionType.Remove, 0, "ab"), "Basics redo 2 action");
        uh.CompletedRedoStep();
        Expect.True(!uh.IsSavePoint, "Basics after redo");
    }

    private static void EnsureTruncationAfterUndo()
    {
        var uh = new UndoHistory();
        Expect.Equal(0, uh.Actions, "Truncation initial");
        uh.AppendAction(ActionType.Insert, 0, "ab"u8, out _);
        Expect.Equal(1, uh.Actions, "Truncation 1");
        uh.AppendAction(ActionType.Insert, 2, "cd"u8, out _);
        Expect.Equal(2, uh.Actions, "Truncation 2");
        Expect.True(uh.CanUndo && !uh.CanRedo, "Truncation undo state");
        Expect.Equal(2, uh.StartUndo(), "Truncation steps");
        uh.CompletedUndoStep();
        Expect.Equal(2, uh.Actions, "Not truncated until forward action");
        uh.CompletedUndoStep();
        Expect.Equal(2, uh.Actions, "Not truncated after full undo");
        uh.AppendAction(ActionType.Insert, 0, "12"u8, out _);
        Expect.Equal(1, uh.Actions, "Truncated by new action");
    }

    private static void Coalesce()
    {
        var uh = new UndoHistory();
        uh.AppendAction(ActionType.Insert, 0, "ab"u8, out bool startSequence);
        Expect.True(startSequence, "Coalesce first startSequence");
        Expect.True(!uh.IsSavePoint && uh.AfterSavePoint && uh.CanUndo && !uh.CanRedo, "Coalesce state");
        byte[]? val = uh.AppendAction(ActionType.Insert, 2, "cd"u8, out startSequence);
        Expect.Equal("cd", Str(val!), "Coalesce second data");
        Expect.True(!startSequence, "Coalesce second coalesces");

        Expect.Equal(2, uh.StartUndo(), "Coalesce undo steps");
        Expect.True(Equal(uh.GetUndoStep(), ActionType.Insert, 2, "cd"), "Coalesce undo 2");
        uh.CompletedUndoStep();
        Expect.True(Equal(uh.GetUndoStep(), ActionType.Insert, 0, "ab"), "Coalesce undo 1");
        uh.CompletedUndoStep();
        Expect.True(uh.IsSavePoint, "Coalesce undone to save point");

        Expect.Equal(2, uh.StartRedo(), "Coalesce redo steps");
        Expect.True(Equal(uh.GetRedoStep(), ActionType.Insert, 0, "ab"), "Coalesce redo 1");
        uh.CompletedRedoStep();
        Expect.True(Equal(uh.GetRedoStep(), ActionType.Insert, 2, "cd"), "Coalesce redo 2");
        uh.CompletedRedoStep();
        Expect.True(!uh.IsSavePoint, "Coalesce redone");
    }

    private static void SimpleContainer()
    {
        var uh = new UndoHistory();
        byte[]? val = uh.AppendAction(ActionType.Container, 1000, [], out bool startSequence);
        Expect.True(startSequence && val is null, "SimpleContainer first");
        val = uh.AppendAction(ActionType.Container, 1001, [], out startSequence);
        Expect.True(!startSequence && val is null, "SimpleContainer second");
    }

    private static void CoalesceContainer()
    {
        var uh = new UndoHistory();
        uh.AppendAction(ActionType.Insert, 0, "ab"u8, out bool startSequence);
        Expect.True(startSequence, "CoalesceContainer insert");
        byte[]? val = uh.AppendAction(ActionType.Container, 1000, [], out startSequence);
        Expect.True(!startSequence && val is null, "CoalesceContainer container 1000");
        uh.AppendAction(ActionType.Container, 1001, [], out startSequence);
        Expect.True(!startSequence, "CoalesceContainer container 1001");
        // Coalescible since container actions are skipped to determine compatibility.
        val = uh.AppendAction(ActionType.Insert, 2, "cd"u8, out startSequence);
        Expect.True(!startSequence && Str(val!) == "cd", "CoalesceContainer second insert");
        // Break the sequence with a non-coalescible container action.
        uh.AppendAction(ActionType.Container, 1002, [], out startSequence, false);
        Expect.True(startSequence, "CoalesceContainer breaking container");

        Expect.Equal(1, uh.StartUndo(), "CoalesceContainer first undo steps");
        var container = uh.GetUndoStep();
        Expect.True(EqualContainerAction(container, 1002) && !container.MayCoalesce, "CoalesceContainer 1002");
        uh.CompletedUndoStep();
        Expect.Equal(4, uh.StartUndo(), "CoalesceContainer second undo steps");
        Expect.True(Equal(uh.GetUndoStep(), ActionType.Insert, 2, "cd"), "CoalesceContainer undo cd");
        uh.CompletedUndoStep();
        Expect.True(EqualContainerAction(uh.GetUndoStep(), 1001), "CoalesceContainer undo 1001");
        uh.CompletedUndoStep();
        Expect.True(EqualContainerAction(uh.GetUndoStep(), 1000), "CoalesceContainer undo 1000");
        uh.CompletedUndoStep();
        Expect.True(Equal(uh.GetUndoStep(), ActionType.Insert, 0, "ab"), "CoalesceContainer undo ab");
        uh.CompletedUndoStep();
        Expect.True(!uh.CanUndo, "CoalesceContainer reached beginning");
    }

    private static void Grouping()
    {
        var uh = new UndoHistory();
        uh.BeginUndoAction();
        uh.AppendAction(ActionType.Insert, 0, "ab"u8, out bool startSequence);
        Expect.True(startSequence, "Grouping first");
        Expect.True(!uh.IsSavePoint && uh.AfterSavePoint && uh.CanUndo && !uh.CanRedo, "Grouping state");
        uh.AppendAction(ActionType.Remove, 0, "ab"u8, out startSequence);
        Expect.True(!startSequence, "Grouping remove coalesces");
        byte[]? val = uh.AppendAction(ActionType.Insert, 0, "cde"u8, out startSequence);
        Expect.True(!startSequence && Str(val!) == "cde", "Grouping insert coalesces");
        uh.EndUndoAction();

        Expect.Equal(3, uh.StartUndo(), "Grouping undo steps");
        Expect.True(Equal(uh.GetUndoStep(), ActionType.Insert, 0, "cde"), "Grouping undo 3");
        uh.CompletedUndoStep();
        Expect.True(Equal(uh.GetUndoStep(), ActionType.Remove, 0, "ab"), "Grouping undo 2");
        uh.CompletedUndoStep();
        Expect.True(Equal(uh.GetUndoStep(), ActionType.Insert, 0, "ab"), "Grouping undo 1");
        uh.CompletedUndoStep();
        Expect.True(uh.IsSavePoint, "Grouping undone");

        Expect.Equal(3, uh.StartRedo(), "Grouping redo steps");
        Expect.True(Equal(uh.GetRedoStep(), ActionType.Insert, 0, "ab"), "Grouping redo 1");
        uh.CompletedRedoStep();
        Expect.True(Equal(uh.GetRedoStep(), ActionType.Remove, 0, "ab"), "Grouping redo 2");
        uh.CompletedRedoStep();
        Expect.True(Equal(uh.GetRedoStep(), ActionType.Insert, 0, "cde"), "Grouping redo 3");
        uh.CompletedRedoStep();
        Expect.True(!uh.IsSavePoint, "Grouping redone");
    }

    private static void DeepGroup()
    {
        var uh = new UndoHistory();
        uh.BeginUndoAction();
        uh.BeginUndoAction();
        uh.AppendAction(ActionType.Insert, 0, "ab"u8, out bool startSequence);
        Expect.True(startSequence, "DeepGroup first");
        byte[]? val = uh.AppendAction(ActionType.Container, 1000, [], out startSequence, false);
        Expect.True(val is null && !startSequence, "DeepGroup container");
        uh.AppendAction(ActionType.Remove, 0, "ab"u8, out startSequence);
        Expect.True(!startSequence, "DeepGroup remove");
        uh.AppendAction(ActionType.Insert, 0, "cde"u8, out startSequence);
        Expect.True(!startSequence, "DeepGroup insert");
        uh.EndUndoAction();
        uh.EndUndoAction();
        Expect.Equal(4, uh.StartUndo(), "DeepGroup steps");
    }

    // Port of CellBufferLong "Random": random edits stay consistent with a reference string, and undo restores.
    private static void RandomEdits()
    {
        var random = new Random(42);
        var cb = new CellBuffer();
        var reference = new StringBuilder();
        const string alphabet = "ab\r\n";
        for (int i = 0; i < 2000; i++)
        {
            if (reference.Length == 0 || random.Next(3) != 0)
            {
                int pos = random.Next(reference.Length + 1);
                var piece = new StringBuilder();
                for (int n = random.Next(1, 5); n > 0; n--) piece.Append(alphabet[random.Next(alphabet.Length)]);
                cb.InsertString(pos, Encoding.ASCII.GetBytes(piece.ToString()), out _);
                reference.Insert(pos, piece.ToString());
            }
            else
            {
                int pos = random.Next(reference.Length);
                int len = random.Next(1, Math.Min(4, reference.Length - pos) + 1);
                cb.DeleteChars(pos, len, out _);
                reference.Remove(pos, len);
            }
        }
        string text = reference.ToString();
        Expect.Equal(text, Str(cb.ToArray()), "Random text");
        // Line starts match a direct scan.
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if (text[i] is '\r' or '\n') starts.Add(i + 1);
        }
        Expect.Equal(starts.Count, cb.Lines, "Random line count");
        for (int line = 0; line < starts.Count; line++)
            Expect.Equal(starts[line], cb.LineStart(line), $"Random LineStart({line})");
        while (cb.CanUndo)
        {
            int steps = cb.StartUndo();
            for (int s = 0; s < steps; s++) cb.PerformUndoStep();
        }
        Expect.Equal(0, cb.Length, "Random fully undone");
        Expect.Equal(1, cb.Lines, "Random fully undone lines");
    }
}