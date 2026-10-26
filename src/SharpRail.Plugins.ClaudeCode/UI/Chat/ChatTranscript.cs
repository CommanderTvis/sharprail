using Avalonia.Collections;

namespace SharpRail.Plugins.ClaudeCode.UI.Chat;

/// <summary>
/// Consecutive routine steps (reads, searches, thinking) folded into one row, as upstream's activity group: not a wire item, so it
/// exists only here and keeps the id of its first step while it grows.
/// </summary>
internal sealed record ChatActivityItem(string Id, long Seq, IReadOnlyList<ChatItem> Steps) : ChatItem(Id, Seq);

/// <summary>
/// What the user has picked so far for a question card, per question: the chosen option labels and the typed "Other" text. Immutable, so a
/// change is a new draft and a new row revision; it lives in the transcript, not in the card, so it survives the row's replacement.
/// </summary>
internal sealed record QuestionDraft(IReadOnlyList<IReadOnlyList<string>> Chosen, IReadOnlyList<string> Other)
{
    public static QuestionDraft For(ChatQuestionItem item) => new([.. item.Questions.Select(_ => (IReadOnlyList<string>)[])], [.. item.Questions.Select(_ => "")]);

    /// <summary>Picking an option: it replaces the choice of a single-select question and flips in a multi-select one; a typed "Other" yields to a single pick.</summary>
    public QuestionDraft Pick(int question, string label, bool multi)
    {
        var chosen = Chosen.ToList();
        var now = chosen[question].ToList();
        if (multi) { if (!now.Remove(label)) now.Add(label); }
        else now = now.Contains(label) ? [] : [label];
        chosen[question] = now;
        return new(chosen, multi || now.Count == 0 ? Other : [.. Other.Select((text, at) => at == question ? "" : text)]);
    }

    public QuestionDraft WithOther(int question, bool multi, string text)
    {
        var other = Other.ToList();
        other[question] = text;
        var chosen = Chosen.ToList();
        if (!multi && text.Length > 0) chosen[question] = [];
        return new(chosen, other);
    }

    public bool Complete => Chosen.Select((picked, at) => picked.Count > 0 || Other[at].Trim().Length > 0).All(done => done);

    /// <summary>The answer to each question by question text: a multi-select answer is its labels joined by ", " (the typed text last), "Other" is the typed text.</summary>
    public Dictionary<string, string> Answers(ChatQuestionItem item) =>
        item.Questions.Select((question, at) => (question.Question, Text: string.Join(", ", Chosen[at].Concat(Other[at].Trim().Length > 0 ? [Other[at].Trim()] : []))))
            .ToDictionary(pair => pair.Question, pair => pair.Text);
}

/// <summary>One visible line of the transcript. A row is replaced, never mutated, so a changed row is a new reference and its revision keys the measured-height cache.</summary>
internal sealed record ChatRow(ChatItem Item, int Depth, int Revision, bool Expanded, string? Full, QuestionDraft? Draft = null)
{
    public string Id => Item.Id;
}

/// <summary>
/// The conversation as the view shows it: items by id, flattened into rows with subagent children nested under their
/// tool call. Every change touches only the rows it concerns, so the list control sees a replace or an insert, never a rebuild.
/// </summary>
internal sealed class ChatTranscript
{
    private readonly Dictionary<string, ChatItem> items = [];
    private readonly Dictionary<string, List<string>> children = [];
    private readonly Dictionary<string, ChatRow> rowsById = [];
    private readonly Dictionary<string, string> full = [];
    // Ids whose expansion differs from their default; it outlives the item's replacement and a reset.
    private readonly HashSet<string> flipped = [];
    private int revision;
    private readonly Dictionary<string, List<string>> groupSteps = [];
    private readonly Dictionary<string, QuestionDraft> drafts = [];
    private readonly HashSet<string> pending = [];

    private static bool IsPending(ChatItem item) => item is ChatPermissionItem { Decision: ChatPermissionDecision.Pending } or ChatQuestionItem { Decision: ChatPermissionDecision.Pending };

    /// <summary>The oldest request still waiting for an answer, which the panel above the composer shows; the others wait their turn.</summary>
    public ChatItem? FirstPending() => pending.Select(id => items.GetValueOrDefault(id)).OfType<ChatItem>().OrderBy(item => item.Seq).FirstOrDefault();

    public int PendingCount => pending.Count;
    // The activity group each folded step belongs to.
    private readonly Dictionary<string, string> groupOf = [];

    public AvaloniaList<ChatRow> Rows { get; } = [];

    public ChatHeader? Header { get; private set; }

    public int Count => items.Count;

    public event Action<ChatHeader>? HeaderChanged;

    /// <summary>Raised after an item was added or replaced, for a view of part of the thread (a subagent's dialog) to follow it.</summary>
    public event Action<ChatItem>? ItemChanged;

    public ChatItem? Item(string id) => items.GetValueOrDefault(id);

    public ChatRow? Row(string id) => rowsById.GetValueOrDefault(id);

