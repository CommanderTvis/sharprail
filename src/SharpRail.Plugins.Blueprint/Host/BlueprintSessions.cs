using System.Text.RegularExpressions;

namespace SharpRail.Plugins.Blueprint.Host;

internal sealed record PersistedBlueprint(BlueprintSource Source, BlueprintAgentId AgentId,
    BlueprintAuthor? Author = null, bool Closed = false);

internal sealed class BlueprintSessions(
    Func<Dictionary<string, PersistedBlueprint>> read,
    Action<Dictionary<string, PersistedBlueprint>> write,
    Action<BlueprintChangedPayload> publish)
{
    private static readonly BlueprintDoc Empty = new([], "");
    private readonly Dictionary<string, Session> sessions = [];

    private sealed class Session(string workspaceId, string path, BlueprintSource source, BlueprintAgentId agentId,
        BlueprintAuthor? author = null)
    {
        public string WorkspaceId { get; } = workspaceId;
        public string Path { get; } = path;
        public BlueprintSource Source { get; } = source;
        public BlueprintAgentId AgentId { get; } = agentId;
        public BlueprintAuthor? Author { get; set; } = author;
        public BlueprintDoc? Seen { get; set; }
        public IReadOnlyList<BlueprintEdit> PendingEdits { get; set; } = [];
    }

    private void Remember(Session session)
    {
        var records = read();
        records[session.WorkspaceId] = new(session.Source, session.AgentId, session.Author);
        write(records);
    }

    internal static string? ReadFile(string path)
    {
        try { return System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(System.IO.Path.Combine(path, BlueprintContract.File))); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static BlueprintDoc? OnDisk(Session session)
    {
        var text = ReadFile(session.Path);
        return text is null ? null : BlueprintReconcile.CarryOverLocks(session.Seen ?? Empty, BlueprintFormat.Parse(text));
    }

    private static BlueprintState StateOf(Session session, BlueprintDoc? doc)
    {
        var title = Regex.Match(doc?.Frontmatter ?? "", @"^title:\s*(.+)$", RegexOptions.Multiline);
        return new(session.WorkspaceId, session.Source,
            title.Success ? title.Groups[1].Value.Trim().Trim('\'', '"') : BlueprintPrompts.DescribeSource(session.Source),
            session.AgentId, session.Author, doc is null ? BlueprintPhase.Awaiting : BlueprintPhase.Ready, doc ?? Empty,
            doc is not null && session.Seen is not null ? BlueprintReconcile.Diff(session.Seen, doc) : [],
            session.PendingEdits, BlueprintFormat.BlockLines(doc ?? Empty));
    }

    private BlueprintState Publish(Session session, BlueprintDoc? doc)
    {
        var state = StateOf(session, doc);
        session.Seen = doc;
        publish(new(session.WorkspaceId, state));
        return state;
    }

    private Session Require(string workspaceId) => sessions.GetValueOrDefault(workspaceId)
        ?? throw new InvalidOperationException($"No blueprint in workspace {workspaceId}");

    public BlueprintState Open(string workspaceId, string path, BlueprintSource source, BlueprintAgentId agentId)
    {
        var session = new Session(workspaceId, path, source, agentId);
        sessions[workspaceId] = session;
        Remember(session);
        return StateOf(session, OnDisk(session));
    }

    public BlueprintState? Get(string workspaceId, string? path)
    {
        if (!sessions.TryGetValue(workspaceId, out var session) && path is not null)
        {
            var record = read().GetValueOrDefault(workspaceId);
            if (record?.Closed == true || record is null && ReadFile(path) is null) return null;
            session = new(workspaceId, path, record?.Source ?? new BlueprintProduct(),
                record?.AgentId ?? BlueprintAgentId.Claude, record?.Author);
            sessions[workspaceId] = session;
            if (record is null) Remember(session);
        }
        return session is null ? null : StateOf(session, OnDisk(session));
    }

    public BlueprintAuthor? Author(string workspaceId) => sessions.GetValueOrDefault(workspaceId)?.Author;

    public void SetAuthor(string workspaceId, BlueprintAuthor author)
    {
        var session = Require(workspaceId);
        session.Author = author;
        Remember(session);
        Publish(session, OnDisk(session));
    }

    public void NoteAuthorSession(string workspaceId, string tabKey, string agentSessionId)
    {
        if (sessions.GetValueOrDefault(workspaceId) is not { Author: BlueprintTerminalAuthor author } session ||
            author.TabKey != tabKey || author.AgentSessionId == agentSessionId) return;
        session.Author = author with { AgentSessionId = agentSessionId };
        Remember(session);
        Publish(session, OnDisk(session));
    }

    public void NoteFileChanged(string workspaceId)
    {
        if (sessions.GetValueOrDefault(workspaceId) is { } session) Publish(session, OnDisk(session));
    }

    private string WriteAndReconcile(Session session, BlueprintDoc doc, IReadOnlyList<string> ids, IReadOnlyList<BlueprintEdit> edits)
    {
        File.WriteAllText(System.IO.Path.Combine(session.Path, BlueprintContract.File), BlueprintFormat.Serialize(doc));
        Publish(session, doc);
        return BlueprintPrompts.Reconcile(doc, ids, edits);
    }

    public string? Select(string workspaceId, string controlId, string optionId)
    {
        var session = Require(workspaceId);
        var current = OnDisk(session);
        if (current is null) return null;
        var edits = session.PendingEdits;
        session.PendingEdits = [];
        return WriteAndReconcile(session, BlueprintReconcile.ApplySelection(current, controlId, optionId), [controlId], edits);
    }

    public void Edit(string workspaceId, BlueprintEditTarget target, string after)
    {
        var session = Require(workspaceId);
        if (session.Seen is null) return;
        var before = BlueprintReconcile.TextAt(session.Seen, target);
        if (before is null || before == after) return;
        var original = session.PendingEdits.FirstOrDefault(edit => edit.Target == target);
        session.PendingEdits = session.PendingEdits.Where(edit => edit.Target != target)
            .Append(new(target, original?.Before ?? before, after)).ToArray();
        Publish(session, BlueprintReconcile.ApplyTextEdit(session.Seen, target, after));
    }

    public string? ConfirmEdits(string workspaceId)
    {
        var session = Require(workspaceId);
        if (session.PendingEdits.Count == 0 || session.Seen is null) return null;
        var edits = session.PendingEdits;
        session.PendingEdits = [];
        return WriteAndReconcile(session, session.Seen, [], edits);
    }

    public void DiscardEdits(string workspaceId)
    {
        var session = Require(workspaceId);
        if (session.PendingEdits.Count == 0) return;
        session.PendingEdits = [];
        session.Seen = null;
        Publish(session, OnDisk(session));
    }

    public void Close(string workspaceId)
    {
        sessions.Remove(workspaceId);
        var records = read();
        records[workspaceId] = new(new BlueprintProduct(), BlueprintAgentId.Claude, Closed: true);
        write(records);
    }
}