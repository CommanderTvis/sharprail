using SharpRail.Scintilla.Core;

namespace SharpRail.Checks.ScintillaCore;

/// <summary>Port of Scintilla test/unit/testSplitVector.cxx (copy/move compile checks have no C# counterpart).</summary>
internal static class SplitVectorChecks
{
    private static readonly int[] TestArray = [3, 4, 5, 6];

    internal static void Run()
    {
        IsEmptyInitially();
        InsertOne();
        Insertion();
        InsertionString();
        InsertionPattern();
        EnsureLength();
        InsertFromArray();
        InsertEmpty();
        SetValue();
        Indexing();
        Fill();
        DeleteOne();
        DeleteRange();
        DeleteAll();
        GetRange();
        GetRangeOverGap();
        ReplaceUp();
        ReplaceDown();
        BufferPointer();
        DeleteBackAndForth();
        GrowSize();
        OutsideBounds();
    }

    private static void ExpectValues(SplitVector<int> sv, int[] expected, string what)
    {
        Expect.Equal(expected.Length, sv.Length, $"{what} length");
        for (int i = 0; i < expected.Length; i++) Expect.Equal(expected[i], sv.ValueAt(i), $"{what} [{i}]");
    }

    private static void IsEmptyInitially() => Expect.Equal(0, new SplitVector<int>().Length, "IsEmptyInitially");

    private static void InsertOne()
    {
        var sv = new SplitVector<int>();
        sv.InsertValue(0, 10, 0);
        sv.Insert(5, 3);
        Expect.Equal(11, sv.Length, "InsertOne length");
        for (int i = 0; i < sv.Length; i++) Expect.Equal(i == 5 ? 3 : 0, sv.ValueAt(i), $"InsertOne [{i}]");
    }

    private static void Insertion()
    {
        var sv = new SplitVector<int>();
        sv.InsertValue(0, 10, 0);
        Expect.Equal(10, sv.Length, "Insertion length");
        for (int i = 0; i < sv.Length; i++) Expect.Equal(0, sv.ValueAt(i), $"Insertion [{i}]");
    }

    private static void InsertionString()
    {
        var sa = new SplitVector<string>();
        void Check()
        {
            for (int i = 0; i < sa.Length; i++) Expect.True(!string.IsNullOrEmpty(sa[i]), $"InsertionString [{i}] empty");
        }
        foreach (var (pos, s) in new[] { (0, "Alpha"), (0, "Beta"), (0, "Cat"), (1, "Dog"), (0, "Elephant"), (1, "Fox"), (0, "Grass"), (1, "Hat") })
        {
            sa.Insert(pos, s);
            Check();
        }
        sa.Delete(4);
        Check();
        foreach (var (pos, s) in new[] { (0, "Indigo"), (1, "Jackal"), (0, "Kanga"), (1, "Lion"), (0, "Mango"), (1, "Neon") })
        {
            sa.Insert(pos, s);
            Check();
        }
    }

    private static void InsertionPattern()
    {
        var sv = new SplitVector<int>();
        sv.Insert(0, 1);
        sv.Insert(0, 2);
        sv.Insert(0, 3);
        sv.Insert(1, 4);
        sv.Insert(0, 5);
        sv.Insert(1, 6);
        sv.Insert(0, 7);
        sv.Insert(1, 8);
        ExpectValues(sv, [7, 8, 5, 6, 3, 4, 2, 1], "InsertionPattern 1");
        sv.Delete(4);
        ExpectValues(sv, [7, 8, 5, 6, 4, 2, 1], "InsertionPattern 2");
        sv.Insert(0, 9);
        sv.Insert(1, 0xa);
        sv.Insert(0, 0xb);
        sv.Insert(1, 0xc);
        sv.Insert(0, 0xd);
        sv.Insert(1, 0xe);
        ExpectValues(sv, [0xd, 0xe, 0xb, 0xc, 9, 0xa, 7, 8, 5, 6, 4, 2, 1], "InsertionPattern 3");
    }