    /// <summary>The user messages waiting for the turn to end, read from the tail of the list.</summary>
    public IReadOnlyList<ChatUserItem> QueuedMessages()
    {
        var queued = new List<ChatUserItem>();
        for (var index = Rows.Count - 1; index >= 0 && index >= Rows.Count - 200; index--)
            if (Rows[index].Item is ChatUserItem { Queued: true } message) queued.Insert(0, message);
        return queued;
    }

    /// <summary>A copy of the references to every item held, for a search to order and scan off the UI thread.</summary>
    public IReadOnlyList<ChatItem> AllItems() => [.. items.Values];

    /// <summary>The row that shows an item: its own, else the row of the activity group or ancestor that holds it, unfolding ancestors that hide it.</summary>
    public ChatRow? RowFor(string id)
    {
        if (rowsById.TryGetValue(id, out var own)) return own;
        if (groupOf.TryGetValue(id, out var group) && rowsById.TryGetValue(group, out var grouped)) return grouped;
        return Reveal(id) is { } shown ? rowsById.GetValueOrDefault(shown) : null;
    }

    /// <summary>Whether an item sits somewhere under a tool call, through any depth of nesting.</summary>
    public bool Under(ChatItem item, string ancestor)
    {
        for (var parent = item.ParentId; parent is not null; parent = items.GetValueOrDefault(parent)?.ParentId)
            if (parent == ancestor) return true;
        return false;
    }

    public IReadOnlyList<ChatItem> Children(string id) => children.TryGetValue(id, out var ids) ? [.. ids.Select(child => items[child])] : [];

    /// <summary>Tool calls that start a subagent keep their nested items open until the user folds them.</summary>
    public static bool Nests(ChatItem item) => item is ChatToolItem { Name: "Task" or "Agent" };

    public static bool DefaultExpanded(ChatItem item) => Nests(item) || item is ChatPermissionItem { Decision: ChatPermissionDecision.Pending };

    public bool IsExpanded(ChatItem item) => DefaultExpanded(item) != flipped.Contains(item.Id);

    public void Apply(ChatPush push)
    {
        switch (push)
        {
            case ChatResetPush: Reset(); break;
            case ChatHeaderPush { Header: var header }: Header = header; HeaderChanged?.Invoke(header); break;
            case ChatItemPush { Item: var item }: Upsert(item); ItemChanged?.Invoke(item); break;
            case ChatRemovePush { ItemId: var gone }: Remove(gone); break;
        }
    }

    public void SetHeader(ChatHeader header)
    {
        Header = header;
        HeaderChanged?.Invoke(header);
    }

    public void Reset()
    {
        items.Clear(); children.Clear(); rowsById.Clear(); full.Clear(); groupOf.Clear(); groupSteps.Clear(); drafts.Clear(); pending.Clear();
        Rows.Clear();
    }

    private ChatRow Make(ChatItem item, int depth) =>
        new(item, depth, ++revision, IsExpanded(item), full.GetValueOrDefault(item.Id), drafts.GetValueOrDefault(item.Id));

    private int IndexOf(string id)
    {
        if (!rowsById.TryGetValue(id, out var row)) return -1;
        for (var index = Rows.Count - 1; index >= 0; index--)
            if (ReferenceEquals(Rows[index], row)) return index;
        return -1;
    }

    public void Upsert(ChatItem item)
    {
        // A result is obsolete on the wire and never a row: the extension draws nothing for one.
        if (item is ChatResultItem) return;
        if (IsPending(item)) pending.Add(item.Id); else pending.Remove(item.Id);
        // Subagent traffic is kept for the thread dialog and never drawn in the main list: the Agent card is its only trace there.
        if (item.ParentId is { } owner)
        {
            items[item.Id] = item;
            if (!children.TryGetValue(owner, out var thread)) children[owner] = thread = [];
            if (!thread.Contains(item.Id)) thread.Add(item.Id);
            return;
        }
        var known = items.ContainsKey(item.Id);
        items[item.Id] = item;
        if (known && groupOf.TryGetValue(item.Id, out var grouped))
        {
            Regroup(grouped);
            return;
        }
        if (known)
        {
            if (IndexOf(item.Id) is var index and >= 0)
            {
                var next = Make(item, Rows[index].Depth);
                Rows[index] = next;
                rowsById[item.Id] = next;
                // A permission answer folds the card to its decision.
                if (item is ChatPermissionItem) flipped.Remove(item.Id);
            }
            return;
        }
        if (Routine(item) && FoldIntoActivity(item)) return;
        var row = Make(item, 0);
        rowsById[item.Id] = row;
        Rows.Add(row);
    }

    private static readonly HashSet<string> RoutineTools = ["Read", "Grep", "Glob", "LS", "WebFetch", "WebSearch", "NotebookRead"];

    /// <summary>A step that belongs in an activity group rather than a card of its own.</summary>
    public static bool Routine(ChatItem item) => item is ChatToolItem { ParentId: null } tool && RoutineTools.Contains(tool.Name) || item is ChatThinkingItem { ParentId: null };

