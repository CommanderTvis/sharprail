using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.Chat;

namespace SharpRail.Plugins.Acp.Host;

/// <summary>What the host remembers of a chat: which agent it talks to and the ACP session to resume.</summary>
internal sealed record AcpChatRecord(string WorkspaceId, string ChatId)
{
    public string? AgentId { get; init; }
    public string? SessionId { get; init; }
}

/// <summary>The registry of ACP chats and the handler of every <see cref="ChatContract"/> method.</summary>
internal sealed class AcpChatHost(IPluginHostContext context, Func<AcpAgentEntry, string, Task<IAcpTransport>>? connect = null)
{
    private const string RecordsState = "chats";
    private const string AgentsState = "agents";

    private readonly ConcurrentDictionary<(string, string), AcpChatSession> sessions = new();
    private readonly Lock recordsGate = new();

    /// <summary>Where the registry copy and installed binaries live; ~/.sharprail/acp unless a check says otherwise.</summary>
    public string DataDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharprail", "acp");
    public AcpFetch Fetch { get; init; } = AcpRegistryCache.HttpFetch;
    public Func<string, CancellationToken, Task<byte[]>> Download { get; init; } = AcpInstaller.HttpDownload;

    private AcpRegistryCache Cache => new(Path.Combine(DataDirectory, "registry"), Fetch);

    public AcpFetch FetchIcon { get; init; } = AcpIconCache.HttpFetch;

    /// <summary>The icons of the registry's agents. The urls come from the registry copy here, never from the UI.</summary>
    public async Task<AcpIconSet> IconsAsync(AcpIconParams p, CancellationToken cancellationToken)
    {
        var root = p.CacheOnly ? await Cache.CachedAsync(cancellationToken) : (await Cache.LoadAsync(false, cancellationToken)).Root;
        if (root is null) return new(new Dictionary<string, string>());
        var wanted = AcpRegistry.View(root, "cache", null, null, AcpRegistry.Platform()).Agents.Where(agent => agent.Icon is not null).Select(agent => (agent.Id, agent.Icon!));
        return new(await new AcpIconCache(Path.Combine(DataDirectory, "registry", "icons"), FetchIcon).LoadAsync(wanted, p.Refresh, p.CacheOnly, cancellationToken));
    }

    public async Task<AcpInstallPlan> PlanAsync(AcpInstallPlanParams p, CancellationToken cancellationToken)
    {
        var (root, _) = await Cache.LoadAsync(false, cancellationToken);
        return AcpRegistry.Plan(root, p.AgentId, p.Kind, AcpRegistry.Platform()) ?? throw new InvalidOperationException("The registry has no such agent for this machine.");
    }

