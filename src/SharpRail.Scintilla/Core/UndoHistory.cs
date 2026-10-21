namespace SharpRail.Scintilla.Core;

/// <summary>Kind of an undo action (upstream ActionType).</summary>
internal enum ActionType { Insert, Remove, Container }

/// <summary>One undoable step. <see cref="Data"/> holds the inserted or removed bytes; a container action has none
/// and keeps its token in <see cref="Position"/>.</summary>
internal sealed class UndoAction(ActionType type, int position, byte[]? data, bool mayCoalesce)
{
    internal ActionType Type { get; } = type;
    internal int Position { get; } = position;
    internal byte[]? Data { get; } = data;
    internal int Length => Data?.Length ?? 0;
    internal bool MayCoalesce { get; set; } = mayCoalesce;
}

/// <summary>Undo actions with coalescing, save point and grouping (upstream src/UndoHistory.h/.cxx).</summary>
/// <remarks>A plain list of actions replaces upstream's scrap stack and scaled vectors. Tentative (IME) undo and
/// change-history detach points are not ported. Actions past <see cref="Current"/> are the redo stack and are
/// truncated by the next appended action.</remarks>
internal sealed class UndoHistory
{
    private readonly List<UndoAction> actions = [];
    private int currentAction;
    private int undoSequenceDepth;
    private int savePoint;

    internal int Actions => actions.Count;

    internal int Current => currentAction;

    internal int UndoSequenceDepth => undoSequenceDepth;

    /// <summary>Records an action; <paramref name="startSequence"/> is true when it starts a new undo step rather
    /// than coalescing into the previous one. Returns the stored copy of <paramref name="data"/>.</summary>
    internal byte[]? AppendAction(ActionType at, int position, ReadOnlySpan<byte> data, out bool startSequence,
        bool mayCoalesce = true)
    {
        if (currentAction < savePoint) savePoint = -1;
        if (undoSequenceDepth > 0) mayCoalesce = true;
        bool coalesce = true;
        if (currentAction >= 1)
        {
            int targetAct = currentAction - 1;
            if (undoSequenceDepth == 0)
            {
                // Container actions may forward the coalesce state of text actions.
                while (targetAct > 0 && actions[targetAct].Type == ActionType.Container && actions[targetAct].MayCoalesce)
                    targetAct--;
                var target = actions[targetAct];
                if (currentAction == savePoint)
                    coalesce = false;
                else if (!mayCoalesce || !target.MayCoalesce)
                    coalesce = false;
                else if (at == ActionType.Container || target.Type == ActionType.Container)
                {
                    // A coalescible container action.
                }
                else if (at != target.Type)
                    coalesce = false;
                else if (at == ActionType.Insert && position != target.Position + target.Length)
                    coalesce = false; // Insertions must be immediately after to coalesce.
                else if (at == ActionType.Remove)
                {
                    if (data.Length is 1 or 2)
                    {
                        // Backspace or Delete of one character coalesces; anything else does not.
                        if (position + data.Length != target.Position && position != target.Position)
                            coalesce = false;
                    }
                    else
                        coalesce = false;
                }
            }
            else if (!actions[targetAct].MayCoalesce)
                coalesce = false;
        }
        else
            coalesce = false;

        startSequence = !coalesce;
        if (currentAction > 0 && startSequence) actions[currentAction - 1].MayCoalesce = false;
        byte[]? stored = data.Length > 0 ? data.ToArray() : null;
        if (currentAction < actions.Count) actions.RemoveRange(currentAction, actions.Count - currentAction);
        actions.Add(new UndoAction(at, position, stored, mayCoalesce));
        currentAction++;
        return stored;
    }

    internal void BeginUndoAction(bool mayCoalesce = false)
    {
        if (undoSequenceDepth == 0 && currentAction > 0) actions[currentAction - 1].MayCoalesce = mayCoalesce;
        undoSequenceDepth++;
    }

    internal void EndUndoAction()
    {
        if (undoSequenceDepth == 0) return;
        undoSequenceDepth--;
        if (undoSequenceDepth == 0 && currentAction > 0) actions[currentAction - 1].MayCoalesce = false;
    }

    internal void DropUndoSequence() => undoSequenceDepth = 0;

    internal void DeleteUndoHistory()
    {
        actions.Clear();
        currentAction = 0;
        savePoint = 0;
    }

    internal void SetSavePoint() => savePoint = currentAction;

    internal bool IsSavePoint => savePoint == currentAction;

    internal bool BeforeSavePoint => savePoint < 0 || savePoint > currentAction;

    internal bool BeforeReachableSavePoint => savePoint > 0 && savePoint > currentAction;

    internal bool AfterSavePoint => savePoint >= 0 && savePoint <= currentAction;

    internal bool CanUndo => currentAction > 0 && actions.Count != 0;

    /// <summary>Number of actions in the undo step ending at the current action.</summary>
    internal int StartUndo()
    {
        if (currentAction == 0) return 0;
        int act = currentAction - 1;
        while (act > 0 && actions[act - 1].MayCoalesce) act--;
        return currentAction - act;
    }

    internal UndoAction GetUndoStep() => actions[currentAction - 1];

    internal void CompletedUndoStep() => currentAction--;

    internal bool CanRedo => actions.Count > currentAction;

    /// <summary>Number of actions in the redo step starting at the current action.</summary>
    internal int StartRedo()
    {
        if (currentAction >= actions.Count) return 0;
        int maxAction = actions.Count - 1;
        int act = currentAction;
        while (act <= maxAction && actions[act].MayCoalesce) act++;
        act = Math.Min(act, maxAction);
        return act - currentAction + 1;
    }

    internal UndoAction GetRedoStep() => actions[currentAction];

    internal void CompletedRedoStep() => currentAction++;
}