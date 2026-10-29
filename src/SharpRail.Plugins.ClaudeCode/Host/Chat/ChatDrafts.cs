using System.Text.Json.Serialization;

using SharpRail.Plugins.Chat;

namespace SharpRail.Plugins.ClaudeCode.Host.Chat;

/// <summary>One chat's stored draft: the text, its revision, the revision the last send consumed, and when it last changed (Unix milliseconds).</summary>
internal sealed record StoredDraft(string Text, long Revision, long Consumed, long Changed)
{
    /// <summary>The client whose write made this revision; kept in memory only.</summary>
    [JsonIgnore]
    public string? Origin { get; init; }
}

/// <summary>
/// The shared composer drafts of every chat: last-writer-wins on the whole text at a monotonic revision, a claim that lets one send per
/// draft revision through, and persistence through the plugin's state (bounded, written after a short debounce, flushed on dispose).
/// </summary>
internal sealed class ChatDrafts : IDisposable
{
    public const int MaxChats = 200;
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly Lock gate = new();
    private readonly Dictionary<string, StoredDraft> drafts;
    private readonly Action<Dictionary<string, StoredDraft>> write;
    private readonly Action<ChatPush> publish;
    private readonly Action<string> warn;
    private readonly Timer timer;
    private bool dirty;
    private bool disposed;

    public ChatDrafts(Func<Dictionary<string, StoredDraft>> read, Action<Dictionary<string, StoredDraft>> write, Action<ChatPush> publish, Action<string> warn)
    {
        drafts = read();
        this.write = write;
        this.publish = publish;
        this.warn = warn;
        timer = new Timer(_ => Save());
    }

    private static string Key(string workspaceId, string chatId) => workspaceId + "\0" + chatId;

    private static ChatDraft Record(StoredDraft stored) => new(stored.Text, stored.Revision) { Origin = stored.Origin };

    public ChatDraft Get(string workspaceId, string chatId)
    {
        lock (gate) return drafts.TryGetValue(Key(workspaceId, chatId), out var stored) ? Record(stored) : new ChatDraft("", 0);
    }

    /// <summary>Last writer wins: the next revision, unless the text is the one already stored.</summary>
    public ChatDraft Set(string workspaceId, string chatId, string text, string? clientId)
    {
        if (text.Length > ChatContract.DraftLimit) throw new InvalidOperationException($"A draft is at most {ChatContract.DraftLimit} characters.");
        ChatDraft result;
        lock (gate)
        {
            var key = Key(workspaceId, chatId);
            var current = drafts.GetValueOrDefault(key) ?? new StoredDraft("", 0, 0, 0);
            if (current.Text == text && current.Revision > 0) return Record(current);
            if (text.Length == 0 && current.Revision == 0) return Record(current);
            result = Record(Store(key, current with { Text = text, Revision = current.Revision + 1, Origin = clientId }));
        }
        publish(new ChatDraftPush(workspaceId, chatId, result));
        return result;
    }

    /// <summary>Claims a send from this draft revision; refuses one an earlier send already consumed. Returns what to restore if the send fails.</summary>
    public long Claim(string workspaceId, string chatId, long revision)
    {
        lock (gate)
        {
            var key = Key(workspaceId, chatId);
            var current = drafts.GetValueOrDefault(key) ?? new StoredDraft("", 0, 0, 0);
            if (revision <= current.Consumed) throw new InvalidOperationException(ChatContract.AlreadySent);
            Store(key, current with { Consumed = revision });
            return current.Consumed;
        }
    }

    /// <summary>Gives a claim back after a send that failed.</summary>
    public void Release(string workspaceId, string chatId, long revision, long previous)
    {
        lock (gate)
        {
            var key = Key(workspaceId, chatId);
            if (drafts.TryGetValue(key, out var current) && current.Consumed == revision) Store(key, current with { Consumed = previous });
        }
    }

    /// <summary>After a send: an empty draft at a new revision, pushed to every client.</summary>
    public void Clear(string workspaceId, string chatId, string? clientId)
    {
        ChatDraft result;
        lock (gate)
        {
            var key = Key(workspaceId, chatId);
            var current = drafts.GetValueOrDefault(key) ?? new StoredDraft("", 0, 0, 0);
            result = Record(Store(key, current with { Text = "", Revision = current.Revision + 1, Origin = clientId }));
        }
        publish(new ChatDraftPush(workspaceId, chatId, result));
    }

    public void ForgetWorkspace(string workspaceId)
    {
        lock (gate)
        {
            var prefix = workspaceId + "\0";
            var gone = drafts.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (gone.Count == 0) return;
            foreach (var key in gone) drafts.Remove(key);
            Dirty();
        }
    }

    private StoredDraft Store(string key, StoredDraft draft)
    {
        draft = draft with { Changed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        drafts[key] = draft;
        if (drafts.Count > MaxChats)
            foreach (var old in drafts.Where(pair => pair.Key != key).OrderBy(pair => pair.Value.Text.Length > 0).ThenBy(pair => pair.Value.Changed).Take(drafts.Count - MaxChats).Select(pair => pair.Key).ToList())
                drafts.Remove(old);
        Dirty();
        return draft;
    }

    private void Dirty()
    {
        dirty = true;
        if (!disposed) timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Writes the drafts now when something changed since the last write.</summary>
    public void Save()
    {
        Dictionary<string, StoredDraft> copy;
        lock (gate)
        {
            if (!dirty) return;
            dirty = false;
            copy = new(drafts);
        }
        try { write(copy); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            lock (gate) dirty = true;
            warn("could not save the chat drafts: " + error.Message);
        }
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        timer.Dispose();
        Save();
    }
}
