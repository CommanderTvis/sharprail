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

    private readonly ConcurrentDictionary<string, PluginCall> clientForWorkspace = new();
    private readonly ConcurrentDictionary<TerminalRef, int> claudePidOfTab = new();
    private readonly ConcurrentDictionary<TerminalRef, string> transcriptOfTab = new();
    private readonly ConcurrentDictionary<string, HostWorkspace> workspaces = new();
    private readonly StatusStore statuses = new();
    private readonly InterruptWatch interrupts = new();
    private readonly TranscriptUsage usage = new();
    private IdeBridge.IdeBridge? bridge;
    private Chat.ClaudeChatHost? chat;
    private AgentWatch? watch;
    private ContextUsageRunner? contextUsage;
    private FileSystemWatcher? claudeHomeWatch;

    internal IdeBridge.IdeBridge? Bridge => bridge;

    private readonly ModelListCache modelLists = new(ModelListCache.DefaultPath());
    private readonly PermissionModeProbe permissionModes = new();

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
        context.Method(ClaudeCodeContract.Account, async (p, _, cancellationToken) =>
        {
            // A launch another plugin governs runs under that source's account, so Claude's own account and cached usage are not read.
            if (context.AgentLaunchContributions("claude").Select(contribution => contribution.Account).OfType<AgentAccountSource>().FirstOrDefault() is { } governing)
                return new ClaudeAccount(false, []) { Governed = await governing.Read(cancellationToken) };
            return await ClaudeAccountReader.ReadAsync(Command(), p.Refresh == true);
        });
        context.Method(ClaudeCodeContract.ModelListGet, (_, _, _) => Done(modelLists.Get(Command())));
        context.Method(ClaudeCodeContract.ModelListPut, (p, _, _) => Done(modelLists.Put(Command(), p.Models)));
        context.Method(ClaudeCodeContract.PermissionModesGet, async (_, _, _) => await permissionModes.GetAsync(Command()));
        context.Method(ClaudeCodeContract.SystemPromptGet, (_, _, _) => Done(new SystemPromptFile(SystemPrompt.FilePath(), SystemPrompt.IgnoredEdits())));
        // The project's disable-list is the only way to shape the instructions; the generated files are never edited.
        InstructionSectionList WithEdits(InstructionSectionList list) => new()
        {
            Path = list.Path,
            Sections = list.Sections,
            UnknownDisabled = list.UnknownDisabled,
            Diagnostics = list.Diagnostics,
            IgnoredEditsPath = SystemPrompt.IgnoredEdits()
        };
        context.Method(ClaudeCodeContract.InstructionsList, async (p, _, ct) => WithEdits(await context.InstructionSectionsAsync(ClaudeCodeContract.Id, p.WorkspaceId, ct)));
        context.Method(ClaudeCodeContract.InstructionsSet, async (p, _, ct) => WithEdits(await context.SetInstructionSectionAsync(ClaudeCodeContract.Id, p.WorkspaceId, p.Id, p.Enabled, ct)));
        context.OnInstructionSectionsChanged(root => context.Publish(ClaudeCodeContract.InstructionsChanged, new InstructionSectionsChanged { ProjectRoot = root }));
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
        contextUsage = new ContextUsageRunner(
            (spec, timeout, cancellationToken) => Bounded.RunAsync(spec.Argv, timeout, spec.Cwd, spec.Environment, cancellationToken),
            TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(2),
            workspaceId => context.Publish(ClaudeCodeContract.ContextUsage, new ContextUsageChanged(workspaceId)));
        context.Method(ClaudeCodeContract.ContextUsageGet, async (p, call, ct) =>
        {
            var scope = await KnownScopeAsync(p.WorkspaceId, p.TabKey, ct);
            // Project files reach the baseline through the workspace watch; a failure to start it only loses that trigger.
            _ = context.WatchWorkspaceAsync(p.WorkspaceId, CancellationToken.None).AsTask()
                .ContinueWith(task => context.Log.Warn("could not watch the workspace for context changes: " + task.Exception?.GetBaseException().Message),
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return contextUsage.Get(ContextSpec(p.WorkspaceId, p.TabKey, scope), p.Refresh == true);
        });
        context.OnFilesChanged(change => contextUsage.Changed(
            [.. change.Paths.Select(path => Path.GetFullPath(AbsoluteIn(change.WorkspaceId, path)))], change.WorkspaceId, change.Truncated));
        // User-level settings and instructions live outside every workspace, so their directory is watched on its own.
        try
        {
            claudeHomeWatch = new FileSystemWatcher(ClaudeEnvironment.ClaudeHome()) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, change) => contextUsage.Changed([change.FullPath]);
            claudeHomeWatch.Changed += changed;
            claudeHomeWatch.Created += changed;
            claudeHomeWatch.Deleted += changed;
            claudeHomeWatch.Renamed += (_, change) => contextUsage.Changed([change.FullPath, change.OldFullPath]);
            claudeHomeWatch.EnableRaisingEvents = true;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            context.Log.Warn("could not watch Claude Code's configuration directory: " + error.Message);
        }
        context.ExternalFiles(workspaceId =>
        {
            IEnumerable<ConfigScope> scopes = [ScopeOf(workspaceId), .. context.Terminals()
                .Where(terminal => terminal.Terminal.WorkspaceId == workspaceId).Select(terminal => ScopeOf(workspaceId, terminal.Terminal.TabKey))];
            return [.. scopes.SelectMany(scope => ClaudeResolver.FilePaths(workspaceId, scope.Root, scope.PromptFiles)).Append(SystemPrompt.FilePath()).Distinct()];
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
        context.Method(ClaudeCodeContract.AtMention, async (p, _, _) =>
            new IdeAtMentionResult(bridge is null ? 0 : await bridge.AtMentioned(p with { Path = AbsoluteIn(p.WorkspaceId, p.Path) })));
        context.Method(ClaudeCodeContract.ActionReply, (p, _, _) =>
        {
            bridge?.Settle(p);
            return Done(new Ack());
        });
        context.Method(ClaudeCodeContract.StatusSnapshot, (p, call, _) =>
        {
            // A client that draws a workspace's Claude terminals can act for its sessions before any editor reported a selection.
            clientForWorkspace.TryAdd(p.WorkspaceId, call);
            return Done(statuses.Snapshot(p.WorkspaceId));
        });

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

        void WritePrompt()
        {
            try { SystemPrompt.Write(context.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.ToolList), context.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.Section)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                context.Log.Warn("could not write the system prompt: " + error.Message);
            }
        }
        WritePrompt();

        Dictionary<string, string> LaunchEnvironment(TerminalRef terminal)
        {
            // Plugins enabled or disabled since the last session change what the file says.
            WritePrompt();
            var promptFile = SystemPrompt.FilePath();
            try
            {
                promptFile = SystemPrompt.WriteFor(terminal.WorkspaceId, context.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.ToolList, terminal.WorkspaceId),
                    context.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.Section, terminal.WorkspaceId));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                context.Log.Warn("could not write the workspace's system prompt: " + error.Message);
            }
            var environment = new Dictionary<string, string>
            {
                [ClaudeCodeContract.PromptFileVariable] = promptFile,
                [ClaudeCodeContract.PluginDirectoryVariable] = HookPluginDirectory
            };
            if (context.Projects().FirstOrDefault(project => project.Path == ProjectOf(terminal.WorkspaceId)) is { } project)
            {
                environment[ClaudeCodeContract.WorktreesVariable] = project.WorktreesDirectory;
                if (project.ScratchDirectory.Length > 0 && Path.GetFileName(terminal.WorkspaceId.TrimEnd('/', '\\')) is { Length: > 0 } leaf)
                {
                    var scratch = Path.Combine(project.ScratchDirectory, leaf);
                    try { Directory.CreateDirectory(scratch); environment[ClaudeCodeContract.ScratchVariable] = scratch; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { context.Log.Warn("could not create the scratch directory: " + error.Message); }
                }
            }
            try { environment[ClaudeCodeContract.StatusUrlVariable] = $"{context.PublicBaseUrl()}/status/{context.TerminalToken(terminal)}"; }
            catch (InvalidOperationException) { }
            if (bridge?.Port is { } port) environment[IdeBridge.IdeBridge.SsePortVariable] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return environment;
        }
        context.TerminalEnvironment(LaunchEnvironment);
        chat = new Chat.ClaudeChatHost(context, LaunchEnvironment, LoopbackRoot, PublishStatus, HookPluginDirectory);
        chat.Register();

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
                var existing = context.AgentRecord(terminal);
                // A new process that names its session (SharpRail's --session-id, or --resume with an id) is in that
                // session from its first moment, before any hook reports; hooks then follow /clear, /resume and forks.
                context.SetAgentRecord(terminal, new TerminalAgentRecord("claude", command)
                {
                    SessionId = AgentResume.LaunchedSessionId(command) ?? existing?.SessionId,
                    Model = existing?.Model,
                    Effort = existing?.Effort,
                    LaunchedByUi = ProcessTree.CaptureUiLaunch(agentPid)
                });
            });

        context.OnTerminal(change =>
        {
            if (change is TerminalSpawned) watch.Poke();
            else if (change is TerminalPromptChanged { Foreground: true } prompt && context.AgentRecord(prompt.Terminal)?.Kind == "claude" && !StartedByAnotherAgent(prompt.Terminal))
            {
                // Claude's own OSC 133 marks say whether it waits at its prompt; they win over guessing, while the hook's
                // Blocked, Done and Failed stay until it runs again. Without marks only the hook reports.
                var last = statuses.Snapshot(prompt.Terminal.WorkspaceId).FirstOrDefault(push => push.TabKey == prompt.Terminal.TabKey)?.Status;
                var next = prompt.AtPrompt ? ClaudeCodeStatus.Idle : ClaudeCodeStatus.Running;
                if (last != next && !(prompt.AtPrompt && last is ClaudeCodeStatus.Blocked or ClaudeCodeStatus.Done or ClaudeCodeStatus.Failed))
                    PublishStatus(new ClaudeCodeStatusPush(prompt.Terminal.WorkspaceId, prompt.Terminal.TabKey,
                        new AgentStatusReport(prompt.AtPrompt ? "prompt_mark" : "command_mark"), next));
            }
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
            // A tab that knows its session resumes exactly that one, never the directory's latest; a session that never
            // saved a transcript starts afresh under the same id. Only a legacy record without an id continues.
            var offer = record.SessionId is { } id && AgentResume.IsSessionId(id)
                ? AgentResume.SessionExists(terminal.WorkspaceId, id)
                    ? AgentResume.ResumeCommand(record.Command, id, record.Model, record.Effort, HookPluginDirectory)
                    : AgentResume.StartCommand(record.Command, id, record.Model, record.Effort, HookPluginDirectory)
                : AgentResume.ContinueCommand(record.Command, record.Model, record.Effort, HookPluginDirectory);
            // Start fresh on the resume offer: the same invocation under a new id, so the new session takes its own lock.
            var fresh = AgentResume.StartCommand(record.Command, Guid.NewGuid().ToString(), record.Model, record.Effort, HookPluginDirectory);
            if (context.AgentLaunchContributions("claude") is { Count: > 0 } contributions)
            {
                if (offer is not null) offer = AgentLaunchComposition.Apply(offer, "claude", contributions, OperatingSystem.IsWindows());
                if (fresh is not null) fresh = AgentLaunchComposition.Apply(fresh, "claude", contributions, OperatingSystem.IsWindows());
            }
            // The revived terminal runs the agent as its program; a Windows host types it as an offer instead.
            if (offer is null || OperatingSystem.IsWindows()) return offer is null ? null : new RevivePrefill(offer) { Program = true, Fresh = fresh };
            // A revival is SharpRail's own launch, whatever started the session it resumes. The command comes from the
            // process table, which holds no environment, so the agent-view setting is applied again rather than carried.
            var agentView = context.Settings<ClaudeCodeSettings>().DisableAgentView ? "CLAUDE_CODE_DISABLE_AGENT_VIEW=true " : "";
            return new RevivePrefill("SHARPRAIL_UI_LAUNCH=claude " + agentView + offer)
            { Program = true, Fresh = fresh is null ? null : "SHARPRAIL_UI_LAUNCH=claude " + agentView + fresh };
        });
    }

    // A fresh session's baseline, measured where and how this tab's session started: its directory, its context-shaping
    // flags (or, with no Claude in the tab, the flags SharpRail's launcher would pass), and the same SharpRail MCP address.
    private ContextRunSpec ContextSpec(string workspaceId, string? tabKey, ConfigScope scope)
    {
        var settings = context.Settings<ClaudeCodeSettings>();
        List<string> flags;
        if (tabKey is not null && context.AgentRecord(new(workspaceId, tabKey)) is { Kind: "claude" } record)
            flags = [.. ContextLaunch.Flags(record.Command, scope.Root)];
        else
        {
            flags = [];
            if (settings.AppendSystemPrompt)
            {
                var promptFile = SystemPrompt.FilePath();
                try
                {
                    promptFile = SystemPrompt.WriteFor(workspaceId, context.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.ToolList, workspaceId),
                        context.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.Section, workspaceId));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                flags.AddRange(["--append-system-prompt-file", promptFile]);
            }
            flags.AddRange(["--plugin-dir", HookPluginDirectory]);
            if (settings.Model is { Length: > 0 } model) flags.AddRange(["--model", model]);
            if (settings.Effort is { Length: > 0 } effort) flags.AddRange(["--effort", effort]);
        }
        var environment = new Dictionary<string, string>
        {
            ["THINKRAIL_MCP_URL"] = $"{LoopbackRoot()}/mcp/{context.TerminalToken(new(workspaceId, McpListProbeTabKey))}",
            // The measuring run must not report status as if it were the tab's session.
            [ClaudeCodeContract.StatusUrlVariable] = ""
        };
        if (settings.DisableAgentView) environment["CLAUDE_CODE_DISABLE_AGENT_VIEW"] = "true";
        var home = ClaudeEnvironment.ClaudeHome();
        var inputs = ClaudeResolver.FilePaths(workspaceId, scope.Root, scope.PromptFiles).ToList();
        string[] flagFiles = ["--append-system-prompt-file", "--system-prompt-file", "--settings", "--mcp-config", "--plugin-dir"];
        for (var index = 0; index + 1 < flags.Count; index++)
            if (flagFiles.Contains(flags[index])) inputs.Add(Path.GetFullPath(flags[index + 1], scope.Root));
        inputs.AddRange([Path.Combine(home, "settings.json"), Path.Combine(home, "settings.local.json"), Path.Combine(home, "CLAUDE.md"),
            Path.Combine(scope.Root, ".mcp.json"), Path.Combine(scope.Root, ".claude", "settings.json"), Path.Combine(scope.Root, ".claude", "settings.local.json")]);
        return new(workspaceId, ClaudeCommands.ClaudeBinary(Command()), scope.Root, flags, environment, [.. inputs.Distinct()]);
    }

    private string HookPluginDirectory => Path.Combine(context.AssetsDirectory ??
        Path.Combine(AppContext.BaseDirectory, "plugins", ClaudeCodeContract.Id, "assets"), "marketplace", "claude-plugin");

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
        if (report.SessionId is { Length: > 0 } || report.Model is { Length: > 0 } || report.Effort is { Length: > 0 })
        {
            var existing = context.AgentRecord(owner);
            var changedSession = report.SessionId is { Length: > 0 } reportedSession && existing?.SessionId != reportedSession;
            var next = (existing ?? new TerminalAgentRecord("claude", "claude")) with
            {
                SessionId = report.SessionId is { Length: > 0 } ? report.SessionId : existing?.SessionId,
                Model = report.Model is { Length: > 0 } ? report.Model : changedSession ? null : existing?.Model,
                Effort = report.Effort is { Length: > 0 } ? report.Effort : changedSession ? null : existing?.Effort
            };
            if (next != existing) context.SetAgentRecord(owner, next);
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

    public async ValueTask DisposeAsync()
    {
        if (chat is not null) await chat.DisposeAsync();
        watch?.Stop();
        claudeHomeWatch?.Dispose();
        contextUsage?.Dispose();
        interrupts.StopAll();
        bridge?.Stop();
        clientForWorkspace.Clear();
        claudePidOfTab.Clear();
    }
}