    public async Task<AcpAgentList> InstallAsync(AcpInstallRequest request, CancellationToken cancellationToken)
    {
        var (root, _) = await Cache.LoadAsync(false, cancellationToken);
        var entry = await new AcpInstaller(Path.Combine(DataDirectory, "agents"), Download).InstallAsync(root, request, AcpRegistry.Platform(), cancellationToken);
        // An update or reinstall keeps what the user set on the agent.
        if (ListAgents().Agents.FirstOrDefault(existing => existing.Id == entry.Id) is { } before)
            entry = entry with { Enabled = before.Enabled, CommandOverride = before.CommandOverride, UseDefaultArgs = before.UseDefaultArgs, ArgsOverride = before.ArgsOverride, Cwd = before.Cwd, Env = new Dictionary<string, string>(entry.Env).Concat(before.Env).GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => group.Last().Value) };
        failures.TryRemove(entry.Id, out _);
        return AddAgent(entry);
    }

    private async Task<IAcpTransport> LaunchAsync(AcpAgentEntry entry, string cwd)
    {
        var directory = entry.Cwd.Length > 0 ? entry.Cwd : cwd;
        var command = entry.LaunchCommand();
        IReadOnlyList<string> args = entry.LaunchArgs();
        var env = entry.Env;
        // A registry npx agent runs with the host's pinned Bun, so no Node or npx is needed; npx stays when Bun cannot be provided, and for a command the user chose.
        if (entry.CommandOverride.Length == 0 && entry.Origin == "registry" && command == "npx" && await context.BunPathAsync(directory) is { Length: > 0 } bun)
        {
            (command, args) = AcpRunner.Bun(bun, args);
            env = AcpRunner.GuardedEnvironment(Path.Combine(DataDirectory, "bun"), env);
        }
        return await ProcessAcpTransport.StartAsync(new AcpLaunch(command, args, env, directory));
    }

    private static string RecordKey(string workspaceId, string chatId) => workspaceId + "\0" + chatId;

    private AcpChatRecord? LoadRecord(string workspaceId, string chatId) =>
        context.ReadState<Dictionary<string, AcpChatRecord>>(RecordsState, []).GetValueOrDefault(RecordKey(workspaceId, chatId));

    private void SaveRecord(string workspaceId, string chatId, Func<AcpChatRecord, AcpChatRecord> edit)
    {
        lock (recordsGate)
        {
            var all = context.ReadState<Dictionary<string, AcpChatRecord>>(RecordsState, []);
            var key = RecordKey(workspaceId, chatId);
            all[key] = edit(all.GetValueOrDefault(key) ?? new AcpChatRecord(workspaceId, chatId));
            context.WriteState(RecordsState, all);
        }
    }

    private readonly ConcurrentDictionary<string, string> failures = new();

    public AcpAgentList ListAgents() => new(context.ReadState<List<AcpAgentEntry>>(AgentsState, []));

    /// <summary>The list with what is known to be wrong: a program that is not there, or a start that failed last time.</summary>
    public AcpAgentList WithProblems(AcpAgentList list)
    {
        var problems = new Dictionary<string, string>();
        foreach (var agent in list.Agents.Where(agent => agent.Enabled))
        {
            var program = agent.LaunchCommand();
            if (Path.IsPathRooted(program) && !File.Exists(program)) problems[agent.Id] = "The program is missing: " + program;
            else if (failures.TryGetValue(agent.Id, out var failure)) problems[agent.Id] = "The last start failed: " + failure;
        }
        return list with { Problems = problems };
    }

    public AcpAgentList AddAgent(AcpAgentEntry entry)
    {
        if (entry.Id.Length == 0 || entry.Command.Trim().Length == 0) throw new ArgumentException("An agent needs an id and a command.");
        lock (recordsGate)
        {
            var all = context.ReadState<List<AcpAgentEntry>>(AgentsState, []);
            all.RemoveAll(existing => existing.Id == entry.Id);
            all.Add(entry);
            context.WriteState(AgentsState, all);
            return new(all);
        }
    }

    public AcpAgentList RemoveAgent(string id)
    {
        lock (recordsGate)
        {
            var all = context.ReadState<List<AcpAgentEntry>>(AgentsState, []);
            if (all.FirstOrDefault(existing => existing.Id == id) is { Origin: "registry" } installed) DeleteManaged(installed);
            failures.TryRemove(id, out _);
            all.RemoveAll(existing => existing.Id == id);
            context.WriteState(AgentsState, all);
            return new(all);
        }
    }

    /// <summary>Removing a registry install also deletes its unpacked files, but only inside this plugin's agents directory.</summary>
    private void DeleteManaged(AcpAgentEntry entry)
    {
        try
        {
            if (!AcpIconRules.IsSafeId(entry.Id)) return;
            var root = Path.GetFullPath(Path.Combine(DataDirectory, "agents")) + Path.DirectorySeparatorChar;
            var directory = Path.GetFullPath(Path.Combine(DataDirectory, "agents", entry.Id));
            if (directory.StartsWith(root, StringComparison.Ordinal) && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { context.Log.Warn("could not delete the files of " + entry.Id + ": " + error.Message); }
    }

    private AcpChatSession Session(string workspaceId, string chatId) =>
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
        context.Method(AcpContract.Agents, (_, _, _) => ValueTask.FromResult(WithProblems(ListAgents())));
        context.Method(AcpContract.AddAgent, (p, _, _) => ValueTask.FromResult(WithProblems(AddAgent(p))));
        context.Method(AcpContract.Registry, async (p, _, ct) => (await Cache.LoadAsync(p.Refresh, ct)).View);
        context.Method(AcpContract.Icons, async (p, _, ct) => await IconsAsync(p, ct));
        context.Method(AcpContract.InstallPlan, async (p, _, ct) => await PlanAsync(p, ct));
        context.Method(AcpContract.Install, async (p, _, ct) => WithProblems(await InstallAsync(p, ct)));
        context.Method(AcpContract.RemoveAgent, (p, _, _) => ValueTask.FromResult(WithProblems(RemoveAgent(p.AgentId))));
        context.Method(AcpContract.Choose, (p, _, _) =>
        {
            SaveRecord(p.WorkspaceId, p.ChatId, record => record with { AgentId = p.AgentId });
            return ValueTask.FromResult(new Ack());
        });
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
        context.Method(ChatContract.SetPermissionMode, async (p, _, _) => await Session(p.WorkspaceId, p.ChatId).SetModeAsync(p.Value));
        context.Method(ChatContract.SetEffort, (_, _, _) => throw Unsupported("This agent exposes no effort levels."));
        context.Method(ChatContract.Answer, (p, _, _) =>
        {
            if (p.Allow) Session(p.WorkspaceId, p.ChatId).Answer(p.RequestId, true, p.Remember == true);
            else Session(p.WorkspaceId, p.ChatId).Answer(p.RequestId, false, false);
            return ValueTask.FromResult(new Ack());
        });
        context.Method(ChatContract.AnswerQuestion, (_, _, _) => throw Unsupported("ACP agents ask through permission requests."));
        context.Method(ChatContract.Dequeue, (_, _, _) => throw Unsupported("ACP has no queued messages."));
        context.Method(ChatContract.EditQueued, (_, _, _) => throw Unsupported("ACP has no queued messages."));
        context.Method(ChatContract.Rewind, (_, _, _) => throw Unsupported("ACP cannot rewind."));
        context.Method(ChatContract.RewindPreview, (_, _, _) => throw Unsupported("ACP cannot rewind."));
        context.Method(ChatContract.Fork, (_, _, _) => throw Unsupported("ACP cannot fork yet."));
        context.Method(ChatContract.StopTask, (_, _, _) => throw Unsupported("ACP has no background tasks."));
        context.Method(ChatContract.ItemDetail, (p, _, _) => ValueTask.FromResult(new ChatDetail(Session(p.WorkspaceId, p.ChatId).ItemDetail(p.ItemId) ?? "")));
        context.Method(ChatContract.Sessions, (_, _, _) => ValueTask.FromResult(new ChatSessionList([])));
        context.Method(ChatContract.Files, async (p, _, ct) => await FindFilesAsync(p, ct));
    }

    private static NotSupportedException Unsupported(string message) => new(message);

    public async Task DisposeAsync()
    {
        var all = sessions.Values.ToArray();
        sessions.Clear();
        await Task.WhenAll(all.Select(session => session.DisposeAsync().AsTask()));
    }

    private async ValueTask<ChatHeader> OpenAsync(ChatOpenParams p)
    {
        if (sessions.TryGetValue((p.WorkspaceId, p.ChatId), out var existing) && existing.Active) return existing.Header;
        var cwd = (await context.WorkspaceAsync(p.WorkspaceId, CancellationToken.None))?.Path ?? p.WorkspaceId;
        var record = await Task.Run(() => LoadRecord(p.WorkspaceId, p.ChatId));
        var entry = ListAgents().Agents.FirstOrDefault(agent => agent.Id == record?.AgentId) ?? throw new InvalidOperationException("Choose an ACP agent for this chat first.");
        var session = new AcpChatSession(p.WorkspaceId, p.ChatId, cwd, () => connect is null ? LaunchAsync(entry, cwd) : connect(entry, cwd), Publish, message => context.Log.Warn(message), new AcpClientMethods(cwd));
        sessions[(p.WorkspaceId, p.ChatId)] = session;
        if (existing is not null) await existing.DisposeAsync();
        var header = session.Begin();
        _ = Task.Run(async () =>
        {
            await session.StartAsync(p.ResumeSessionId ?? record?.SessionId);
            if (session.Header.Status == ChatStatus.Failed) failures[entry.Id] = session.Header.Error ?? "the agent did not start";
            else failures.TryRemove(entry.Id, out _);
            if (session.SessionId is { } id) SaveRecord(p.WorkspaceId, p.ChatId, current => current with { SessionId = id });
        });
        return header;
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

[JsonSerializable(typeof(Dictionary<string, AcpChatRecord>))]
[JsonSerializable(typeof(List<AcpAgentEntry>))]
internal sealed partial class AcpHostJson : JsonSerializerContext;