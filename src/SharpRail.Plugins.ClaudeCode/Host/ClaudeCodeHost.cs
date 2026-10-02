using System.Collections.Concurrent;
using System.Text;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

namespace SharpRail.Plugins.ClaudeCode.Host;

/// <summary>
/// The Claude Code plugin's host half: the configuration methods, the IDE bridge, the status route the hook plugin posts
/// to, the process poll that writes the agent record, the revive prefill and the terminal environment.
/// </summary>
public sealed class ClaudeCodeHost : PluginHostModule
{
    public override PluginContract Contract => ClaudeCodeContract.Contract;

    public override async ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var activation = new ClaudeCodeActivation(context);
        await activation.StartAsync();
        return activation.DisposeAsync;
    }
}

internal sealed class ClaudeCodeActivation(IPluginHostContext context)
{
    // Not a terminal tab id, so it can never be a real tab's key.
    private const string McpListProbeTabKey = "claude-config-mcp-probe";

    private readonly HookPlugin hookPlugin = new(context.AssetsDirectory);
    private readonly ConcurrentDictionary<string, PluginCall> clientForWorkspace = new();
    private readonly ConcurrentDictionary<TerminalRef, int> claudePidOfTab = new();
    private readonly ConcurrentDictionary<TerminalRef, string> transcriptOfTab = new();
    private readonly ConcurrentDictionary<string, HostWorkspace> workspaces = new();
    private readonly StatusStore statuses = new();
    private readonly InterruptWatch interrupts = new();
    private readonly TranscriptUsage usage = new();
    private IdeBridge.IdeBridge? bridge;
    private AgentWatch? watch;

    internal IdeBridge.IdeBridge? Bridge => bridge;

    private string Command() => context.Settings<ClaudeCodeSettings>().Command.Trim() is { Length: > 0 } command ? command : "claude";

    private sealed record ConfigScope(string Root, IReadOnlyList<string> PromptFiles);

    // What a session in this tab loads depends on where it is now and how it was started, so the root is the claude
    // process's own directory, else the one its hook last reported, else the workspace root.
    private ConfigScope ScopeOf(string workspaceId, string? tabKey = null)
    {
        if (tabKey is null) return new(workspaceId, []);
        var terminal = new TerminalRef(workspaceId, tabKey);
        if (context.AgentRecord(terminal) is not { Kind: "claude" } record) return new(workspaceId, []);
        var pid = claudePidOfTab.TryGetValue(terminal, out var found) ? found : 0;
        var reported = statuses.Snapshot(workspaceId).FirstOrDefault(push => push.TabKey == tabKey)?.Report.Cwd;
        var root = (pid > 0 ? ProcessTree.CaptureCwd(pid) : null) ?? (reported is { Length: > 0 } ? reported : null) ?? workspaceId;
        return new(root, SessionContext.AppendedPromptFiles(record.Command, root));
    }

    private async Task<ConfigScope> KnownScopeAsync(string workspaceId, string? tabKey, CancellationToken cancellationToken)
    {
        if (!workspaces.ContainsKey(workspaceId))
        {
            if (await context.WorkspaceAsync(workspaceId, cancellationToken) is not { } workspace) throw new InvalidOperationException($"Unknown workspace: {workspaceId}");
            workspaces[workspace.Id] = workspace;
        }
        return ScopeOf(workspaceId, tabKey);
    }

    private static string AbsoluteIn(string workspaceId, string path) => Path.IsPathRooted(path) ? path : Path.Combine(workspaceId, path);

    // The loopback server's root: the plugin's base URL names its own route under it.
    private string LoopbackRoot()
    {
        var url = context.PublicBaseUrl();
        var route = PluginIdentity.Route(context.Id);
        return url.EndsWith(route, StringComparison.Ordinal) ? url[..^route.Length] : url;
    }

    private bool StartedByAnotherAgent(TerminalRef terminal)
    {
        if (context.AgentRecord(terminal) is not { } record || record.Kind == "claude") return false;
        if (context.Terminals().FirstOrDefault(item => item.Terminal == terminal)?.Pid is not { } pid) return false;
        return ProcessTree.Capture() is { } snapshot && ProcessTree.RunsInsideAgent(snapshot, pid, "claude", record.Kind);
    }

    private static ValueTask<T> Done<T>(T value) => ValueTask.FromResult(value);