    private static void EnsureLength()
    {
        var sv = new SplitVector<int>();
        sv.EnsureLength(4);
        ExpectValues(sv, [0, 0, 0, 0], "EnsureLength");
    }

    private static void InsertFromArray()
    {
        var sv = new SplitVector<int>();
        sv.InsertFromSpan(0, TestArray);
        ExpectValues(sv, [3, 4, 5, 6], "InsertFromArray");
    }

    private static void InsertEmpty()
    {
        var sv = new SplitVector<int>();
        sv.InsertEmpty(0, 0);
        Expect.Equal(0, sv.Length, "InsertEmpty 0");
        var pi = sv.InsertEmpty(0, 2);
        ExpectValues(sv, [0, 0], "InsertEmpty 1");
        pi[0] = 4;
        pi[1] = 5;
        ExpectValues(sv, [4, 5], "InsertEmpty 2");
        pi = sv.InsertEmpty(1, 2);
        pi[0] = 6;
        pi[1] = 7;
        ExpectValues(sv, [4, 6, 7, 5], "InsertEmpty 3");
    }

    private static void SetValue()
    {
        var sv = new SplitVector<int>();
        sv.InsertValue(0, 10, 0);
        sv.SetValueAt(5, 3);
        Expect.Equal(10, sv.Length, "SetValue length");
        for (int i = 0; i < sv.Length; i++) Expect.Equal(i == 5 ? 3 : 0, sv.ValueAt(i), $"SetValue [{i}]");
        sv.InsertValue(7, 1, 17);
        Expect.Equal(17, sv.ValueAt(7), "SetValue 7");
        Expect.Equal(0, sv.ValueAt(8), "SetValue 8");
        sv.SetValueAt(8, 19);
        Expect.Equal(19, sv.ValueAt(8), "SetValue 8 set");
    }

    private static void Indexing()
    {
        var sv = new SplitVector<int>();
        sv.InsertValue(0, 10, 0);
        sv.SetValueAt(5, 3);
        Expect.Equal(10, sv.Length, "Indexing length");
        for (int i = 0; i < sv.Length; i++) Expect.Equal(i == 5 ? 3 : 0, sv[i], $"Indexing [{i}]");
    }

    private static void Fill()
    {
        var sv = new SplitVector<int>();
        sv.InsertValue(0, 10, 0);
        Expect.Equal(10, sv.Length, "Fill length");
        sv.InsertValue(7, 1, 1);
        Expect.Equal(11, sv.Length, "Fill length 2");
        for (int i = 0; i < sv.Length; i++) Expect.Equal(i == 7 ? 1 : 0, sv.ValueAt(i), $"Fill [{i}]");
    }

    private static void DeleteOne()
    {
        var sv = new SplitVector<int>();
        sv.InsertFromSpan(0, TestArray);
        sv.Delete(2);
        Expect.Equal(3, sv.Length, "DeleteOne length");
        Expect.Equal(3, sv[0], "DeleteOne 0");
        Expect.Equal(4, sv[1], "DeleteOne 1");
        Expect.Equal(6, sv[2], "DeleteOne 2");
    }

    private static void DeleteRange()
    {
        var sv = new SplitVector<int>();
        sv.InsertValue(0, 10, 0);
        sv.InsertValue(7, 1, 1);
        Expect.Equal(11, sv.Length, "DeleteRange length");
        sv.DeleteRange(2, 3);
        Expect.Equal(8, sv.Length, "DeleteRange length 2");
        for (int i = 0; i < sv.Length; i++) Expect.Equal(i == 4 ? 1 : 0, sv.ValueAt(i), $"DeleteRange [{i}]");
    }

    private static void DeleteAll()
    {
        var sv = new SplitVector<int>();
        sv.InsertValue(0, 10, 0);
        sv.InsertValue(7, 1, 1);
        sv.DeleteRange(2, 3);
        sv.DeleteAll();
        Expect.Equal(0, sv.Length, "DeleteAll");
    }

