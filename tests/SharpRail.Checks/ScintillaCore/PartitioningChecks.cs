using SharpRail.Scintilla.Core;

namespace SharpRail.Checks.ScintillaCore;

/// <summary>Port of Scintilla test/unit/testPartitioning.cxx. InsertMultipleWithCast folds into InsertPartitions,
/// since positions are int throughout.</summary>
internal static class PartitioningChecks
{
    internal static void Run()
    {
        var part = new Partitioning();
        Expect.Equal(1, part.Partitions, "IsEmptyInitially partitions");
        Expect.Equal(0, part.PositionFromPartition(part.Partitions), "IsEmptyInitially end");
        Expect.Equal(0, part.PartitionFromPosition(0), "IsEmptyInitially partition of 0");

        part = new Partitioning();
        part.InsertText(0, 1);
        Expect.Equal(1, part.Partitions, "SimpleInsert partitions");
        Expect.Equal(1, part.PositionFromPartition(part.Partitions), "SimpleInsert end");

        part = new Partitioning();
        part.InsertText(0, 2);
        part.InsertPartition(1, 1);
        Positions(part, [0, 1, 2], "TwoPartitions");

        part = new Partitioning();
        part.InsertText(0, 3);
        part.InsertPartition(1, 2);
        part.SetPartitionStartPosition(1, 1);
        Positions(part, [0, 1, 3], "MoveStart");
        part.Check();

        part = new Partitioning();
        part.InsertText(0, 3);
        part.InsertPartition(1, 2);
        part.InsertText(0, 3);
        part.InsertText(1, 2);
        Positions(part, [0, 5, 8], "InsertAgain");
        part.Check();

        part = new Partitioning();
        part.InsertText(0, 10);
        part.InsertPartitions(1, [2, 5, 7]);
        Positions(part, [0, 2, 5, 7, 10], "InsertMultiple");
        part.Check();

        part = new Partitioning();
        part.InsertText(0, 9);
        Expect.Equal(1, part.Partitions, "InsertMultipleWithCast partitions");
        part.InsertPartitions(1, [2, 4, 6, 8]);
        Positions(part, [0, 2, 4, 6, 8, 9], "InsertMultipleWithCast");
        part.Check();

        part = new Partitioning();
        part.InsertText(0, 3);
        part.InsertPartition(1, 2);
        part.InsertText(1, 2);
        part.InsertText(0, 3);
        Positions(part, [0, 5, 8], "InsertReversed");
        part.Check();

        part = new Partitioning();
        part.InsertText(0, 3);
        part.InsertPartition(1, 2);
        part.SetPartitionStartPosition(1, 1);
        Positions(part, [0, 1, 3], "InverseSearch");
        Expect.Equal(0, part.PartitionFromPosition(0), "InverseSearch 0");
        Expect.Equal(1, part.PartitionFromPosition(1), "InverseSearch 1");
        Expect.Equal(1, part.PartitionFromPosition(2), "InverseSearch 2");
        Expect.Equal(1, part.PartitionFromPosition(3), "InverseSearch 3");
        part.Check();

        part = new Partitioning();
        part.InsertText(0, 2);
        part.InsertPartition(1, 1);
        part.RemovePartition(1);
        Positions(part, [0, 2], "DeletePartition");
        part.Check();

        part = new Partitioning();
        part.InsertText(0, 3);
        part.InsertPartition(1, 2);
        part.SetPartitionStartPosition(1, 1);
        part.DeleteAll();
        Expect.Equal(1, part.Partitions, "DeleteAll partitions");
        Expect.Equal(0, part.PositionFromPartition(part.Partitions), "DeleteAll end");

        part = new Partitioning();
        part.InsertText(0, 10);
        part.InsertPartition(1, 3);
        part.InsertPartition(2, 6);
        part.InsertPartition(3, 9);
        part.InsertText(2, 4);
        part.InsertText(1, 2);
        part.InsertText(0, 3);
        Positions(part, [0, 6, 11, 18, 19], "TestBackwards");
        part.Check();

        TestMany();
    }

    private static void Positions(Partitioning part, int[] expected, string what)
    {
        Expect.Equal(expected.Length - 1, part.Partitions, $"{what} partitions");
        for (int i = 0; i < expected.Length; i++)
            Expect.Equal(expected[i], part.PositionFromPartition(i), $"{what} position of {i}");
    }

    private static void TestMany()
    {
        var part = new Partitioning();
        part.InsertText(0, 42);
        for (int i = 0; i < 20; i++) part.InsertPartition(i + 1, (i + 1) * 2);
        for (int i = 20; i > 0; i--) part.InsertText(i, 2);
        Expect.Equal(21, part.Partitions, "TestMany partitions");
        for (int i = 1; i < 20; i++)
        {
            Expect.Equal(i * 4 - 2, part.PositionFromPartition(i), $"TestMany position of {i}");
            Expect.Equal(i, part.PartitionFromPosition(i * 4 - 2), $"TestMany partition of {i * 4 - 2}");
        }
        part.InsertText(19, 2);
        Expect.Equal(3, part.PartitionFromPosition(10), "TestMany partition of 10");
        part.InsertText(0, 2);
        part.InsertText(0, -2);
        part.RemovePartition(1);
        Expect.Equal(0, part.PositionFromPartition(0), "TestMany 0");
        Expect.Equal(6, part.PositionFromPartition(1), "TestMany 1");
        Expect.Equal(10, part.PositionFromPartition(2), "TestMany 2");
        part.RemovePartition(10);
        Expect.Equal(46, part.PositionFromPartition(10), "TestMany 10");
        Expect.Equal(10, part.PartitionFromPosition(46), "TestMany partition of 46");
        Expect.Equal(50, part.PositionFromPartition(11), "TestMany 11");
        Expect.Equal(11, part.PartitionFromPosition(50), "TestMany partition of 50");
        part.Check();
    }
}