using System.Runtime.InteropServices;

namespace SharpRail.Scintilla.Core;

/// <summary>UTF-8 bytes, style bytes, line starts, read-only flag and undo (upstream src/CellBuffer.h/.cxx).</summary>
/// <remarks>Lines end at CR, LF or CR LF only; Unicode line ends, per-line UTF-16/UTF-32 indices and change
/// history are not ported. Per-line data (margin text) follows line insertion and removal like upstream's
/// LineVector.</remarks>
internal sealed class CellBuffer
{
    private readonly SplitVector<byte> substance = new();
    private readonly SplitVector<byte> style = new();
    private readonly Partitioning lineStarts = new();
    private readonly UndoHistory uh = new();
    private readonly LineAnnotation? perLine;

    internal CellBuffer(LineAnnotation? perLine = null) => this.perLine = perLine;

    internal bool ReadOnly { get; set; }

    internal bool IsCollectingUndo { get; private set; } = true;

    internal int Length => substance.Length;

    internal int Lines => lineStarts.Partitions;

    internal byte CharAt(int position) => substance.ValueAt(position);

    internal byte StyleAt(int position) => style.ValueAt(position);

    internal void GetCharRange(Span<byte> buffer, int position, int length)
    {
        if (length <= 0) return;
        if (position < 0 || position + length > substance.Length) throw new ArgumentOutOfRangeException(nameof(position));
        substance.GetRange(buffer, position, length);
    }

    internal void GetStyleRange(Span<byte> buffer, int position, int length)
    {
        if (length <= 0) return;
        if (position < 0 || position + length > style.Length) throw new ArgumentOutOfRangeException(nameof(position));
        style.GetRange(buffer, position, length);
    }

    /// <summary>All text bytes, for tests and whole-document reads.</summary>
    internal byte[] ToArray()
    {
        var bytes = new byte[Length];
        GetCharRange(bytes, 0, Length);
        return bytes;
    }

    internal int LineStart(int line)
    {
        if (line < 0) return 0;
        if (line >= Lines) return Length;
        return lineStarts.PositionFromPartition(line);
    }

    internal int LineFromPosition(int position) => lineStarts.PartitionFromPosition(position);

    /// <summary>Position of the end of <paramref name="line"/> before its CR, LF or CR LF; -1 before the start.</summary>
    internal int LineEnd(int line)
    {
        if (line < 0) return -1;
        if (line >= Lines - 1) return Length;
        int position = LineStart(line + 1) - 1; // Back over CR or LF.
        if (position > LineStart(line) && CharAt(position - 1) == '\r') position--;
        return position;
    }

    internal bool SetStyleAt(int position, byte styleValue)
    {
        if (style.ValueAt(position) == styleValue) return false;
        style.SetValueAt(position, styleValue);
        return true;
    }

