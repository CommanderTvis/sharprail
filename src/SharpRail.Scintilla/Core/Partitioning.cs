namespace SharpRail.Scintilla.Core;

/// <summary>
/// Partition starts with a lazily applied step (upstream src/Partitioning.h). Holds line starts: partitions after
/// <c>stepPartition</c> are stored without the pending <c>stepLength</c>, which is applied on demand.
/// </summary>
internal sealed class Partitioning
{
    private int stepPartition;
    private int stepLength;
    private readonly SplitVector<int> body;

    internal Partitioning(int growSize = 8)
    {
        body = new SplitVector<int>(growSize);
        body.Insert(0, 0);
        body.Insert(1, 0);
    }

    internal int Partitions => body.Length - 1;

    internal int Length => PositionFromPartition(Partitions);

    private void RangeAddDelta(int start, int end, int delta)
    {
        for (int i = start; i < end; i++) body[i] += delta;
    }

    private void ApplyStep(int partitionUpTo)
    {
        if (stepLength != 0) RangeAddDelta(stepPartition + 1, partitionUpTo + 1, stepLength);
        stepPartition = partitionUpTo;
        if (stepPartition >= Partitions)
        {
            stepPartition = Partitions;
            stepLength = 0;
        }
    }

    private void BackStep(int partitionDownTo)
    {
        if (stepLength != 0) RangeAddDelta(partitionDownTo + 1, stepPartition + 1, -stepLength);
        stepPartition = partitionDownTo;
    }

    internal void ReAllocate(int newSize) => body.ReAllocate(newSize + 1);

    internal void InsertPartition(int partition, int pos)
    {
        if (stepPartition < partition) ApplyStep(partition);
        body.Insert(partition, pos);
        stepPartition++;
    }

    internal void InsertPartitions(int partition, ReadOnlySpan<int> positions)
    {
        if (stepPartition < partition) ApplyStep(partition);
        body.InsertFromSpan(partition, positions);
        stepPartition += positions.Length;
    }

    internal void SetPartitionStartPosition(int partition, int pos)
    {
        ApplyStep(partition + 1);
        if (partition < 0 || partition >= body.Length) return;
        body.SetValueAt(partition, pos);
    }

    internal void InsertText(int partitionInsert, int delta)
    {
        const int closePortion = 10;
        if (stepLength != 0)
        {
            if (partitionInsert >= stepPartition)
            {
                ApplyStep(partitionInsert);
                stepLength += delta;
            }
            else if (partitionInsert >= stepPartition - body.Length / closePortion)
            {
                BackStep(partitionInsert);
                stepLength += delta;
            }
            else
            {
                ApplyStep(Partitions);
                stepPartition = partitionInsert;
                stepLength = delta;
            }
        }
        else
        {
            stepPartition = partitionInsert;
            stepLength = delta;
        }
    }

    internal void RemovePartition(int partition)
    {
        if (partition > stepPartition) ApplyStep(partition);
        stepPartition--;
        body.Delete(partition);
    }

    internal int PositionFromPartition(int partition)
    {
        if (partition < 0 || partition >= body.Length) return 0;
        int pos = body[partition];
        if (partition > stepPartition) pos += stepLength;
        return pos;
    }

    internal int PartitionFromPosition(int pos)
    {
        if (body.Length <= 1) return 0;
        if (pos >= PositionFromPartition(Partitions)) return Partitions - 1;
        int lower = 0;
        int upper = Partitions;
        do
        {
            int middle = (upper + lower + 1) / 2;
            int posMiddle = body[middle];
            if (middle > stepPartition) posMiddle += stepLength;
            if (pos < posMiddle) upper = middle - 1;
            else lower = middle;
        } while (lower < upper);
        return lower;
    }

    internal void DeleteAll()
    {
        body.DeleteAll();
        stepPartition = 0;
        stepLength = 0;
        body.Insert(0, 0);
        body.Insert(1, 0);
    }

    /// <summary>Throws when the partitioning is inconsistent (upstream CHECK_CORRECTNESS).</summary>
    internal void Check()
    {
        if (Length < 0) throw new InvalidOperationException("Partitioning: Length can not be negative.");
        if (Partitions < 1) throw new InvalidOperationException("Partitioning: Must always have 1 or more partitions.");
        if (Length == 0)
        {
            if (PositionFromPartition(0) != 0 || PositionFromPartition(1) != 0)
                throw new InvalidOperationException("Partitioning: Invalid empty partitioning.");
            return;
        }
        for (int i = 0; i < Partitions; i++)
        {
            int pos = PositionFromPartition(i);
            int posNext = PositionFromPartition(i + 1);
            if (pos > posNext) throw new InvalidOperationException("Partitioning: Negative partition.");
            if (pos == posNext) throw new InvalidOperationException("Partitioning: Empty partition.");
        }
    }
}