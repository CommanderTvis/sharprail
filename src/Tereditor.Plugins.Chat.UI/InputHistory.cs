namespace Tereditor.Plugins.Chat.UI;

/// <summary>What the box should do with an Up, Down or Esc press.</summary>
internal enum InputHistoryStep { None, CaretToStart, CaretToEnd, Show }

/// <summary>The box's answer to a key: nothing (the key stays plain caret movement), a caret jump, or new text with its selection.</summary>
internal readonly record struct InputHistoryMove(InputHistoryStep Step, string Text = "", int SelectionStart = 0, int SelectionEnd = 0);

/// <summary>Where a composer is in its input history.</summary>
internal enum InputHistoryState
{
    /// <summary>The box holds the user's own text; nothing is stashed.</summary>
    Idle,
    /// <summary>The box shows a recalled entry exactly as recalled; the draft typed before is stashed.</summary>
    Browsing,
    /// <summary>The user edited the recalled entry: it is the working text now and the stash is untouched.</summary>
    BrowsingEdited,
}

/// <summary>
/// The composer's input history as one explicit state machine, free of the box: Up recalls only from the first line and, on a draft or an edited
/// recalled entry, only after a press that first moved the caret to the start; Down recalls newer entries only from the last line; the draft
/// typed before browsing is stashed and put back (text and selection) by Down past the newest entry or by Esc; sending resets it.
/// </summary>
internal sealed class InputHistory
{
    private int at;
    private string? shown;
    private string stashText = "";
    private int stashStart, stashEnd;
    // Where the caret and selection were before the guard press moved them to the start: what a stashed draft comes back with.
    private (string Text, int Start, int End)? before;

    public InputHistoryState State { get; private set; }

    /// <summary>The stashed draft while browsing, else null.</summary>
    public string? Stash => State == InputHistoryState.Idle ? null : stashText;

    public InputHistoryMove Up(string text, int selectionStart, int selectionEnd, int caret, bool onFirstLine, IReadOnlyList<string> entries)
    {
        if (entries.Count == 0 || !onFirstLine) return default;
        if (State == InputHistoryState.Idle)
        {
            if (text.Length > 0 && caret > 0) { before = (text, selectionStart, selectionEnd); return new(InputHistoryStep.CaretToStart); }
            (stashText, stashStart, stashEnd) = before is { } kept && kept.Text == text ? (text, kept.Start, kept.End) : (text, selectionStart, selectionEnd);
            at = entries.Count - 1;
        }
        else
        {
            if (State == InputHistoryState.BrowsingEdited && caret > 0) return new(InputHistoryStep.CaretToStart);
            at = Math.Max(0, Math.Min(at, entries.Count - 1) - 1);
        }
        return Show(entries[at]);
    }

    public InputHistoryMove Down(string text, int caret, bool onLastLine, IReadOnlyList<string> entries)
    {
        if (State == InputHistoryState.Idle || !onLastLine) return default;
        if (State == InputHistoryState.BrowsingEdited && caret < text.Length) return new(InputHistoryStep.CaretToEnd);
        if (at + 1 < entries.Count) { at++; return Show(entries[at]); }
        return Restore();
    }

    public InputHistoryMove Escape() => State == InputHistoryState.Idle ? default : Restore();

    /// <summary>Tells the machine the box's text changed, whoever changed it: anything but the shown entry is an edit.</summary>
    public void TextChanged(string text)
    {
        if (State == InputHistoryState.Browsing && text != shown) State = InputHistoryState.BrowsingEdited;
    }

    /// <summary>A send (or a mode change) ends browsing and drops the stash.</summary>
    public void Reset()
    {
        State = InputHistoryState.Idle;
        shown = null;
        stashText = "";
        before = null;
    }

    private InputHistoryMove Show(string entry)
    {
        State = InputHistoryState.Browsing;
        shown = entry;
        before = null;
        return new(InputHistoryStep.Show, entry, entry.Length, entry.Length);
    }

    private InputHistoryMove Restore()
    {
        var move = new InputHistoryMove(InputHistoryStep.Show, stashText, Math.Min(stashStart, stashText.Length), Math.Min(stashEnd, stashText.Length));
        Reset();
        return move;
    }
}