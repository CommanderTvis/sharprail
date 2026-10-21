using System.Globalization;
using System.Text;

namespace SharpRail.Scintilla.Core;

/// <summary>Document over CellBuffer: styling, line endings, word/character navigation, decorations, margin text (upstream src/Document.h/.cxx).</summary>
/// <remarks>UTF-8 only (code page 65001). Character decoding uses <see cref="Rune"/> and word classes above ASCII
/// use <see cref="CharUnicodeInfo"/> instead of upstream's UniConversion and CharacterCategoryMap. Search, regex,
/// case folding, lexers, folding, markers, line state, annotations, change history, DBCS and tentative (IME) undo
/// are not ported.</remarks>
internal sealed class Document
{
    private readonly CellBuffer cb;
    private readonly List<IDocumentWatcher> watchers = [];
    private int enteredModification;
    private int enteredStyling;
    private int endStyled;

    internal Document()
    {
        cb = new CellBuffer(MarginText);
    }

    internal DecorationList Decorations { get; } = new();

    /// <summary>Margin text and style per line (SCI_MARGINSETTEXT/SETSTYLE).</summary>
    internal LineAnnotation MarginText { get; } = new();

    internal CharClassify CharClass { get; } = new();

    internal EndOfLine EolMode { get; set; } = OperatingSystem.IsWindows() ? EndOfLine.CrLf : EndOfLine.Lf;

    internal ReadOnlySpan<byte> EolBytes => EolMode switch
    {
        EndOfLine.CrLf => "\r\n"u8,
        EndOfLine.Cr => "\r"u8,
        _ => "\n"u8,
    };

    // Watchers

    internal void AddWatcher(IDocumentWatcher watcher)
    {
        if (!watchers.Contains(watcher)) watchers.Add(watcher);
    }

    internal void RemoveWatcher(IDocumentWatcher watcher) => watchers.Remove(watcher);

    private void NotifyModified(ModificationFlags flags, int position, int length, int linesAdded = 0)
    {
        if ((flags & ModificationFlags.InsertText) != 0)
            Decorations.InsertSpace(position, length);
        else if ((flags & ModificationFlags.DeleteText) != 0)
            Decorations.DeleteRange(position, length);
        var change = new DocumentChange(flags, position, length, linesAdded);
        foreach (var watcher in watchers.ToArray()) watcher.Modified(change);
    }

    private void NotifySavePoint(bool atSavePoint)
    {
        foreach (var watcher in watchers.ToArray()) watcher.SavePointChanged(atSavePoint);
    }

    // Text and lines

    internal int Length => cb.Length;

    internal int LinesTotal => cb.Lines;

    internal bool ReadOnly
    {
        get => cb.ReadOnly;
        set => cb.ReadOnly = value;
    }

    internal bool IsModified => !cb.IsSavePoint;

    internal byte CharAt(int position) => cb.CharAt(position);

    internal byte StyleAt(int position) => cb.StyleAt(position);

    internal void GetCharRange(Span<byte> buffer, int position, int length) => cb.GetCharRange(buffer, position, length);

    internal void GetStyleRange(Span<byte> buffer, int position, int length) => cb.GetStyleRange(buffer, position, length);

    internal byte[] GetBytes(int position, int length)
    {
        position = Math.Clamp(position, 0, Length);
        length = Math.Clamp(length, 0, Length - position);
        var bytes = new byte[length];
        cb.GetCharRange(bytes, position, length);
        return bytes;
    }

    internal string GetText(int position, int length) => Encoding.UTF8.GetString(GetBytes(position, length));

    internal string Text => GetText(0, Length);

    internal int LineStart(int line) => cb.LineStart(line);

    internal int LineEnd(int line) => cb.LineEnd(line);

    internal int LineFromPosition(int position) => cb.LineFromPosition(position);

    internal int LineStartPosition(int position) => LineStart(LineFromPosition(position));

    internal int LineEndPosition(int position) => LineEnd(LineFromPosition(position));

    internal bool IsLineStartPosition(int position) => LineStart(LineFromPosition(position)) == position;

    internal bool IsCrLf(int position)
    {
        if (position < 0 || position >= Length - 1) return false;
        return cb.CharAt(position) == '\r' && cb.CharAt(position + 1) == '\n';
    }

