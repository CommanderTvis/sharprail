using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.ClaudeCode.Host.Chat;

/// <summary>
/// The host half of chat mode: the registry of chats, the launch rules for their headless child (the same environment,
/// prompt file and launch contributions a terminal launch gets), the persisted records that revive a chat after a restart,
/// and the handler of every <see cref="ChatContract"/> method.
/// </summary>
internal sealed class ClaudeChatHost
{
    private const string RecordsState = "chats";

    private readonly IPluginHostContext context;
    private readonly Func<TerminalRef, IReadOnlyDictionary<string, string>> pluginEnvironment;
    private readonly Func<string> loopbackRoot;
    private readonly Action<ClaudeCodeStatusPush> status;
    private readonly string hookPluginDirectory;
    private readonly ConcurrentDictionary<(string, string), ClaudeChatSession> sessions = new();
    private readonly Lock recordsGate = new();

    public ClaudeChatHost(IPluginHostContext context, Func<TerminalRef, IReadOnlyDictionary<string, string>> pluginEnvironment, Func<string> loopbackRoot,
        Action<ClaudeCodeStatusPush> status, string hookPluginDirectory)
    {
        this.context = context;
        this.pluginEnvironment = pluginEnvironment;
        this.loopbackRoot = loopbackRoot;
        this.status = status;
        this.hookPluginDirectory = hookPluginDirectory;
    }

    private static string RecordKey(string workspaceId, string chatId) => workspaceId + "\0" + chatId;

    private ChatRecord? LoadRecord(string workspaceId, string chatId) =>
        context.ReadState<Dictionary<string, ChatRecord>>(RecordsState, []).GetValueOrDefault(RecordKey(workspaceId, chatId));

    private void SaveRecord(ChatRecord record)
    {
        lock (recordsGate)
        {
            var all = context.ReadState<Dictionary<string, ChatRecord>>(RecordsState, []);
            all[RecordKey(record.WorkspaceId, record.ChatId)] = record;
            context.WriteState(RecordsState, all);
        }
    }

    private ClaudeChatSession Session(string workspaceId, string chatId) =>
        sessions.TryGetValue((workspaceId, chatId), out var session) ? session : throw new InvalidOperationException("Unknown chat: " + chatId);

    private ClaudeChatSession Create(ChatRecord record) =>
        sessions.AddOrUpdate((record.WorkspaceId, record.ChatId), _ => Build(record), (_, existing) => existing);

    private ClaudeChatSession Build(ChatRecord record) => new(record, new ChatDeps(Launch, Publish, status, SaveRecord, message => context.Log.Warn(message)));

