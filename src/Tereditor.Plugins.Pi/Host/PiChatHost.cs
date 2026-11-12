using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.Chat;

namespace Tereditor.Plugins.Pi.Host;

/// <summary>What the host remembers of a chat so reopening its tab continues the same Pi session.</summary>
internal sealed record PiChatRecord(string WorkspaceId, string ChatId)
{
    public string? SessionFile { get; init; }
}

/// <summary>The registry of Pi chats and the handler of every <see cref="ChatContract"/> method.</summary>
internal sealed class PiChatHost(IPluginHostContext context, Func<string>? sessionsRoot = null)
{
    private const string RecordsState = "chats";

    private readonly ConcurrentDictionary<(string, string), PiChatSession> sessions = new();
    private readonly Lock recordsGate = new();
    private readonly Func<string> sessionsRoot = sessionsRoot ?? DefaultSessionsRoot;

    /// <summary>Pi's session store: <c>~/.pi/agent/sessions</c>, or <c>PI_CODING_AGENT_DIR/sessions</c> when that is set.</summary>
    public static string DefaultSessionsRoot() =>
        Path.Combine(Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") is { Length: > 0 } agent ? agent
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent"), "sessions");

    private static string RecordKey(string workspaceId, string chatId) => workspaceId + "\0" + chatId;

    private PiChatRecord? LoadRecord(string workspaceId, string chatId) =>
        context.ReadState<Dictionary<string, PiChatRecord>>(RecordsState, []).GetValueOrDefault(RecordKey(workspaceId, chatId));

    private void SaveSessionFile(string workspaceId, string chatId, string? file)
    {
        lock (recordsGate)
        {
            var all = context.ReadState<Dictionary<string, PiChatRecord>>(RecordsState, []);
            all[RecordKey(workspaceId, chatId)] = new PiChatRecord(workspaceId, chatId) { SessionFile = file };
            context.WriteState(RecordsState, all);
        }
    }

    private PiChatSession Session(string workspaceId, string chatId) =>
        sessions.TryGetValue((workspaceId, chatId), out var session) ? session : throw new InvalidOperationException("Unknown chat: " + chatId);

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
            if (sessions.TryRemove((p.WorkspaceId, p.ChatId), out var session)) await session.DisposeAsync();
            return new Ack();
        });
        context.Method(ChatContract.SetModel, async (p, _, _) => await Session(p.WorkspaceId, p.ChatId).SetModelAsync(p.Value));
        context.Method(ChatContract.SetEffort, async (p, _, _) => await Session(p.WorkspaceId, p.ChatId).SetEffortAsync(p.Value));
        context.Method(ChatContract.SetPermissionMode, (p, _, _) => ValueTask.FromResult(Session(p.WorkspaceId, p.ChatId).Header));
        context.Method(ChatContract.Answer, (_, _, _) => throw Unsupported("Pi asks no permissions."));
        context.Method(ChatContract.AnswerQuestion, (_, _, _) => throw Unsupported("Pi asks no questions."));
        context.Method(ChatContract.Dequeue, (_, _, _) => throw Unsupported("Pi does not take back a queued message."));
        context.Method(ChatContract.EditQueued, (_, _, _) => throw Unsupported("Pi does not change a queued message."));
        context.Method(ChatContract.Rewind, (_, _, _) => throw Unsupported("Pi cannot rewind here yet."));
        context.Method(ChatContract.RewindPreview, (_, _, _) => throw Unsupported("Pi cannot rewind here yet."));
        context.Method(ChatContract.Fork, (_, _, _) => throw Unsupported("Pi cannot fork here yet."));
        context.Method(ChatContract.StopTask, (_, _, _) => throw Unsupported("Pi has no background tasks."));
        context.Method(ChatContract.ItemDetail, (p, _, _) => ValueTask.FromResult(new ChatDetail(Session(p.WorkspaceId, p.ChatId).ItemDetail(p.ItemId) ?? "")));
        context.Method(ChatContract.Sessions, (p, _, ct) => new ValueTask<ChatSessionList>(Task.Run(async () => ListSessions(await RootOf(p.WorkspaceId, ct)), ct)));
        context.Method(ChatContract.Files, async (p, _, ct) => await FindFilesAsync(p, ct));
    }

    private static NotSupportedException Unsupported(string message) => new(message);

    public async Task DisposeAsync()
    {
        var all = sessions.Values.ToArray();
        sessions.Clear();
        await Task.WhenAll(all.Select(session => session.DisposeAsync().AsTask()));
    }

    private async ValueTask<string> RootOf(string workspaceId, CancellationToken cancellationToken) =>
        (await context.WorkspaceAsync(workspaceId, cancellationToken))?.Path ?? workspaceId;

    private async ValueTask<ChatHeader> OpenAsync(ChatOpenParams p)
    {
        if (sessions.TryGetValue((p.WorkspaceId, p.ChatId), out var existing) && existing.Active) return existing.Header;
        var settings = context.Settings<PiSettings>();
        var cwd = await RootOf(p.WorkspaceId, CancellationToken.None);
        var record = await Task.Run(() => LoadRecord(p.WorkspaceId, p.ChatId));
        var resume = p.ResumeSessionId ?? record?.SessionFile;
        var spawn = new PiSpawn(settings.Command.Trim() is { Length: > 0 } command ? command : "pi", cwd)
        {
            Model = p.Model ?? (settings.Model.Length > 0 ? settings.Model : null),
            Thinking = p.Effort ?? (settings.Thinking.Length > 0 ? settings.Thinking : null),
            Session = resume is not null && File.Exists(resume) ? resume : null
        };
        var session = existing ?? sessions.AddOrUpdate((p.WorkspaceId, p.ChatId),
            _ => new PiChatSession(p.WorkspaceId, p.ChatId, Publish, message => context.Log.Warn(message), (chat, file) => SaveSessionFile(p.WorkspaceId, chat, file)),
            (_, current) => current);
        var header = session.Begin(spawn);
        _ = session.StartAsync(spawn);
        return header;
    }

    /// <summary>Pi's stored sessions for a directory, newest first: <c>--&lt;path with / as -&gt;--</c> under the sessions root.</summary>
    internal ChatSessionList ListSessions(string cwd)
    {
        var directory = Path.Combine(sessionsRoot(), "--" + cwd.TrimStart('/', '\\').Replace('/', '-').Replace('\\', '-').Replace(':', '-') + "--");
        if (!Directory.Exists(directory)) return new([]);
        var files = new DirectoryInfo(directory).EnumerateFiles("*.jsonl").OrderByDescending(file => file.LastWriteTimeUtc).Take(50);
        var open = sessions.Values.Where(session => session.Active && session.SessionFile is not null).ToDictionary(session => session.SessionFile!, session => session);
        return new([.. files.Select(file => new ChatSessionInfo(file.FullName, TitleOf(file.FullName), file.LastWriteTimeUtc.ToString("o"))
        {
            ChatId = open.TryGetValue(file.FullName, out var live) ? ChatOf(live) : null
        })]);
    }

    private string? ChatOf(PiChatSession session) => sessions.FirstOrDefault(pair => ReferenceEquals(pair.Value, session)).Key.Item2;

    // The session's first user message, or the name the user gave it.
    private static string TitleOf(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path).Take(200))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (PiFolder.Str(root, "type") == "session_info" && PiFolder.Str(root, "name") is { Length: > 0 } name) return name;
                if (PiFolder.Str(root, "type") == "message" && root.TryGetProperty("message", out var message) && PiFolder.Str(message, "role") == "user"
                    && PiFolder.UserText(message).Trim() is { Length: > 0 } text) return text.Length > 80 ? text[..80] : text;
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        return Path.GetFileNameWithoutExtension(path);
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
}

[JsonSerializable(typeof(Dictionary<string, PiChatRecord>))]
internal sealed partial class PiHostJson : JsonSerializerContext;