    /// <summary>Line start of <paramref name="position"/>'s line, or of its first non-blank character when the
    /// position is elsewhere (Home toggling, SCI_VCHOME).</summary>
    internal int VCHomePosition(int position)
    {
        int line = LineFromPosition(position);
        int startPosition = LineStart(line);
        int endLine = LineEnd(line);
        int startText = startPosition;
        while (startText < endLine && cb.CharAt(startText) is (byte)' ' or (byte)'\t') startText++;
        return position == startText ? startPosition : startText;
    }

    private void ModifiedAt(int position)
    {
        if (endStyled > position) endStyled = position;
    }

    // Modification

    internal int InsertString(int position, string text) => InsertString(position, Encoding.UTF8.GetBytes(text));

    /// <summary>Inserts bytes, returning the length inserted (0 when read-only or re-entered).</summary>
    internal int InsertString(int position, ReadOnlySpan<byte> s)
    {
        if (s.Length <= 0 || cb.ReadOnly || enteredModification != 0) return 0;
        position = Math.Clamp(position, 0, Length);
        enteredModification++;
        try
        {
            NotifyModified(ModificationFlags.BeforeInsert | ModificationFlags.User, position, s.Length);
            int prevLinesTotal = LinesTotal;
            bool startSavePoint = cb.IsSavePoint;
            cb.InsertString(position, s);
            if (startSavePoint && cb.IsCollectingUndo) NotifySavePoint(false);
            ModifiedAt(position);
            NotifyModified(ModificationFlags.InsertText | ModificationFlags.User, position, s.Length,
                LinesTotal - prevLinesTotal);
        }
        finally
        {
            enteredModification--;
        }
        return s.Length;
    }

    /// <summary>Deletes bytes; false when out of range, read-only or re-entered.</summary>
    internal bool DeleteChars(int position, int length)
    {
        if (position < 0 || length <= 0 || position + length > Length) return false;
        if (enteredModification != 0) return false;
        if (cb.ReadOnly) return false;
        enteredModification++;
        try
        {
            NotifyModified(ModificationFlags.BeforeDelete | ModificationFlags.User, position, length);
            int prevLinesTotal = LinesTotal;
            bool startSavePoint = cb.IsSavePoint;
            cb.DeleteChars(position, length, out _);
            if (startSavePoint && cb.IsCollectingUndo) NotifySavePoint(false);
            ModifiedAt(position < Length || position == 0 ? position : NextPosition(position, -1));
            NotifyModified(ModificationFlags.DeleteText | ModificationFlags.User, position, length,
                LinesTotal - prevLinesTotal);
        }
        finally
        {
            enteredModification--;
        }
        return true;
    }

    /// <summary>Deletes all text as one undo step.</summary>
    internal void ClearAll()
    {
        BeginUndoAction();
        DeleteChars(0, Length);
        EndUndoAction();
    }

    // Undo

    internal bool IsCollectingUndo => cb.IsCollectingUndo;

    internal void SetUndoCollection(bool collect) => cb.SetUndoCollection(collect);

    internal bool CanUndo => cb.CanUndo;

    internal bool CanRedo => cb.CanRedo;

    internal void BeginUndoAction(bool coalesceWithPrior = false) => cb.BeginUndoAction(coalesceWithPrior);

    internal void EndUndoAction() => cb.EndUndoAction();

    internal void AddUndoAction(int token, bool mayCoalesce) => cb.AddUndoAction(token, mayCoalesce);

    internal void DeleteUndoHistory() => cb.DeleteUndoHistory();

    internal void SetSavePoint()
    {
        cb.SetSavePoint();
        NotifySavePoint(true);
    }

    internal bool IsSavePoint => cb.IsSavePoint;

