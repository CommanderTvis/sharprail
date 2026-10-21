namespace SharpRail.Scintilla.Core;

/// <summary>Gap buffer (upstream src/SplitVector.h): efficient insertion and deletion near a movable gap.</summary>
/// <remarks>Out-of-range reads through <see cref="ValueAt"/> return <c>default</c>; out-of-range edits are ignored, as upstream.</remarks>
internal sealed class SplitVector<T>
{
    private const int GrowthPortion = 6;
    private const int InitialGrowSize = 8;

    private T[] body = [];
    private int lengthBody;
    private int part1Length;
    private int gapLength;
    private int growSize;

    internal SplitVector(int growSize = InitialGrowSize) => this.growSize = growSize;

    internal int Length => lengthBody;

    internal int GapPosition => part1Length;

    internal int GetGrowSize() => growSize;

    internal void SetGrowSize(int size) => growSize = size;

    private void GapTo(int position)
    {
        if (position == part1Length) return;
        if (gapLength > 0)
        {
            if (position < part1Length)
                Array.Copy(body, position, body, position + gapLength, part1Length - position);
            else
                Array.Copy(body, part1Length + gapLength, body, part1Length, position - part1Length);
        }
        part1Length = position;
    }

    private void RoomFor(int insertionLength)
    {
        if (gapLength >= insertionLength) return;
        while (growSize < body.Length / GrowthPortion) growSize *= 2;
        ReAllocate(body.Length + insertionLength + growSize);
    }

    private void Init()
    {
        body = [];
        lengthBody = 0;
        part1Length = 0;
        gapLength = 0;
        growSize = InitialGrowSize;
    }

    internal void ReAllocate(int newSize)
    {
        if (newSize <= body.Length) return;
        GapTo(lengthBody);
        gapLength += newSize - body.Length;
        Array.Resize(ref body, newSize);
    }

    internal T ValueAt(int position)
    {
        if (position < part1Length) return position < 0 ? default! : body[position];
        return position >= lengthBody ? default! : body[gapLength + position];
    }

    internal void SetValueAt(int position, T value)
    {
        if (position < 0 || position >= lengthBody) return;
        if (position < part1Length) body[position] = value;
        else body[gapLength + position] = value;
    }

    internal T this[int position]
    {
        get => position < part1Length ? body[position] : body[gapLength + position];
        set
        {
            if (position < part1Length) body[position] = value;
            else body[gapLength + position] = value;
        }
    }

    internal void Insert(int position, T value)
    {
        if (position < 0 || position > lengthBody) return;
        RoomFor(1);
        GapTo(position);
        body[part1Length] = value;
        lengthBody++;
        part1Length++;
        gapLength--;
    }

    internal void InsertValue(int position, int insertLength, T value)
    {
        if (insertLength <= 0 || position < 0 || position > lengthBody) return;
        RoomFor(insertLength);
        GapTo(position);
        body.AsSpan(part1Length, insertLength).Fill(value);
        lengthBody += insertLength;
        part1Length += insertLength;
        gapLength -= insertLength;
    }

    /// <summary>Inserts <paramref name="insertLength"/> default values and returns them as a writable span.</summary>
    internal Span<T> InsertEmpty(int position, int insertLength)
    {
        if (insertLength <= 0 || position < 0 || position > lengthBody) return [];
        InsertValue(position, insertLength, default!);
        return body.AsSpan(position, insertLength);
    }

    internal void EnsureLength(int wantedLength)
    {
        if (lengthBody < wantedLength) InsertEmpty(lengthBody, wantedLength - lengthBody);
    }

    internal void InsertFromSpan(int positionToInsert, ReadOnlySpan<T> values)
    {
        int insertLength = values.Length;
        if (insertLength <= 0 || positionToInsert < 0 || positionToInsert > lengthBody) return;
        RoomFor(insertLength);
        GapTo(positionToInsert);
        values.CopyTo(body.AsSpan(part1Length, insertLength));
        lengthBody += insertLength;
        part1Length += insertLength;
        gapLength -= insertLength;
    }

    internal void Delete(int position) => DeleteRange(position, 1);

    internal void DeleteRange(int position, int deleteLength)
    {
        if (position < 0 || position + deleteLength > lengthBody) return;
        if (position == 0 && deleteLength == lengthBody)
        {
            Init();
        }
        else if (deleteLength > 0)
        {
            GapTo(position);
            if (ClearOnDelete) Array.Clear(body, part1Length + gapLength, deleteLength);
            lengthBody -= deleteLength;
            gapLength += deleteLength;
        }
    }

    private static readonly bool ClearOnDelete =
        System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    internal void DeleteAll() => DeleteRange(0, lengthBody);

    internal void GetRange(Span<T> buffer, int position, int retrieveLength)
    {
        int range1Length = 0;
        if (position < part1Length) range1Length = Math.Min(retrieveLength, part1Length - position);
        body.AsSpan(position, range1Length).CopyTo(buffer);
        int position2 = position + range1Length + gapLength;
        body.AsSpan(position2, retrieveLength - range1Length).CopyTo(buffer[range1Length..]);
    }

    /// <summary>Moves the gap to the end and returns the contiguous contents.</summary>
    internal Span<T> BufferPointer()
    {
        RoomFor(1);
        GapTo(lengthBody);
        body[lengthBody] = default!;
        return body.AsSpan(0, lengthBody);
    }

    /// <summary>Returns a contiguous span over a range, moving the gap only when the range straddles it.</summary>
    internal Span<T> RangePointer(int position, int rangeLength)
    {
        if (position < part1Length)
        {
            if (position + rangeLength > part1Length)
            {
                GapTo(position);
                return body.AsSpan(position + gapLength, rangeLength);
            }
            return body.AsSpan(position, rangeLength);
        }
        return body.AsSpan(position + gapLength, rangeLength);
    }
}