    // A push the wire refuses is logged, never allowed to take the session's flush down with it.
    private void Publish(ChatPush push)
    {
        try { context.Publish(ChatContract.Updates, push); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        {
            context.Log.Warn($"could not publish a chat {push.GetType().Name}: {error.Message}");
        }
    }

    public void Register()
    {
        context.Method(ChatContract.Open, (p, _, _) => OpenAsync(p));
        context.Method(ChatContract.Snapshot, (p, _, _) => ValueTask.FromResult(sessions.TryGetValue((p.WorkspaceId, p.ChatId), out var session) ? session.Snapshot() : []));
        context.Method(ChatContract.Send, async (p, _, _) =>
        {
            await Session(p.WorkspaceId, p.ChatId).SendAsync(p.Text, p.Attachments);
            return new Ack();
        });
        context.Method(ChatContract.Interrupt, async (p, _, _) =>
        {
            await Session(p.WorkspaceId, p.ChatId).InterruptAsync();
            return new Ack();
        });
        context.Method(ChatContract.Close, async (p, _, _) =>
        {
            if (sessions.TryGetValue((p.WorkspaceId, p.ChatId), out var session)) await session.CloseAsync();
            return new Ack();
        });
        context.Method(ChatContract.SetModel, async (p, _, _) => await Session(p.WorkspaceId, p.ChatId).SetModelAsync(p.Value));
        context.Method(ChatContract.SetPermissionMode, async (p, _, _) => await Session(p.WorkspaceId, p.ChatId).SetPermissionModeAsync(p.Value));
        context.Method(ChatContract.SetEffort, async (p, _, _) => await Session(p.WorkspaceId, p.ChatId).SetEffortAsync(p.Value));
        context.Method(ChatContract.Answer, async (p, _, _) =>
        {
            await Session(p.WorkspaceId, p.ChatId).AnswerAsync(p);
            return new Ack();
        });
        context.Method(ChatContract.AnswerQuestion, async (p, _, _) =>
        {
            await Session(p.WorkspaceId, p.ChatId).AnswerQuestionAsync(p);
            return new Ack();
        });
        context.Method(ChatContract.Dequeue, async (p, _, _) =>
        {
            await Session(p.WorkspaceId, p.ChatId).DequeueAsync(p.ItemId);
            return new Ack();
        });
        context.Method(ChatContract.EditQueued, async (p, _, _) =>
        {
            await Session(p.WorkspaceId, p.ChatId).EditQueuedAsync(p.ItemId, p.Text);
            return new Ack();
        });
        context.Method(ChatContract.SendQueuedNow, async (p, _, _) =>
        {
            await Session(p.WorkspaceId, p.ChatId).SendQueuedNowAsync(p.ItemId);
            return new Ack();
        });
        context.Method(ChatContract.Rewind, async (p, _, _) =>
        {
            await Session(p.WorkspaceId, p.ChatId).RewindAsync(p.Uuid, p.Files == true);
            return new Ack();
        });
        context.Method(ChatContract.RewindPreview, async (p, _, _) => await Session(p.WorkspaceId, p.ChatId).RewindPreviewAsync(p.Uuid));
        context.Method(ChatContract.Fork, (p, _, _) => Task.Run(() => Fork(p)).ToValueTask());
        context.Method(ChatContract.StopTask, async (p, _, _) =>
        {
            await Session(p.WorkspaceId, p.ChatId).StopTaskAsync(p.TaskId);
            return new Ack();
        });
        context.Method(ChatContract.ItemDetail, (p, _, _) =>
            ValueTask.FromResult(new ChatDetail(Session(p.WorkspaceId, p.ChatId).ItemDetail(p.ItemId) ?? "")));
        context.Method(ChatContract.Sessions, (p, _, ct) => Task.Run(() => ListSessions(p.WorkspaceId, ct), ct).ToValueTask());
        context.Method(ChatContract.Files, async (p, _, ct) => await FindFilesAsync(p, ct));
    }

    public async Task DisposeAsync()
    {
        var all = sessions.Values.ToArray();
        sessions.Clear();
        await Task.WhenAll(all.Select(session => session.DisposeAsync()));
    }

    private async ValueTask<ChatHeader> OpenAsync(ChatOpenParams p)
    {
        if (sessions.TryGetValue((p.WorkspaceId, p.ChatId), out var existing) && existing.Active) return existing.Header;
        // Two children writing one transcript would mix two conversations: a session another tab of this workspace runs is refused here, whatever the caller asked.
        if (p.ResumeSessionId is { } resume && p.Fork != true &&
            sessions.Values.FirstOrDefault(other => other.Active && other.Record.WorkspaceId == p.WorkspaceId && other.Record.ChatId != p.ChatId && other.Record.SessionId == resume) is { } running)
            throw new InvalidOperationException($"That conversation is already open in another tab (chat {running.Record.ChatId}).");
        var settings = context.Settings<ClaudeCodeSettings>();
        var record = existing?.Record ?? await Task.Run(() => LoadRecord(p.WorkspaceId, p.ChatId));
        var cwd = p.WorkspaceId;
        if (record is null)
        {
            var fresh = new ChatRecord(p.WorkspaceId, p.ChatId)
            {
                SessionId = p.ResumeSessionId ?? (AgentResume.IsSessionId(p.ChatId) ? p.ChatId : Guid.NewGuid().ToString()),
                Cwd = cwd,
                Model = p.Model ?? Empty(settings.Model),
                Effort = p.Effort ?? Empty(settings.Effort),
                PermissionMode = p.PermissionMode ?? Empty(settings.PermissionMode)
            };
            record = p.Fork == true && p.ResumeSessionId is { } source ? fresh with { ForkOf = source, SessionId = null } : fresh;
            SaveRecord(record);
        }
        var session = existing ?? Create(record);
        var header = session.Begin();
        var (spawn, transcript) = await Task.Run(() => Plan(record));
        _ = session.StartAsync(spawn, transcript, record.ForkOf is null ? null : record.ForkBefore);
        return header;
    }

    private static string? Empty(string value) => value.Length == 0 ? null : value;

    // How this open continues the conversation: a fork branches from its source, a recorded session resumes when its
    // transcript is on disk and otherwise starts afresh under the same id. The transcript is what the view shows first.
    private static (ChatSpawn Spawn, string? Transcript) Plan(ChatRecord record)
    {
        var cwd = record.Cwd ?? record.WorkspaceId;
        if (record.ForkOf is { } source)
            return (new ChatSpawn(null, source, true, null), AgentResume.TranscriptPath(cwd, source));
        var id = record.SessionId ?? record.ChatId;
        return AgentResume.TranscriptPath(cwd, id) is { } path
            ? (new ChatSpawn(null, id, false, null), path)
            : (new ChatSpawn(id, null, false, null), null);
    }

    private ChatForked Fork(ChatRewindParams p)
    {
        if (!sessions.TryGetValue((p.WorkspaceId, p.ChatId), out var live)) throw new InvalidOperationException("Failed to fork conversation: Unknown session id");
        var source = live.Record;
        var copy = new ChatRecord(p.WorkspaceId, Guid.NewGuid().ToString())
        {
            Cwd = source.Cwd,
            Model = source.Model,
            Effort = source.Effort,
            PermissionMode = source.PermissionMode,
            Title = source.Title is { } title ? title + " (fork)" : null,
            ForkOf = source.SessionId ?? source.ChatId,
            ForkBefore = p.Uuid
        };
        SaveRecord(copy);
        return new ChatForked(copy.ChatId) { PromptText = live.PromptTextOf(p.Uuid) };
    }

    private ChatSessionList ListSessions(string workspaceId, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(AgentResume.ExpectedTranscriptPath(workspaceId, Guid.Empty.ToString()));
        if (directory is null || !Directory.Exists(directory)) return new([]);
        var found = new List<ChatSessionInfo>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.jsonl").OrderByDescending(file => file.LastWriteTimeUtc).Take(50))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(file.Name);
            if (!AgentResume.IsSessionId(id)) continue;
            found.Add(new ChatSessionInfo(id, FirstPrompt(file.FullName) ?? id, file.LastWriteTimeUtc.ToString("o")));
        }
        // Which of them a chat runs now, and which a chat started (its record names the session).
        var running = sessions.Values.Where(session => session.Active && session.Record.WorkspaceId == workspaceId && session.Record.SessionId is not null)
            .GroupBy(session => session.Record.SessionId!).ToDictionary(group => group.Key, group => group.First().Record.ChatId);
        var started = context.ReadState<Dictionary<string, ChatRecord>>(RecordsState, []).Values
            .Where(record => record.WorkspaceId == workspaceId && record.SessionId is not null).Select(record => record.SessionId!).ToHashSet();
        return new([.. found.Select(info => info with { ChatId = running.GetValueOrDefault(info.SessionId), StartedIn = started.Contains(info.SessionId) ? "chat" : null })]);
    }