    private static void GetRange()
    {
        var sv = new SplitVector<int>();
        sv.InsertValue(0, 10, 0);
        sv.InsertValue(7, 1, 1);
        var retrieve = new int[11];
        sv.GetRange(retrieve, 0, 11);
        for (int i = 0; i < sv.Length; i++) Expect.Equal(i == 7 ? 1 : 0, retrieve[i], $"GetRange [{i}]");
    }

    private static void GetRangeOverGap()
    {
        var sv = new SplitVector<int>();
        sv.InsertFromSpan(0, TestArray);
        Expect.Equal(4, sv.Length, "GetRangeOverGap length");
        var retrieve = new int[4];
        sv.GetRange(retrieve, 0, 4);
        for (int i = 0; i < sv.Length; i++) Expect.Equal(i + 3, retrieve[i], $"GetRangeOverGap [{i}]");
    }

    private static void ReplaceUp()
    {
        const int testLength = 105;
        var sv = new SplitVector<int>();
        sv.EnsureLength(testLength);
        for (int i = 0; i < testLength; i++) sv.SetValueAt(i, i + 2);
        Expect.Equal(testLength, sv.Length, "ReplaceUp length");
        for (int i = 0; i < sv.Length; i++)
        {
            sv.InsertValue(i, 1, i + 9);
            sv.Delete(i + 1);
        }
        for (int i = 0; i < sv.Length; i++) Expect.Equal(i + 9, sv.ValueAt(i), $"ReplaceUp [{i}]");
    }

    private static void ReplaceDown()
    {
        const int testLength = 24;
        var sv = new SplitVector<int>();
        sv.EnsureLength(testLength);
        for (int i = 0; i < testLength; i++) sv.SetValueAt(i, i + 12);
        Expect.Equal(testLength, sv.Length, "ReplaceDown length");
        for (int i = sv.Length - 1; i >= 0; i--)
        {
            sv.InsertValue(i, 1, i + 5);
            sv.Delete(i + 1);
        }
        for (int i = 0; i < sv.Length; i++) Expect.Equal(i + 5, sv.ValueAt(i), $"ReplaceDown [{i}]");
    }

    private static void BufferPointer()
    {
        var sv = new SplitVector<int>();
        sv.InsertFromSpan(0, TestArray);
        sv.Insert(0, 99);
        Expect.Equal(1, sv.GapPosition, "BufferPointer gap");
        Expect.Equal(5, sv.Length, "BufferPointer length");
        var retrieve = sv.BufferPointer();
        for (int i = 1; i < sv.Length; i++) Expect.Equal(i + 3 - 1, retrieve[i], $"BufferPointer [{i}]");
        Expect.Equal(5, sv.Length, "BufferPointer length after");
        Expect.Equal(5, sv.GapPosition, "BufferPointer gap after");
    }

    private static void DeleteBackAndForth()
    {
        var sv = new SplitVector<int>();
        sv.InsertValue(0, 10, 87);
        for (int i = 0; i < 10; i += 2)
        {
            int len = 10 - i;
            Expect.Equal(len, sv.Length, "DeleteBackAndForth length");
            for (int j = 0; j < sv.Length; j++) Expect.Equal(87, sv.ValueAt(j), $"DeleteBackAndForth [{j}]");
            sv.Delete(len - 1);
            sv.Delete(0);
        }
    }

    private static void GrowSize()
    {
        var sv = new SplitVector<int>();
        sv.SetGrowSize(5);
        Expect.Equal(5, sv.GetGrowSize(), "GrowSize");
    }

    private static void OutsideBounds()
    {
        var sv = new SplitVector<int>();
        sv.InsertValue(0, 10, 87);
        Expect.Equal(0, sv.ValueAt(-1), "OutsideBounds -1");
        Expect.Equal(0, sv.ValueAt(10), "OutsideBounds 10");
    }
}