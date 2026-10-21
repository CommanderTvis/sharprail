namespace SharpRail.Scintilla.Core;

/// <summary>Result of <see cref="RunStyles{T}.FillRange"/>: whether anything changed and the range actually filled.</summary>
internal readonly record struct FillResult(bool Changed, int Position, int FillLength);

/// <summary>Run-length values over positions (upstream src/RunStyles.h/.cxx). <c>default(T)</c> is the empty value.</summary>
internal sealed class RunStyles<T> where T : struct, IEquatable<T>
{
    private Partitioning starts = new();
    private SplitVector<T> styles = new();

    internal RunStyles() => styles.InsertValue(0, 2, default);

    private static bool IsSet(T value) => !value.Equals(default);

    private int RunFromPosition(int position)
    {
        int run = starts.PartitionFromPosition(position);
        while (run > 0 && position == starts.PositionFromPartition(run - 1)) run--;
        return run;
    }

    private int SplitRun(int pos)
    {
        int run = RunFromPosition(pos);
        int posRun = starts.PositionFromPartition(run);
        if (posRun < pos)
        {
            T runStyle = ValueAt(pos);
            run++;
            starts.InsertPartition(run, pos);
            styles.InsertValue(run, 1, runStyle);
        }
        return run;
    }

    private void RemoveRun(int run)
    {
        starts.RemovePartition(run);
        styles.DeleteRange(run, 1);
    }

    private void RemoveRunIfEmpty(int run)
    {
        if (run < starts.Partitions && starts.Partitions > 1
            && starts.PositionFromPartition(run) == starts.PositionFromPartition(run + 1))
            RemoveRun(run);
    }

    private void RemoveRunIfSameAsPrevious(int run)
    {
        if (run > 0 && run < starts.Partitions && styles.ValueAt(run - 1).Equals(styles.ValueAt(run)))
            RemoveRun(run);
    }

    internal int Length => starts.PositionFromPartition(starts.Partitions);

    internal int Runs => starts.Partitions;

    internal T ValueAt(int position) => styles.ValueAt(starts.PartitionFromPosition(position));

    internal int FindNextChange(int position, int end)
    {
        int run = starts.PartitionFromPosition(position);
        if (run < starts.Partitions)
        {
            int runChange = starts.PositionFromPartition(run);
            if (runChange > position) return runChange;
            int nextChange = starts.PositionFromPartition(run + 1);
            if (nextChange > position) return nextChange;
            if (position < end) return end;
        }
        return end + 1;
    }

    internal int StartRun(int position) => starts.PositionFromPartition(starts.PartitionFromPosition(position));

    internal int EndRun(int position) => starts.PositionFromPartition(starts.PartitionFromPosition(position) + 1);

    internal FillResult FillRange(int position, T value, int fillLength)
    {
        var resultNoChange = new FillResult(false, position, fillLength);
        if (fillLength <= 0) return resultNoChange;
        int end = position + fillLength;
        if (end > Length) return resultNoChange;
        int runEnd = RunFromPosition(end);
        T valueCurrent = styles.ValueAt(runEnd);
        if (valueCurrent.Equals(value))
        {
            end = starts.PositionFromPartition(runEnd);
            if (position >= end) return resultNoChange;
            fillLength = end - position;
        }
        else
        {
            int startRun = starts.PositionFromPartition(runEnd);
            if (position > startRun)
            {
                int endRun = starts.PositionFromPartition(runEnd + 1);
                if (end < endRun)
                {
                    starts.InsertPartitions(runEnd + 1, [position, end]);
                    styles.Insert(runEnd + 1, value);
                    styles.Insert(runEnd + 2, valueCurrent);
                    return new FillResult(true, position, fillLength);
                }
            }
            runEnd = SplitRun(end);
        }
        int runStart = RunFromPosition(position);
        if (styles.ValueAt(runStart).Equals(value))
        {
            runStart++;
            position = starts.PositionFromPartition(runStart);
            fillLength = end - position;
        }
        else if (starts.PositionFromPartition(runStart) < position)
        {
            runStart = SplitRun(position);
            runEnd++;
        }
        if (runStart < runEnd)
        {
            var result = new FillResult(true, position, fillLength);
            styles.SetValueAt(runStart, value);
            for (int run = runStart + 1; run < runEnd; run++) RemoveRun(runStart + 1);
            runEnd = RunFromPosition(end);
            RemoveRunIfSameAsPrevious(runEnd);
            RemoveRunIfSameAsPrevious(runStart);
            runEnd = RunFromPosition(end);
            RemoveRunIfEmpty(runEnd);
            return result;
        }
        return resultNoChange;
    }