    private static string? FirstPrompt(string path)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            for (var lines = 0; lines < 200 && reader.ReadLine() is { } line; lines++)
            {
                if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal)) continue;
                var message = JsonSerializer.Deserialize<JsonElement>(line);
                if (ChatFolder.Str(message, "type") != "user" || message.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) continue;
                if (!message.TryGetProperty("message", out var body) || !body.TryGetProperty("content", out var content)) continue;
                var text = content.ValueKind == JsonValueKind.String ? content.GetString()
                    : content.ValueKind == JsonValueKind.Array ? string.Concat(content.EnumerateArray().Select(block => ChatFolder.Str(block, "text"))) : null;
                if (text is { Length: > 0 }) return text.Length > 120 ? text[..120] : text;
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        return null;
    }

    private async ValueTask<ChatFileList> FindFilesAsync(ChatFilesParams p, CancellationToken cancellationToken)
    {
        var listing = await context.GitAsync(p.WorkspaceId, ["ls-files", "--cached", "--others", "--exclude-standard"], new GitRunOptions(TimeSpan.FromSeconds(10)), cancellationToken);
        if (!listing.Ok) return new([]);
        var query = p.Query.Trim();
        var paths = listing.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => query.Length == 0 || path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path).Contains(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(path => path.Length)
            .Take(50);
        return new([.. paths]);
    }

    // The child gets what a terminal launch gets: the plugin's environment (prompt file, IDE bridge port, scratch and
    // worktree directories), the host's MCP address keyed to this chat, and every other plugin's launch contribution.
    private ChatLaunch Launch(ChatRecord record, ChatSpawn spawn)
    {
        var settings = context.Settings<ClaudeCodeSettings>();
        var terminal = new TerminalRef(record.WorkspaceId, record.ChatId);
        var cwd = record.Cwd is { } known && Directory.Exists(known) ? known : record.WorkspaceId;
        var environment = new Dictionary<string, string>(pluginEnvironment(terminal))
        {
            ["THINKRAIL_MCP_URL"] = $"{loopbackRoot()}/mcp/{context.TerminalToken(terminal)}",
            ["SHARPRAIL_UI_LAUNCH"] = "claude",
            // The chat reports its own status from the stream; the hook plugin must not report it a second time.
            [ClaudeCodeContract.StatusUrlVariable] = ""
        };
        if (settings.DisableAgentView) environment["CLAUDE_CODE_DISABLE_AGENT_VIEW"] = "true";

        var words = Words(settings.Command.Trim().Length > 0 ? settings.Command : "claude");
        var contributions = context.AgentLaunchContributions("claude");
        var executable = words[0];
        var prefix = new List<string>();
        foreach (var contribution in contributions.Where(contribution => !contribution.ReplacesExecutable)) prefix.AddRange(contribution.Wrapper);
        if (contributions.FirstOrDefault(contribution => contribution.ReplacesExecutable && contribution.Wrapper.Count > 0) is { } replacer)
            prefix.AddRange(replacer.Wrapper);
        else prefix.Add(executable);
        var arguments = new List<string>();
        foreach (var contribution in contributions) arguments.AddRange(contribution.Arguments);
        foreach (var (name, value) in contributions.SelectMany(contribution => contribution.Environment))
            environment.TryAdd(name, value);
        arguments.AddRange(words.Skip(1));
        arguments.AddRange(["-p", "--output-format", "stream-json", "--verbose", "--input-format", "stream-json", "--include-partial-messages", "--replay-user-messages", "--permission-prompt-tool=stdio"]);
        if (spawn.Resume is { } resume) arguments.AddRange(["--resume", resume]);
        if (spawn.Fork) arguments.Add("--fork-session");
        if (spawn.ResumeAt is { } at) arguments.AddRange(["--resume-session-at", at]);
        if (spawn.SessionId is { } sessionId) arguments.AddRange(["--session-id", sessionId]);
        if (record.Model is { Length: > 0 } model) arguments.AddRange(["--model", model]);
        if (record.Effort is { Length: > 0 } effort) arguments.AddRange(["--effort", effort]);
        if (record.PermissionMode is { Length: > 0 } mode) arguments.AddRange(["--permission-mode", mode]);
        if (settings.Advisor.Length > 0) arguments.AddRange(["--advisor", settings.Advisor]);
        if (settings.Chrome) arguments.Add("--chrome");
        if (settings.AppendSystemPrompt && environment.TryGetValue(ClaudeCodeContract.PromptFileVariable, out var promptFile)) arguments.AddRange(["--append-system-prompt-file", promptFile]);
        arguments.AddRange(["--plugin-dir", hookPluginDirectory]);
        return new ChatLaunch(prefix[0], [.. prefix.Skip(1), .. arguments], environment, cwd);
    }

    // The configured command line as words, honouring single and double quotes; never interpreted by a shell.
    internal static List<string> Words(string command)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        char? quote = null;
        var started = false;
        foreach (var character in command)
        {
            if (quote is { } open) { if (character == open) quote = null; else word.Append(character); }
            else if (character is '\'' or '"') { quote = character; started = true; }
            else if (char.IsWhiteSpace(character)) { if (started || word.Length > 0) { words.Add(word.ToString()); word.Clear(); started = false; } }
            else word.Append(character);
        }
        if (started || word.Length > 0) words.Add(word.ToString());
        return words.Count == 0 ? ["claude"] : words;
    }
}

internal static class ChatTasks
{
    public static ValueTask<T> ToValueTask<T>(this Task<T> task) => new(task);
}