    public async Task StartAsync()
    {
        foreach (var workspace in await context.WorkspacesAsync()) workspaces[workspace.Id] = workspace;

        context.Method(ClaudeCodeContract.ConfigGet, async (p, _, ct) =>
        {
            var scope = await KnownScopeAsync(p.WorkspaceId, p.TabKey, ct);
            return ClaudeResolver.Resolve(p.WorkspaceId, scope.Root, scope.PromptFiles);
        });
        context.Method(ClaudeCodeContract.Account, async (p, _, _) => await ClaudeAccountReader.ReadAsync(Command(), p.Refresh == true));
        context.Method(ClaudeCodeContract.PluginStatus, async (_, _, _) => await hookPlugin.StatusMaintainedAsync(Command()));
        context.Method(ClaudeCodeContract.InstallPlugin, async (_, _, _) => await hookPlugin.InstallAsync(Command()));
        context.Method(ClaudeCodeContract.PluginUninstallPlan, (p, _, _) => Done(new CommandPlan(ClaudeCommands.PluginUninstall(Command(), p.Name, p.Scope))));
        context.Method(ClaudeCodeContract.PluginUninstall, async (p, _, ct) =>
            await ClaudeCommands.UninstallAsync(Command(), p.Name, p.Scope, (await KnownScopeAsync(p.WorkspaceId, p.TabKey, ct)).Root));
        context.Method(ClaudeCodeContract.PluginMovePlan, (p, _, _) => Done(new CommandsPlan(ClaudeCommands.PluginMove(Command(), p.Name, p.From, p.To))));
        context.Method(ClaudeCodeContract.PluginMove, async (p, _, ct) =>
            await ClaudeCommands.MoveAsync(Command(), p.Name, p.From, p.To, (await KnownScopeAsync(p.WorkspaceId, p.TabKey, ct)).Root));
        context.Method(ClaudeCodeContract.MarketplacePlan, (p, _, _) => Done(new CommandPlan(ClaudeCommands.Marketplace(Command(), p.Action))));
        context.Method(ClaudeCodeContract.MarketplaceRun, async (p, _, ct) =>
            await ClaudeCommands.RunMarketplaceAsync(Command(), p.Action, (await KnownScopeAsync(p.WorkspaceId, p.TabKey, ct)).Root));
        context.Method(ClaudeCodeContract.McpList, async (p, _, ct) =>
        {
            var scope = await KnownScopeAsync(p.WorkspaceId, p.TabKey, ct);
            var declared = ClaudeResolver.Resolve(p.WorkspaceId, scope.Root, scope.PromptFiles).Capabilities
                .Where(item => item.Kind == ClaudeCapabilityKind.Mcp).Select(item => item.Name).ToHashSet();
            // The health check reports SharpRail's own MCP server broken unless it carries the token a real terminal would.
            var probe = $"{LoopbackRoot()}/mcp/{context.TerminalToken(new(p.WorkspaceId, McpListProbeTabKey))}";
            var entries = await McpList.ListAsync(Command(), scope.Root, new Dictionary<string, string> { ["THINKRAIL_MCP_URL"] = probe });
            return new McpListResult(McpList.Capabilities(entries, declared, scope.Root));
        });
        context.ExternalFiles(workspaceId =>
        {
            IEnumerable<ConfigScope> scopes = [ScopeOf(workspaceId), .. context.Terminals()
                .Where(terminal => terminal.Terminal.WorkspaceId == workspaceId).Select(terminal => ScopeOf(workspaceId, terminal.Terminal.TabKey))];
            return [.. scopes.SelectMany(scope => ClaudeResolver.FilePaths(workspaceId, scope.Root, scope.PromptFiles)).Distinct()];
        });
        context.Method(ClaudeCodeContract.ReadFile, async (p, _, ct) =>
        {
            var scope = await KnownScopeAsync(p.WorkspaceId, p.TabKey, ct);
            return ClaudeResolver.ReadFile(p.WorkspaceId, scope.Root, p.Path, scope.PromptFiles);
        });
        context.Method(ClaudeCodeContract.WriteFile, async (p, _, ct) =>
        {
            var scope = await KnownScopeAsync(p.WorkspaceId, p.TabKey, ct);
            return ClaudeResolver.WriteFile(p.WorkspaceId, scope.Root, p.Path, p.Content, p.BaseHash, scope.PromptFiles);
        });
        context.Method(ClaudeCodeContract.PlanEdit, async (p, _, ct) =>
            ClaudeEdits.Plan(p.WorkspaceId, p.Scope, p.Edit, (await KnownScopeAsync(p.WorkspaceId, p.TabKey, ct)).Root));
        context.Method(ClaudeCodeContract.ApplyEdit, async (p, _, ct) =>
            ClaudeEdits.Apply(p.WorkspaceId, p.Scope, p.Edit, p.BaseHash, (await KnownScopeAsync(p.WorkspaceId, p.TabKey, ct)).Root));

        context.Method(ClaudeCodeContract.SelectionChanged, (p, call, _) =>
        {
            clientForWorkspace[p.WorkspaceId] = call;
            bridge?.SelectionChanged(p with { Path = AbsoluteIn(p.WorkspaceId, p.Path) });
            return Done(new Ack());
        });
        context.Method(ClaudeCodeContract.DocumentClosed, (p, call, _) =>
        {
            clientForWorkspace[p.WorkspaceId] = call;
            bridge?.DocumentClosed(p with { Path = AbsoluteIn(p.WorkspaceId, p.Path) });
            return Done(new Ack());
        });
        context.Method(ClaudeCodeContract.ActionReply, (p, _, _) =>
        {
            bridge?.Settle(p);
            return Done(new Ack());
        });
        context.Method(ClaudeCodeContract.StatusSnapshot, (p, _, _) => Done(statuses.Snapshot(p.WorkspaceId)));

        bridge = new IdeBridge.IdeBridge(new(
            request =>
            {
                // Addressed, not broadcast: the client that reported this workspace's editor owns the action.
                if (!clientForWorkspace.TryGetValue(request.WorkspaceId, out var client)) throw new InvalidOperationException("No SharpRail client is connected");
                context.Publish(ClaudeCodeContract.IdeAction, request, client);
            },
            () => [.. workspaces.Values.Select(workspace => workspace.Path).Distinct()],
            context.WorkspaceForProcess));
        try { bridge.Start(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
        {
            context.Log.Warn("could not start the IDE bridge: " + error.Message);
        }

        context.Route(Route);

        try { SystemPrompt.Write(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            context.Log.Warn("could not write the system prompt: " + error.Message);
        }

        context.TerminalEnvironment(terminal =>
        {
            var environment = new Dictionary<string, string> { [ClaudeCodeContract.PromptFileVariable] = SystemPrompt.FilePath() };
            if (ProjectOf(terminal.WorkspaceId) is { } project) environment[ClaudeCodeContract.WorktreesVariable] = SystemPrompt.WorktreesDirectoryOf(project);
            try { environment[ClaudeCodeContract.StatusUrlVariable] = $"{context.PublicBaseUrl()}/status/{context.TerminalToken(terminal)}"; }
            catch (InvalidOperationException) { }
            if (bridge?.Port is { } port) environment[IdeBridge.IdeBridge.SsePortVariable] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return environment;
        });

        watch = new AgentWatch(
            () => [.. context.Terminals().Where(terminal => terminal.Pid is not null)
                .Select(terminal => new AgentWatchTarget(terminal.Terminal.WorkspaceId, terminal.Terminal.TabKey, terminal.Pid!.Value))],
            _ => { },
            (workspaceId, tabKey) =>
            {
                claudePidOfTab.TryRemove(new(workspaceId, tabKey), out _);
                context.SetAgentRecord(new(workspaceId, tabKey), null);
            },
            (workspaceId, tabKey, agentPid) =>
            {
                var terminal = new TerminalRef(workspaceId, tabKey);
                if (StartedByAnotherAgent(terminal) || ProcessTree.CaptureCommand(agentPid) is not { } command) return;
                claudePidOfTab[terminal] = agentPid;
                context.SetAgentRecord(terminal, new TerminalAgentRecord("claude", command) { SessionId = context.AgentRecord(terminal)?.SessionId });
            });

        context.OnTerminal(change =>
        {
            if (change is TerminalSpawned) watch.Poke();
            else if (change is TerminalClosed or TerminalAgentChanged { Record: null })
            {
                watch.Forget(change.Terminal.WorkspaceId, change.Terminal.TabKey);
                ForgetTab(change.Terminal);
            }
        });

        context.OnWorkspace(change =>
        {
            if (change is WorkspaceCreated created) workspaces[created.Workspace.Id] = created.Workspace;
            else if (change is WorkspaceRemoved removed) workspaces.TryRemove(removed.Id, out _);
            if (change is not WorkspaceUpdated) bridge?.RefreshWorkspaces();
        });

        context.RevivePrefill((terminal, record) =>
        {
            if (record.Kind != "claude") return null;
            var sessionId = record.SessionId is { } id && AgentResume.SessionExists(terminal.WorkspaceId, id) ? id : null;
            var offer = sessionId is not null ? AgentResume.ResumeCommand(record.Command, sessionId) : AgentResume.ContinueCommand(record.Command);
            return offer is null ? null : new RevivePrefill(offer);
        });
    }

    private string? ProjectOf(string workspaceId)
    {
        if (workspaces.TryGetValue(workspaceId, out var workspace))
            return context.Projects().FirstOrDefault(project => project.Id == workspace.ProjectId)?.Path;
        return context.Projects().FirstOrDefault(project => project.Path == workspaceId)?.Path;
    }

    private void ForgetTab(TerminalRef terminal)
    {
        if (transcriptOfTab.TryRemove(terminal, out var transcript) && !transcriptOfTab.Values.Contains(transcript)) usage.Forget(transcript);
        interrupts.Stop(Key(terminal));
        claudePidOfTab.TryRemove(terminal, out _);
        statuses.Forget(terminal.WorkspaceId, terminal.TabKey);
    }

    private static string Key(TerminalRef terminal) => terminal.WorkspaceId + "\0" + terminal.TabKey;

    private void PublishStatus(ClaudeCodeStatusPush push)
    {
        statuses.Record(push);
        context.Publish(ClaudeCodeContract.Status, push);
    }

    private static PluginHttpResponse Text(int status, string body) => new(status) { Body = Encoding.UTF8.GetBytes(body) };

    // The hook plugin posts each Claude Code lifecycle event here, at the address stamped into the terminal.
    private ValueTask<PluginHttpResponse> Route(PluginHttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.Subpath.StartsWith("status/", StringComparison.Ordinal)) return ValueTask.FromResult(Text(404, "not found"));
        if (request.Method != "POST") return ValueTask.FromResult(Text(405, "method not allowed"));
        if (context.TerminalForToken(request.Subpath["status/".Length..]) is not { } owner) return ValueTask.FromResult(Text(404, "unknown terminal"));
        if (StatusReports.Parse(request.Body.Span) is not { } delivery || StartedByAnotherAgent(owner)) return ValueTask.FromResult(Text(200, "ignored"));

        var report = delivery.Report;
        if (report.SessionId is { Length: > 0 } reportedSession)
        {
            var existing = context.AgentRecord(owner);
            if (existing?.SessionId != reportedSession)
                context.SetAgentRecord(owner, (existing ?? new TerminalAgentRecord("claude", "claude")) with { SessionId = reportedSession });
        }

        var sessionId = report.SessionId is { Length: > 0 } session ? session : null;
        string? Locate() => report.TranscriptPath is { Length: > 0 } reported ? reported : sessionId is null ? null : AgentResume.TranscriptPath(report.Cwd ?? "", sessionId);
        var transcript = Locate();
        if (transcript is not null) transcriptOfTab[owner] = transcript;
        var push = new ClaudeCodeStatusPush(owner.WorkspaceId, owner.TabKey, report, delivery.Status) { Usage = transcript is null ? null : usage.Read(transcript) };
        PublishStatus(push);

        if (delivery.Status == ClaudeCodeStatus.Running && sessionId is not null)
            interrupts.Track(Key(owner), Locate, () => PublishStatus(push with
            {
                Status = ClaudeCodeStatus.Idle,
                Report = new AgentStatusReport("interrupted") { SessionId = sessionId, Cwd = report.Cwd, Project = report.Project }
            }));
        else if (delivery.Status is not null) interrupts.Stop(Key(owner));
        return ValueTask.FromResult(Text(200, "ok"));
    }

    public ValueTask DisposeAsync()
    {
        watch?.Stop();
        interrupts.StopAll();
        bridge?.Stop();
        clientForWorkspace.Clear();
        claudePidOfTab.Clear();
        return ValueTask.CompletedTask;
    }
}