    internal void SetValueAt(int position, T value) => FillRange(position, value, 1);

    internal void InsertSpace(int position, int insertLength)
    {
        int runStart = RunFromPosition(position);
        if (starts.PositionFromPartition(runStart) == position)
        {
            T runStyle = ValueAt(position);
            if (runStart == 0)
            {
                if (IsSet(runStyle))
                {
                    styles.SetValueAt(0, default);
                    starts.InsertPartition(1, 0);
                    styles.InsertValue(1, 1, runStyle);
                    starts.InsertText(0, insertLength);
                }
                else
                {
                    starts.InsertText(runStart, insertLength);
                }
            }
            else
            {
                starts.InsertText(IsSet(runStyle) ? runStart - 1 : runStart, insertLength);
            }
        }
        else
        {
            starts.InsertText(runStart, insertLength);
        }
    }

    internal void DeleteAll()
    {
        starts = new Partitioning();
        styles = new SplitVector<T>();
        styles.InsertValue(0, 2, default);
    }

    internal void DeleteRange(int position, int deleteLength)
    {
        int end = position + deleteLength;
        int runStart = RunFromPosition(position);
        int runEnd = RunFromPosition(end);
        if (runStart == runEnd)
        {
            starts.InsertText(runStart, -deleteLength);
            RemoveRunIfEmpty(runStart);
        }
        else
        {
            runStart = SplitRun(position);
            runEnd = SplitRun(end);
            starts.InsertText(runStart, -deleteLength);
            for (int run = runStart; run < runEnd; run++) RemoveRun(runStart);
            RemoveRunIfEmpty(runStart);
            RemoveRunIfSameAsPrevious(runStart);
        }
    }

    internal bool AllSame()
    {
        for (int run = 1; run < starts.Partitions; run++)
            if (!styles.ValueAt(run).Equals(styles.ValueAt(run - 1))) return false;
        return true;
    }

    internal bool AllSameAs(T value) => AllSame() && styles.ValueAt(0).Equals(value);

    internal int Find(T value, int start)
    {
        if (start < Length)
        {
            int run = start != 0 ? RunFromPosition(start) : 0;
            if (styles.ValueAt(run).Equals(value)) return start;
            run++;
            while (run < starts.Partitions)
            {
                if (styles.ValueAt(run).Equals(value)) return starts.PositionFromPartition(run);
                run++;
            }
        }
        return -1;
    }

    /// <summary>Throws when the runs are inconsistent (upstream Check).</summary>
    internal void Check()
    {
        if (Length < 0) throw new InvalidOperationException("RunStyles: Length can not be negative.");
        if (starts.Partitions < 1) throw new InvalidOperationException("RunStyles: Must always have 1 or more partitions.");
        if (starts.Partitions != styles.Length - 1)
            throw new InvalidOperationException("RunStyles: Partitions and styles different lengths.");
        int start = 0;
        while (start < Length)
        {
            int end = EndRun(start);
            if (start >= end) throw new InvalidOperationException("RunStyles: Partition is 0 length.");
            start = end;
        }
        if (IsSet(styles.ValueAt(styles.Length - 1)))
            throw new InvalidOperationException("RunStyles: Unused style at end changed.");
        for (int j = 1; j < styles.Length - 1; j++)
            if (styles.ValueAt(j).Equals(styles.ValueAt(j - 1)))
                throw new InvalidOperationException("RunStyles: Style of a partition same as previous.");
    }
}