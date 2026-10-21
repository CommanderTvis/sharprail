namespace SharpRail.Scintilla.Core;

/// <summary>
/// Document line to display line mapping with per-line heights (wrapped sub-lines); no folding, so every line is
/// visible and expanded (upstream src/ContractionState.cxx). Stays one-to-one until a height other than 1 is set.
/// </summary>
internal sealed class ContractionState
{
    private RunStyles<int>? heights;
    private Partitioning? displayLines;
    private int linesInDocument = 1;

    private bool OneToOne => heights is null;

    private void EnsureData()
    {
        if (!OneToOne) return;
        heights = new RunStyles<int>();
        displayLines = new Partitioning(4);
        int lines = linesInDocument;
        InsertLines(0, lines);
    }

    private void InsertLine(int lineDoc)
    {
        if (OneToOne)
        {
            linesInDocument++;
            return;
        }
        heights!.InsertSpace(lineDoc, 1);
        heights.SetValueAt(lineDoc, 1);
        int lineDisplay = DisplayFromDoc(lineDoc);
        displayLines!.InsertPartition(lineDoc, lineDisplay);
        displayLines.InsertText(lineDoc, 1);
    }

    private void DeleteLine(int lineDoc)
    {
        if (OneToOne)
        {
            linesInDocument--;
            return;
        }
        displayLines!.InsertText(lineDoc, -heights!.ValueAt(lineDoc));
        displayLines.RemovePartition(lineDoc);
        heights.DeleteRange(lineDoc, 1);
    }

    internal void Clear()
    {
        heights = null;
        displayLines = null;
        linesInDocument = 1;
    }

    internal int LinesInDoc => OneToOne ? linesInDocument : displayLines!.Partitions - 1;

    internal int LinesDisplayed => OneToOne ? linesInDocument : displayLines!.PositionFromPartition(LinesInDoc);

    internal int DisplayFromDoc(int lineDoc)
    {
        if (OneToOne) return lineDoc <= linesInDocument ? lineDoc : linesInDocument;
        if (lineDoc > displayLines!.Partitions) lineDoc = displayLines.Partitions;
        return displayLines.PositionFromPartition(lineDoc);
    }

    internal int DisplayFromDocSub(int lineDoc, int lineSub) =>
        DisplayFromDoc(lineDoc) + Math.Min(lineSub, GetHeight(lineDoc) - 1);

    internal int DisplayLastFromDoc(int lineDoc) => DisplayFromDoc(lineDoc) + GetHeight(lineDoc) - 1;

    internal int DocFromDisplay(int lineDisplay)
    {
        if (OneToOne) return lineDisplay;
        if (lineDisplay < 0) return 0;
        if (lineDisplay > LinesDisplayed) return displayLines!.PartitionFromPosition(LinesDisplayed);
        return displayLines!.PartitionFromPosition(lineDisplay);
    }

    internal void InsertLines(int lineDoc, int lineCount)
    {
        if (OneToOne)
        {
            linesInDocument += lineCount;
            return;
        }
        for (int l = 0; l < lineCount; l++) InsertLine(lineDoc + l);
    }

    internal void DeleteLines(int lineDoc, int lineCount)
    {
        if (OneToOne)
        {
            linesInDocument -= lineCount;
            return;
        }
        for (int l = 0; l < lineCount; l++) DeleteLine(lineDoc);
    }

    internal int GetHeight(int lineDoc) => OneToOne ? 1 : heights!.ValueAt(lineDoc);

    /// <summary>Sets the display height of a document line; returns whether it changed.</summary>
    internal bool SetHeight(int lineDoc, int height)
    {
        if (OneToOne && height == 1) return false;
        if (lineDoc >= LinesInDoc) return false;
        EnsureData();
        int current = GetHeight(lineDoc);
        if (current == height) return false;
        displayLines!.InsertText(lineDoc, height - current);
        heights!.SetValueAt(lineDoc, height);
        return true;
    }

    /// <summary>Throws when display and document lines disagree (upstream CHECK_CORRECTNESS).</summary>
    internal void Check()
    {
        for (int lineDoc = 0; lineDoc < LinesInDoc; lineDoc++)
        {
            int height = DisplayFromDoc(lineDoc + 1) - DisplayFromDoc(lineDoc);
            if (height != GetHeight(lineDoc))
                throw new InvalidOperationException($"ContractionState: line {lineDoc} height {height} != {GetHeight(lineDoc)}.");
        }
    }
}