    // A second routine step beside the last row turns the pair into a group; later ones join it.
    private bool FoldIntoActivity(ChatItem item)
    {
        if (Rows.Count == 0 || Rows[^1] is not { Depth: 0 } last) return false;
        string groupId;
        if (last.Item is ChatActivityItem existing) groupId = existing.Id;
        else if (Routine(last.Item))
        {
            groupId = "act:" + last.Id;
            groupSteps[groupId] = [last.Id];
            groupOf[last.Id] = groupId;
            rowsById.Remove(last.Id);
        }
        else return false;
        groupSteps[groupId].Add(item.Id);
        groupOf[item.Id] = groupId;
        Regroup(groupId);
        return true;
    }

    // Rebuilds a group's item from its steps and replaces its row, or puts the row where the first step stood.
    private void Regroup(string groupId)
    {
        var steps = groupSteps[groupId].Select(id => items[id]).ToList();
        var group = new ChatActivityItem(groupId, steps[0].Seq, steps);
        items[groupId] = group;
        var row = Make(group, 0);
        if (rowsById.TryGetValue(groupId, out var current))
        {
            var at = IndexOf(groupId);
            if (at >= 0) Rows[at] = row;
        }
        else
        {
            var first = Rows.Count - 1;
            while (first >= 0 && !(Rows[first].Item.Id == steps[0].Id)) first--;
            if (first >= 0) Rows[first] = row; else Rows.Add(row);
        }
        rowsById[groupId] = row;
    }

    /// <summary>Flips an item between its default and its other state, for rows that fold, such as a notice or a long prompt.</summary>
    public void Toggle(string id)
    {
        if (items.GetValueOrDefault(id) is not { } item) return;
        if (!flipped.Remove(id)) flipped.Add(id);
        var index = IndexOf(id);
        if (index < 0) return;
        var row = Make(item, Rows[index].Depth);
        Rows[index] = row;
        rowsById[id] = row;
    }

    /// <summary>The draft answers of a question card, started on first use.</summary>
    public QuestionDraft DraftOf(ChatQuestionItem item) => drafts.GetValueOrDefault(item.Id) ?? QuestionDraft.For(item);

    public void SetDraft(ChatQuestionItem item, QuestionDraft draft)
    {
        // A pending question draws nothing in the list, so a pick replaces no row: only the panel above the composer shows it.
        drafts[item.Id] = draft;
    }

    /// <summary>Drops an item (a queued message that was cancelled) from the list, its group, its thread and every piece of state kept for it.</summary>
    public void Remove(string id)
    {
        if (!items.ContainsKey(id)) return;
        if (groupOf.Remove(id, out var group))
        {
            groupSteps[group].Remove(id);
            items.Remove(id);
            if (groupSteps[group].Count >= 2) { Regroup(group); return; }
            // A group left with one step is that step's own row again; with none, no row.
            var at = IndexOf(group);
            rowsById.Remove(group); items.Remove(group);
            var left = groupSteps[group].FirstOrDefault();
            groupSteps.Remove(group);
            if (left is null) { if (at >= 0) Rows.RemoveAt(at); return; }
            groupOf.Remove(left);
            var single = Make(items[left], 0);
            rowsById[left] = single;
            if (at >= 0) Rows[at] = single;
            return;
        }
        var index = IndexOf(id);
        if (index >= 0)
        {
            var depth = Rows[index].Depth;
            var end = index + 1;
            while (end < Rows.Count && Rows[end].Depth > depth) end++;
            for (var at = end - 1; at >= index; at--) { rowsById.Remove(Rows[at].Id); items.Remove(Rows[at].Id); Rows.RemoveAt(at); }
        }
        if (items.Remove(id) && items.GetValueOrDefault(id) is null) rowsById.Remove(id);
        foreach (var siblings in children.Values) siblings.Remove(id);
        children.Remove(id);
        full.Remove(id); drafts.Remove(id); flipped.Remove(id); pending.Remove(id);
    }

    /// <summary>Keeps a tool's whole output once fetched, so the card shows it without a second request.</summary>
    public void SetFull(string id, string text)
    {
        full[id] = text;
        if (items.GetValueOrDefault(id) is { } item && IndexOf(id) is var index and >= 0)
        {
            var row = Make(item, Rows[index].Depth);
            Rows[index] = row;
            rowsById[id] = row;
        }
    }

    public bool HasFull(string id) => full.ContainsKey(id);

    /// <summary>The id of the row to scroll to for a tool call, expanding the ancestors that hide it.</summary>
    public string? Reveal(string? toolUseId)
    {
        if (toolUseId is null || !items.TryGetValue(toolUseId, out var item)) return null;
        var parent = item.ParentId;
        while (parent is not null && items.TryGetValue(parent, out var up))
        {
            if (!IsExpanded(up)) Toggle(up.Id);
            parent = up.ParentId;
        }
        return toolUseId;
    }

    public int IndexOfRow(string id) => IndexOf(id);
}