    internal bool SetStyleFor(int position, int length, byte styleValue)
    {
        bool changed = false;
        for (int i = 0; i < length; i++)
        {
            if (style.ValueAt(position + i) != styleValue)
            {
                style.SetValueAt(position + i, styleValue);
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Inserts <paramref name="s"/> unless read-only; returns the bytes recorded for the change.</summary>
    internal byte[] InsertString(int position, ReadOnlySpan<byte> s, out bool startSequence)
    {
        startSequence = false;
        if (ReadOnly) return s.ToArray();
        byte[]? data = IsCollectingUndo ? uh.AppendAction(ActionType.Insert, position, s, out startSequence) : null;
        BasicInsertString(position, s);
        return data ?? s.ToArray();
    }

    /// <summary>Inserts <paramref name="s"/> unless read-only, without copying it when undo is not collected.</summary>
    internal void InsertString(int position, ReadOnlySpan<byte> s)
    {
        if (ReadOnly) return;
        if (IsCollectingUndo) uh.AppendAction(ActionType.Insert, position, s, out _);
        BasicInsertString(position, s);
    }

    /// <summary>Deletes unless read-only; returns the deleted bytes (empty when read-only).</summary>
    internal byte[] DeleteChars(int position, int deleteLength, out bool startSequence)
    {
        startSequence = false;
        if (ReadOnly || deleteLength <= 0) return [];
        var deleted = new byte[deleteLength];
        GetCharRange(deleted, position, deleteLength);
        if (IsCollectingUndo) uh.AppendAction(ActionType.Remove, position, deleted, out startSequence);
        BasicDeleteChars(position, deleteLength);
        return deleted;
    }

    private void InsertLine(int line, int position, bool lineStart)
    {
        lineStarts.InsertPartition(line, position);
        if (perLine is null) return;
        if (line > 0 && lineStart) line--;
        perLine.InsertLine(line);
    }

    private void InsertLines(int line, List<int> positions, bool lineStart)
    {
        if (positions.Count == 0) return;
        lineStarts.InsertPartitions(line, CollectionsMarshal.AsSpan(positions));
        if (perLine is null) return;
        if (line > 0 && lineStart) line--;
        perLine.InsertLines(line, positions.Count);
    }

    private void RemoveLine(int line)
    {
        lineStarts.RemovePartition(line);
        perLine?.RemoveLine(line);
    }

    /// <summary>SharpRail extension: inserted bytes take the neighbouring style instead of 0, so container-styled text keeps its token colour until restyled.</summary>
    internal bool InheritInsertionStyle { get; set; }

    private byte InsertionStyle(int position)
    {
        if (position > 0 && substance.ValueAt(position - 1) is not ((byte)'\r' or (byte)'\n')) return style.ValueAt(position - 1);
        if (position < substance.Length && substance.ValueAt(position) is not ((byte)'\r' or (byte)'\n')) return style.ValueAt(position);
        return position > 0 ? style.ValueAt(position - 1) : (byte)0;
    }

    private void BasicInsertString(int position, ReadOnlySpan<byte> s)
    {
        if (s.Length == 0) return;
        byte chAfter = substance.ValueAt(position);
        int lineInsert = LineFromPosition(position) + 1;
        byte inherited = InheritInsertionStyle ? InsertionStyle(position) : (byte)0;
        substance.InsertFromSpan(position, s);
        style.InsertValue(position, s.Length, inherited);

        bool atLineStart = LineStart(lineInsert - 1) == position;
        // Point all the lines after the insertion point further along in the buffer.
        lineStarts.InsertText(lineInsert - 1, s.Length);
        byte chPrev = substance.ValueAt(position - 1);
        if (chPrev == '\r' && chAfter == '\n')
        {
            // Splitting up a CR LF pair at position.
            InsertLine(lineInsert, position, false);
            lineInsert++;
        }

        int i = 0;
        if (chPrev == '\r' && s[0] == '\n')
        {
            // Patch up what was the end of line.
            i = 1;
            lineStarts.SetPartitionStartPosition(lineInsert - 1, position + 1);
        }
        var positions = new List<int>();
        while (i < s.Length)
        {
            int found = s[i..].IndexOfAny((byte)'\r', (byte)'\n');
            if (found < 0) break;
            i += found;
            if (s[i] == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
            positions.Add(position + i + 1);
            i++;
        }
        InsertLines(lineInsert, positions, atLineStart);
        lineInsert += positions.Count;

        // Joining two lines where the insertion ends with CR and the following text starts with LF.
        if (chAfter == '\n' && s[^1] == '\r') RemoveLine(lineInsert - 1);
    }

    private void BasicDeleteChars(int position, int deleteLength)
    {
        if (deleteLength == 0) return;
        if (position == 0 && deleteLength == substance.Length)
        {
            // Whole buffer: reinitialise the line data rather than deleting each line.
            lineStarts.DeleteAll();
            perLine?.ClearAll();
        }
        else
        {
            // Fix up line positions before the deletion, looking at the text to find removed lines.
            int lineRemove = LineFromPosition(position) + 1;
            lineStarts.InsertText(lineRemove - 1, -deleteLength);
            byte chPrev = substance.ValueAt(position - 1);
            byte chBefore = chPrev;
            byte chNext = substance.ValueAt(position);
            bool ignoreNL = false;
            if (chPrev == '\r' && chNext == '\n')
            {
                // Move back one.
                lineStarts.SetPartitionStartPosition(lineRemove, position);
                lineRemove++;
                ignoreNL = true; // First LF is not a real deletion.
            }

            byte ch = chNext;
            for (int i = 0; i < deleteLength; i++)
            {
                chNext = substance.ValueAt(position + i + 1);
                if (ch == '\r')
                {
                    if (chNext != '\n') RemoveLine(lineRemove);
                }
                else if (ch == '\n')
                {
                    if (ignoreNL) ignoreNL = false; // Further LFs are real deletions.
                    else RemoveLine(lineRemove);
                }
                ch = chNext;
            }
            // The deletion may leave CR next to LF, or remove one of a CR LF pair.
            byte chAfter = substance.ValueAt(position + deleteLength);
            if (chBefore == '\r' && chAfter == '\n')
            {
                RemoveLine(lineRemove - 1);
                lineStarts.SetPartitionStartPosition(lineRemove - 1, position + 1);
            }
        }
        substance.DeleteRange(position, deleteLength);
        style.DeleteRange(position, deleteLength);
    }

    // Undo

    internal void SetUndoCollection(bool collectUndo) => IsCollectingUndo = collectUndo;

    internal void BeginUndoAction(bool mayCoalesce = false) => uh.BeginUndoAction(mayCoalesce);

    internal void EndUndoAction() => uh.EndUndoAction();

    internal int UndoSequenceDepth => uh.UndoSequenceDepth;

    internal void AddUndoAction(int token, bool mayCoalesce) =>
        uh.AppendAction(ActionType.Container, token, [], out _, mayCoalesce);

    internal void DeleteUndoHistory() => uh.DeleteUndoHistory();

    internal void SetSavePoint() => uh.SetSavePoint();

    internal bool IsSavePoint => uh.IsSavePoint;

    internal bool CanUndo => uh.CanUndo;

    internal bool CanRedo => uh.CanRedo;

    internal int StartUndo() => uh.StartUndo();

    internal UndoAction GetUndoStep() => uh.GetUndoStep();

    internal void PerformUndoStep()
    {
        var step = uh.GetUndoStep();
        if (step.Type == ActionType.Insert)
        {
            if (substance.Length < step.Length)
                throw new InvalidOperationException("Undo deletion must be less than document length.");
            BasicDeleteChars(step.Position, step.Length);
        }
        else if (step.Type == ActionType.Remove)
            BasicInsertString(step.Position, step.Data);
        uh.CompletedUndoStep();
    }

    internal int StartRedo() => uh.StartRedo();

    internal UndoAction GetRedoStep() => uh.GetRedoStep();

    internal void PerformRedoStep()
    {
        var step = uh.GetRedoStep();
        if (step.Type == ActionType.Insert)
            BasicInsertString(step.Position, step.Data);
        else if (step.Type == ActionType.Remove)
            BasicDeleteChars(step.Position, step.Length);
        uh.CompletedRedoStep();
    }
}