    /// <summary>Undoes one step and returns the caret position after it (end of reinserted text), or -1.</summary>
    internal int Undo()
    {
        int newPos = -1;
        if (enteredModification != 0 || !cb.IsCollectingUndo || cb.ReadOnly) return newPos;
        enteredModification++;
        try
        {
            bool startSavePoint = cb.IsSavePoint;
            int steps = cb.StartUndo();
            int coalescedStart = 0, coalescedEnd = 0; // Coalesced removals being reinserted; empty at 0.
            for (int step = 0; step < steps; step++)
            {
                int prevLinesTotal = LinesTotal;
                var action = cb.GetUndoStep();
                if (action.Type == ActionType.Remove)
                    NotifyModified(ModificationFlags.BeforeInsert | ModificationFlags.Undo, action.Position, action.Length);
                else if (action.Type == ActionType.Insert)
                    NotifyModified(ModificationFlags.BeforeDelete | ModificationFlags.Undo, action.Position, action.Length);
                cb.PerformUndoStep();
                if (action.Type != ActionType.Container)
                {
                    ModifiedAt(action.Type == ActionType.Insert && action.Position >= Length && action.Position > 0
                        ? NextPosition(action.Position, -1)
                        : action.Position);
                    newPos = action.Position;
                }

                var flags = ModificationFlags.Undo;
                // With undo, an insertion becomes a deletion notification and vice versa.
                if (action.Type == ActionType.Remove)
                {
                    newPos += action.Length;
                    flags |= ModificationFlags.InsertText;
                    if (action.Position >= coalescedStart && action.Position <= coalescedEnd)
                    {
                        coalescedEnd += action.Length;
                        newPos = coalescedEnd;
                    }
                    else
                        (coalescedStart, coalescedEnd) = (action.Position, action.Position + action.Length);
                }
                else if (action.Type == ActionType.Insert)
                {
                    flags |= ModificationFlags.DeleteText;
                    (coalescedStart, coalescedEnd) = (0, 0);
                }
                if (steps > 1) flags |= ModificationFlags.MultiStepUndoRedo;
                if (step == steps - 1) flags |= ModificationFlags.LastStepInUndoRedo;
                if (action.Type != ActionType.Container)
                    NotifyModified(flags, action.Position, action.Length, LinesTotal - prevLinesTotal);
            }
            bool endSavePoint = cb.IsSavePoint;
            if (startSavePoint != endSavePoint) NotifySavePoint(endSavePoint);
        }
        finally
        {
            enteredModification--;
        }
        return newPos;
    }

    /// <summary>Redoes one step and returns the caret position after it, or -1.</summary>
    internal int Redo()
    {
        int newPos = -1;
        if (enteredModification != 0 || !cb.IsCollectingUndo || cb.ReadOnly) return newPos;
        enteredModification++;
        try
        {
            bool startSavePoint = cb.IsSavePoint;
            int steps = cb.StartRedo();
            for (int step = 0; step < steps; step++)
            {
                int prevLinesTotal = LinesTotal;
                var action = cb.GetRedoStep();
                if (action.Type == ActionType.Insert)
                    NotifyModified(ModificationFlags.BeforeInsert | ModificationFlags.Redo, action.Position, action.Length);
                else if (action.Type == ActionType.Remove)
                    NotifyModified(ModificationFlags.BeforeDelete | ModificationFlags.Redo, action.Position, action.Length);
                cb.PerformRedoStep();
                if (action.Type != ActionType.Container)
                {
                    ModifiedAt(action.Position);
                    newPos = action.Position;
                }

                var flags = ModificationFlags.Redo;
                if (action.Type == ActionType.Insert)
                {
                    newPos += action.Length;
                    flags |= ModificationFlags.InsertText;
                }
                else if (action.Type == ActionType.Remove)
                    flags |= ModificationFlags.DeleteText;
                if (steps > 1) flags |= ModificationFlags.MultiStepUndoRedo;
                if (step == steps - 1) flags |= ModificationFlags.LastStepInUndoRedo;
                if (action.Type != ActionType.Container)
                    NotifyModified(flags, action.Position, action.Length, LinesTotal - prevLinesTotal);
            }
            bool endSavePoint = cb.IsSavePoint;
            if (startSavePoint != endSavePoint) NotifySavePoint(endSavePoint);
        }
        finally
        {
            enteredModification--;
        }
        return newPos;
    }

    // Line ends

    /// <summary>Converts every CR, LF and CR LF in <paramref name="s"/> to <paramref name="eolMode"/>.</summary>
    internal static byte[] TransformLineEnds(ReadOnlySpan<byte> s, EndOfLine eolMode)
    {
        ReadOnlySpan<byte> eol = eolMode switch { EndOfLine.CrLf => "\r\n"u8, EndOfLine.Cr => "\r"u8, _ => "\n"u8 };
        var result = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] is (byte)'\r' or (byte)'\n')
            {
                foreach (byte b in eol) result.Add(b);
                if (s[i] == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
            }
            else
                result.Add(s[i]);
        }
        return [.. result];
    }

    /// <summary>Converts every line end in the document to <paramref name="eolMode"/> as one undo step.</summary>
    internal void ConvertLineEnds(EndOfLine eolMode)
    {
        BeginUndoAction();
        for (int pos = 0; pos < Length; pos++)
        {
            byte ch = cb.CharAt(pos);
            if (ch == '\r')
            {
                if (cb.CharAt(pos + 1) == '\n')
                {
                    if (eolMode == EndOfLine.Cr)
                        DeleteChars(pos + 1, 1); // Delete the LF.
                    else if (eolMode == EndOfLine.Lf)
                        DeleteChars(pos, 1); // Delete the CR.
                    else
                        pos++;
                }
                else if (eolMode == EndOfLine.CrLf)
                {
                    pos += InsertString(pos + 1, "\n"u8); // Insert LF.
                }
                else if (eolMode == EndOfLine.Lf)
                {
                    pos += InsertString(pos, "\n"u8);
                    DeleteChars(pos, 1);
                    pos--;
                }
            }
            else if (ch == '\n')
            {
                if (eolMode == EndOfLine.CrLf)
                    pos += InsertString(pos, "\r"u8);
                else if (eolMode == EndOfLine.Cr)
                {
                    pos += InsertString(pos, "\r"u8);
                    DeleteChars(pos, 1);
                    pos--;
                }
            }
        }
        EndUndoAction();
    }

    // Characters

    /// <summary>A decoded character and its width in bytes; invalid UTF-8 decodes as U+FFFD of width 1.</summary>
    internal readonly record struct CharacterExtracted(int Character, int WidthBytes);

    internal CharacterExtracted CharacterAfter(int position)
    {
        if (position >= Length || position < 0) return new CharacterExtracted(0, 0);
        byte lead = cb.CharAt(position);
        if (lead < 0x80) return new CharacterExtracted(lead, 1);
        Span<byte> bytes = stackalloc byte[4];
        int count = Math.Min(4, Length - position);
        cb.GetCharRange(bytes, position, count);
        return Rune.DecodeFromUtf8(bytes[..count], out var rune, out int consumed) == System.Buffers.OperationStatus.Done
            ? new CharacterExtracted(rune.Value, consumed)
            : new CharacterExtracted(Rune.ReplacementChar.Value, 1);
    }

    internal CharacterExtracted CharacterBefore(int position)
    {
        if (position <= 0 || position > Length) return new CharacterExtracted(0, 0);
        byte previous = cb.CharAt(position - 1);
        if (previous < 0x80) return new CharacterExtracted(previous, 1);
        Span<byte> bytes = stackalloc byte[4];
        int count = Math.Min(4, position);
        cb.GetCharRange(bytes, position - count, count);
        return Rune.DecodeLastFromUtf8(bytes[..count], out var rune, out int consumed) == System.Buffers.OperationStatus.Done
            ? new CharacterExtracted(rune.Value, consumed)
            : new CharacterExtracted(Rune.ReplacementChar.Value, 1);
    }

    /// <summary>Moves <paramref name="pos"/> out of the middle of a UTF-8 character (and, when
    /// <paramref name="checkLineEnd"/>, out of a CR LF pair) in direction <paramref name="moveDir"/>.</summary>
    internal int MovePositionOutsideChar(int pos, int moveDir, bool checkLineEnd = true)
    {
        if (pos <= 0) return 0;
        if (pos >= Length) return Length;
        if (checkLineEnd && IsCrLf(pos - 1)) return moveDir > 0 ? pos + 1 : pos - 1;
        byte ch = cb.CharAt(pos);
        if (ch is >= 0x80 and < 0xC0)
        {
            // A trail byte: find the start of the character it may belong to.
            for (int start = pos - 1; start >= Math.Max(0, pos - 3); start--)
            {
                if (cb.CharAt(start) is >= 0x80 and < 0xC0) continue;
                var ce = CharacterAfter(start);
                if (ce.WidthBytes > 1 && start + ce.WidthBytes > pos)
                    return moveDir > 0 ? start + ce.WidthBytes : start;
                break;
            }
        }
        return pos;
    }

    /// <summary>The next character boundary from <paramref name="pos"/> in direction <paramref name="moveDir"/>.</summary>
    internal int NextPosition(int pos, int moveDir)
    {
        int increment = moveDir > 0 ? 1 : -1;
        if (pos + increment <= 0) return 0;
        if (pos + increment >= Length) return Length;
        return increment > 0 ? pos + CharacterAfter(pos).WidthBytes : pos - CharacterBefore(pos).WidthBytes;
    }

    /// <summary>Moves by <paramref name="characterOffset"/> whole characters.</summary>
    internal int MovePositionByCharacters(int pos, int characterOffset)
    {
        int direction = characterOffset > 0 ? 1 : -1;
        for (int i = 0; i != characterOffset; i += direction)
        {
            int next = NextPosition(pos, direction);
            if (next == pos) break;
            pos = next;
        }
        return pos;
    }

    // Words

    internal CharacterClass WordCharacterClass(int ch)
    {
        if (ch < 0x80) return CharClass.GetClass((byte)ch);
        if (ch > 0x10FFFF) return CharacterClass.Space;
        return CharUnicodeInfo.GetUnicodeCategory(ch) switch
        {
            UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator => CharacterClass.NewLine,
            UnicodeCategory.SpaceSeparator or UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned => CharacterClass.Space,
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark => CharacterClass.Word,
            _ => CharacterClass.Punctuation,
        };
    }

    /// <summary>Extends from <paramref name="pos"/> over characters of the same class (or only word characters).</summary>
    internal int ExtendWordSelect(int pos, int delta, bool onlyWordCharacters = false)
    {
        var ccStart = CharacterClass.Word;
        if (delta < 0)
        {
            if (!onlyWordCharacters) ccStart = WordCharacterClass(CharacterBefore(pos).Character);
            while (pos > 0)
            {
                var ce = CharacterBefore(pos);
                if (WordCharacterClass(ce.Character) != ccStart) break;
                pos -= ce.WidthBytes;
            }
        }
        else
        {
            if (!onlyWordCharacters && pos < Length) ccStart = WordCharacterClass(CharacterAfter(pos).Character);
            while (pos < Length)
            {
                var ce = CharacterAfter(pos);
                if (WordCharacterClass(ce.Character) != ccStart) break;
                pos += ce.WidthBytes;
            }
        }
        return MovePositionOutsideChar(pos, delta, true);
    }

    internal int WordStartPosition(int pos, bool onlyWordCharacters) => ExtendWordSelect(pos, -1, onlyWordCharacters);

    internal int WordEndPosition(int pos, bool onlyWordCharacters) => ExtendWordSelect(pos, 1, onlyWordCharacters);

    /// <summary>Start of the next (or previous) word: Ctrl+Right/Left and DelWord targets.</summary>
    internal int NextWordStart(int pos, int delta)
    {
        if (delta < 0)
        {
            while (pos > 0)
            {
                var ce = CharacterBefore(pos);
                if (WordCharacterClass(ce.Character) != CharacterClass.Space) break;
                pos -= ce.WidthBytes;
            }
            if (pos > 0)
            {
                var ccStart = WordCharacterClass(CharacterBefore(pos).Character);
                while (pos > 0)
                {
                    var ce = CharacterBefore(pos);
                    if (WordCharacterClass(ce.Character) != ccStart) break;
                    pos -= ce.WidthBytes;
                }
            }
        }
        else
        {
            var ccStart = WordCharacterClass(CharacterAfter(pos).Character);
            while (pos < Length)
            {
                var ce = CharacterAfter(pos);
                if (WordCharacterClass(ce.Character) != ccStart) break;
                pos += ce.WidthBytes;
            }
            while (pos < Length)
            {
                var ce = CharacterAfter(pos);
                if (WordCharacterClass(ce.Character) != CharacterClass.Space) break;
                pos += ce.WidthBytes;
            }
        }
        return pos;
    }

    /// <summary>End of the next (or previous) word.</summary>
    internal int NextWordEnd(int pos, int delta)
    {
        if (delta < 0)
        {
            if (pos > 0)
            {
                var ccStart = WordCharacterClass(CharacterBefore(pos).Character);
                if (ccStart != CharacterClass.Space)
                {
                    while (pos > 0)
                    {
                        var ce = CharacterBefore(pos);
                        if (WordCharacterClass(ce.Character) != ccStart) break;
                        pos -= ce.WidthBytes;
                    }
                }
                while (pos > 0)
                {
                    var ce = CharacterBefore(pos);
                    if (WordCharacterClass(ce.Character) != CharacterClass.Space) break;
                    pos -= ce.WidthBytes;
                }
            }
        }
        else
        {
            while (pos < Length)
            {
                var ce = CharacterAfter(pos);
                if (WordCharacterClass(ce.Character) != CharacterClass.Space) break;
                pos += ce.WidthBytes;
            }
            if (pos < Length)
            {
                var ccStart = WordCharacterClass(CharacterAfter(pos).Character);
                while (pos < Length)
                {
                    var ce = CharacterAfter(pos);
                    if (WordCharacterClass(ce.Character) != ccStart) break;
                    pos += ce.WidthBytes;
                }
            }
        }
        return pos;
    }

    private static bool IsWordEdge(CharacterClass cc, CharacterClass ccNext) =>
        cc != ccNext && cc is CharacterClass.Word or CharacterClass.Punctuation;

    internal bool IsWordStartAt(int pos)
    {
        if (pos >= Length) return false;
        if (pos < 0) return true;
        var cePos = CharacterAfter(pos);
        // At the start of the document, treat as if there is a space before.
        int prev = pos > 0 ? CharacterBefore(pos).Character : ' ';
        return IsWordEdge(WordCharacterClass(cePos.Character), WordCharacterClass(prev));
    }

    internal bool IsWordEndAt(int pos)
    {
        if (pos <= 0) return false;
        if (pos > Length) return true;
        // At the end of the document, treat as if there is a space after.
        int after = pos < Length ? CharacterAfter(pos).Character : ' ';
        return IsWordEdge(WordCharacterClass(CharacterBefore(pos).Character), WordCharacterClass(after));
    }

    internal bool IsWordAt(int start, int end) => start < end && IsWordStartAt(start) && IsWordEndAt(end);

    // Styling

    internal int EndStyled => endStyled;

    internal void StartStyling(int position) => endStyled = Math.Clamp(position, 0, Length);

    /// <summary>Styles <paramref name="length"/> bytes from the styling position; notifies only a change.</summary>
    internal bool SetStyleFor(int length, byte style)
    {
        if (enteredStyling != 0) return false;
        enteredStyling++;
        int prevEndStyled = endStyled;
        length = Math.Clamp(length, 0, Length - endStyled);
        if (cb.SetStyleFor(endStyled, length, style))
            NotifyModified(ModificationFlags.ChangeStyle | ModificationFlags.User, prevEndStyled, length);
        endStyled += length;
        enteredStyling--;
        return true;
    }

    /// <summary>Styles bytes from the styling position; notifies the minimal changed span.</summary>
    internal bool SetStyles(ReadOnlySpan<byte> styles)
    {
        if (enteredStyling != 0) return false;
        enteredStyling++;
        bool didChange = false;
        int startMod = 0, endMod = 0;
        for (int i = 0; i < styles.Length && endStyled < Length; i++, endStyled++)
        {
            if (cb.SetStyleAt(endStyled, styles[i]))
            {
                if (!didChange) startMod = endStyled;
                didChange = true;
                endMod = endStyled;
            }
        }
        if (didChange)
            NotifyModified(ModificationFlags.ChangeStyle | ModificationFlags.User, startMod, endMod - startMod + 1);
        enteredStyling--;
        return true;
    }

    // Decorations (indicators)

    internal void SetIndicatorCurrent(int indicator) => Decorations.CurrentIndicator = indicator;

    internal void DecorationFillRange(int position, int value, int fillLength)
    {
        position = Math.Clamp(position, 0, Length);
        fillLength = Math.Clamp(fillLength, 0, Length - position);
        var result = Decorations.FillRange(position, value, fillLength);
        if (result.Changed)
            NotifyModified(ModificationFlags.ChangeIndicator | ModificationFlags.User, result.Position, result.FillLength);
    }

    // Margin text

    internal void MarginSetText(int line, string? text)
    {
        MarginText.SetText(line, text);
        NotifyModified(ModificationFlags.ChangeMargin, LineStart(line), 0);
    }

    internal void MarginSetStyle(int line, int style)
    {
        MarginText.SetStyle(line, style);
        NotifyModified(ModificationFlags.ChangeMargin, LineStart(line), 0);
    }

    internal void MarginClearAll()
    {
        int maxEditorLine = LinesTotal;
        for (int line = 0; line < maxEditorLine; line++)
        {
            if (MarginText.Text(line) is not null) MarginSetText(line, null);
        }
        MarginText.ClearAll();
    }
}