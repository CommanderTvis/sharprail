using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Codex.Host;

/// <summary>
/// The Codex plugin's host half: configuration methods, the hook status route and its pushes, the process poll that
/// writes the agent record, the revive prefill, the account and model reads, and the IDE-context provider.
/// </summary>
public sealed class CodexHost : PluginHostModule
{
    public override PluginContract Contract => CodexContract.Contract;

    private static string CommandOf(IPluginHostContext context) =>
        context.Settings<CodexSettings>().Command.Trim() is { Length: > 0 } command ? command : "codex";

    private static async ValueTask<string> WorktreeOf(IPluginHostContext context, string workspaceId, CancellationToken cancellationToken) =>
        (await context.WorkspaceAsync(workspaceId, cancellationToken))?.Path ?? throw new InvalidOperationException($"Unknown workspace: {workspaceId}");

    private static string TabIndex(TerminalRef terminal) => terminal.WorkspaceId + "\n" + terminal.TabKey;

    // A launch line that tells Codex it runs in SharpRail, for a record whose session turned out to have that prompt.
    private static string WithPrompt(string command) =>
        CodexLaunch.HasSharpRailPrompt(command) ? command : CodexLaunch.Line(command, new(false, false) { AppendSystemPrompt = true });

    // A Codex that another agent started (Claude Code, say) inherits the tab's status URL; the tab stays that agent's.
    private static bool StartedByAnotherAgent(IPluginHostContext context, TerminalRef terminal)
    {
        var record = context.AgentRecord(terminal);
        if (record is null || record.Kind == CodexManifest.Id) return false;
        var pid = context.Terminals().FirstOrDefault(item => item.Terminal == terminal)?.Pid;
        return pid is { } shell && ProcessSnapshot.Capture() is { } snapshot && snapshot.RunsInsideAgent(shell, CodexManifest.Id, record.Kind);
    }

    public override async ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var ide = new CodexIdeProvider(context);
        var statuses = new CodexStatusStore();
        var rollouts = new CodexRolloutReader();
        var rolloutOfTab = new Dictionary<string, string>();
        var codexPidOfTab = new ConcurrentDictionary<string, int>();
        var workspaces = new ConcurrentDictionary<string, HostWorkspace>();
        foreach (var workspace in await context.WorkspacesAsync()) workspaces[workspace.Id] = workspace;
        context.OnWorkspace(change =>
        {
            if (change is WorkspaceCreated created) workspaces[created.Workspace.Id] = created.Workspace;
            else if (change is WorkspaceRemoved removed) workspaces.TryRemove(removed.Id, out _);
        });
        var gate = new Lock();
        var command = CommandOf(context);
        var account = new CodexAccountReader(() => CommandOf(context));
        var models = new CodexModelReader(() => CommandOf(context));
        context.OnSettings<CodexSettings>(changed =>
        {
            var next = CommandOf(context);
            lock (gate)
            {
                if (next == command) return;
                command = next;
                _ = account.StopAsync();
                _ = models.StopAsync();
                account = new CodexAccountReader(() => CommandOf(context));
                models = new CodexModelReader(() => CommandOf(context));
            }
        });
        context.Method(CodexContract.Account, async (_, _, cancellationToken) =>
        {
            // A launch another plugin governs runs under that source's account, so the OpenAI account is not even read.
            if (context.AgentLaunchContributions(CodexManifest.Id).Select(contribution => contribution.Account).OfType<AgentAccountSource>().FirstOrDefault() is { } governing)
                return new CodexAccount(false, false, []) { Governed = await governing.Read(cancellationToken) };
            return await account.ReadAsync();
        });
        context.Method(CodexContract.Models, async (_, _, _) => await models.ReadAsync());

        // A Codex tab's configuration follows its process CWD, then its last reported one; otherwise the worktree's.
        string ScopeOf(string worktree, string workspaceId, string? tabKey)
        {
            if (tabKey is null || context.AgentRecord(new(workspaceId, tabKey))?.Kind != CodexManifest.Id) return worktree;
            return (codexPidOfTab.TryGetValue(TabIndex(new(workspaceId, tabKey)), out var pid) ? ProcessSnapshot.CaptureCwd(pid) : null) ??
                statuses.Snapshot(workspaceId).FirstOrDefault(push => push.TabKey == tabKey)?.Cwd ?? worktree;
        }
        CodexConfigSnapshot SnapshotOf(string worktree, string workspaceId, string? tabKey)
        {
            var record = tabKey is null ? null : context.AgentRecord(new(workspaceId, tabKey));
            IReadOnlyList<string> promptFiles = record is { Kind: CodexManifest.Id } && CodexLaunch.HasSharpRailPrompt(record.Command) ? [CodexSystemPrompt.FilePath()] : [];
            return CodexConfig.Resolve(worktree, ScopeOf(worktree, workspaceId, tabKey), promptFiles);
        }

        context.ExternalFiles(workspaceId =>
        {
            var worktree = workspaces.TryGetValue(workspaceId, out var workspace) ? workspace.Path : workspaceId;
            var snapshots = context.Terminals().Where(item => item.Terminal.WorkspaceId == workspaceId)
                .Select(item => SnapshotOf(worktree, workspaceId, item.Terminal.TabKey)).Prepend(SnapshotOf(worktree, workspaceId, null));
            return [.. snapshots.SelectMany(snapshot => snapshot.Layers.Select(layer => layer.Path).Concat(snapshot.Instructions.Select(file => file.Path)))
                .Append(CodexSystemPrompt.FilePath()).Distinct()];
        });
        context.Method(CodexContract.ConfigGet, async (parameters, _, token) =>
            SnapshotOf(await WorktreeOf(context, parameters.WorkspaceId, token), parameters.WorkspaceId, parameters.TabKey));
        context.Method(CodexContract.SetValue, async (parameters, _, token) =>
        {
            var worktree = await WorktreeOf(context, parameters.WorkspaceId, token);
            CodexConfig.WriteValue(worktree, parameters.Scope, parameters.KeyPath, parameters.Value is { ValueKind: not JsonValueKind.Null } value ? value : null);
            return CodexConfig.Resolve(worktree);
        });
        context.Method(CodexContract.CreateInstructions, async (parameters, _, token) =>
            CodexConfig.CreateInstructions(await WorktreeOf(context, parameters.WorkspaceId, token), parameters.Target));
        context.Method(CodexContract.InstallHooks, async (parameters, _, token) =>
        {
            CodexConfig.InstallHooks();
            return SnapshotOf(await WorktreeOf(context, parameters.WorkspaceId, token), parameters.WorkspaceId, parameters.TabKey);
        });
        context.Method(CodexContract.SystemPromptGet, (_, _, _) =>
            ValueTask.FromResult(new CodexSystemPromptFile(CodexSystemPrompt.FilePath(), CodexSystemPrompt.IgnoredEdits())));
        // The project's disable-list is the only way to shape the instructions; the generated text is never edited.
        InstructionSectionList WithEdits(InstructionSectionList list) => new()
        {
            Path = list.Path,
            Sections = list.Sections,
            UnknownDisabled = list.UnknownDisabled,
            Diagnostics = list.Diagnostics,
            IgnoredEditsPath = CodexSystemPrompt.IgnoredEdits()
        };
        context.Method(CodexContract.InstructionsList, async (p, _, ct) => WithEdits(await context.InstructionSectionsAsync(CodexManifest.Id, p.WorkspaceId, ct)));
        context.Method(CodexContract.InstructionsSet, async (p, _, ct) => WithEdits(await context.SetInstructionSectionAsync(CodexManifest.Id, p.WorkspaceId, p.Id, p.Enabled, ct)));
        context.OnInstructionSectionsChanged(root => context.Publish(CodexContract.InstructionsChanged, new InstructionSectionsChanged { ProjectRoot = root }));
        context.Method(CodexContract.StatusSnapshot, (parameters, _, _) => ValueTask.FromResult(statuses.Snapshot(parameters.WorkspaceId)));

        context.Route((request, _) =>
        {
            if (!request.Subpath.StartsWith("status/", StringComparison.Ordinal)) return Text(404, "not found");
            if (request.Method != "POST") return Text(405, "method not allowed");
            if (context.TerminalForToken(request.Subpath["status/".Length..]) is not { } owner) return Text(404, "unknown terminal");
            JsonElement? body;
            try { body = JsonSerializer.Deserialize<JsonElement>(request.Body.Span); }
            catch (JsonException) { body = null; }
            if (CodexStatusReports.Parse(body) is not { } report || StartedByAnotherAgent(context, owner)) return Text(200, "ignored");

            // Only launches with SharpRail's instructions tag their status URL; the record keeps that in its command.
            var appended = request.Query.Contains("thinkrail_prompt=1", StringComparison.Ordinal);
            CodexRolloutFacts? facts = null;
            if (report.TranscriptPath is { } transcript)
            {
                lock (gate) rolloutOfTab[TabIndex(owner)] = transcript;
                facts = rollouts.Read(transcript);
            }
            if (report.SessionId is not null || report.Model is not null || report.Effort is not null || facts?.Model is not null || facts?.Effort is not null)
            {
                var existing = context.AgentRecord(owner) is { Kind: CodexManifest.Id } codex ? codex : null;
                var sessionId = report.SessionId ?? existing?.SessionId;
                var command = existing?.Command ?? CommandOf(context);
                if (appended) command = WithPrompt(command);
                else if (existing?.SessionId != sessionId && CodexLaunch.HasSharpRailPrompt(command)) command = CommandOf(context);
                var sameSession = existing?.SessionId == sessionId;
                var next = new TerminalAgentRecord(CodexManifest.Id, command)
                {
                    SessionId = sessionId,
                    Model = report.Model ?? facts?.Model ?? (sameSession ? existing?.Model : null),
                    Effort = report.Effort ?? facts?.Effort ?? (sameSession ? existing?.Effort : null),
                    LaunchedByUi = existing?.LaunchedByUi
                };
                if (next != existing) context.SetAgentRecord(owner, next);
            }
            var push = new CodexStatusPush(owner.WorkspaceId, owner.TabKey, report.Status, report.Event)
            {
                Model = report.Model,
                Cwd = report.Cwd,
                SessionId = report.SessionId,
                Usage = facts?.Usage,
                Plan = facts?.Plan
            };
            statuses.Record(push);
            context.Publish(CodexContract.Status, push);
            return Text(200, "ok");
        });

        void WritePrompt()
        {
            try { CodexSystemPrompt.Write(context.PromptTexts(CodexManifest.Id, PromptPlacement.ToolList), context.PromptTexts(CodexManifest.Id, PromptPlacement.Section)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        WritePrompt();
        try { CodexSkills.Install(CodexSkills.ShippedRoot(context.AssetsDirectory)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { context.Log.Warn("Codex skills were not installed: " + error.Message); }
        context.TerminalEnvironment(terminal =>
        {
            // Plugins enabled or disabled since the last session change what the file says.
            WritePrompt();
            var environment = new Dictionary<string, string>
            {
                [CodexLaunch.PromptJsonVariable] = CodexSystemPrompt.Json(context.PromptTexts(CodexManifest.Id, PromptPlacement.ToolList, terminal.WorkspaceId),
                    context.PromptTexts(CodexManifest.Id, PromptPlacement.Section, terminal.WorkspaceId)),
                [CodexConfig.StatusUrlEnv] = $"{context.PublicBaseUrl()}/status/{context.TerminalToken(terminal)}"
            };
            var projectId = workspaces.TryGetValue(terminal.WorkspaceId, out var workspace) ? workspace.ProjectId : null;
            if (context.Projects().FirstOrDefault(project => project.Id == projectId || project.Path == terminal.WorkspaceId) is { } project)
            {
                environment[CodexLaunch.WorktreesVariable] = project.WorktreesDirectory;
                if (project.ScratchDirectory.Length > 0 && Path.GetFileName(terminal.WorkspaceId.TrimEnd('/', '\\')) is { Length: > 0 } leaf)
                {
                    var scratch = Path.Combine(project.ScratchDirectory, leaf);
                    try { Directory.CreateDirectory(scratch); environment[CodexLaunch.ScratchVariable] = scratch; }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { context.Log.Warn("could not create the scratch directory: " + error.Message); }
                }
            }
            return environment;
        });

        var watch = new AgentWatch(
            () => [.. context.Terminals().Where(item => item.Pid is not null).Select(item => new AgentWatchTarget(item.Terminal.WorkspaceId, item.Terminal.TabKey, item.Pid!.Value))],
            (workspaceId, tabKey, pid) =>
            {
                var terminal = new TerminalRef(workspaceId, tabKey);
                if (StartedByAnotherAgent(context, terminal)) return;
                codexPidOfTab[TabIndex(terminal)] = pid;
                var existing = context.AgentRecord(terminal) is { Kind: CodexManifest.Id } codex ? codex : null;
                var launchedByUi = ProcessSnapshot.CaptureUiLaunch(pid);
                var command = launchedByUi == false ? CommandOf(context) : existing?.Command ?? CommandOf(context);
                // Before any hook reports, the process's own arguments show whether it received SharpRail's instructions.
                if (ProcessSnapshot.CaptureCommand(pid)?.Contains("developer_instructions=\"# You are running inside SharpRail", StringComparison.Ordinal) == true)
                    command = WithPrompt(command);
                context.SetAgentRecord(terminal, new(CodexManifest.Id, command)
                {
                    SessionId = existing?.SessionId,
                    Model = existing?.Model,
                    Effort = existing?.Effort,
                    LaunchedByUi = launchedByUi
                });
            },
            (workspaceId, tabKey) => context.SetAgentRecord(new(workspaceId, tabKey), null));

        void Forget(TerminalRef terminal)
        {
            codexPidOfTab.TryRemove(TabIndex(terminal), out _);
            statuses.Forget(terminal.WorkspaceId, terminal.TabKey);
            string? rollout;
            bool shared;
            lock (gate)
            {
                rolloutOfTab.Remove(TabIndex(terminal), out rollout);
                shared = rollout is not null && rolloutOfTab.ContainsValue(rollout);
            }
            if (rollout is not null && !shared) rollouts.Forget(rollout);
        }
        context.OnTerminal(change =>
        {
            switch (change)
            {
                case TerminalSpawned: watch.Poke(); break;
                // Codex's own OSC 133 marks say whether it waits at its prompt; they win over guessing, while a hook's
                // Blocked or Done stays until it runs again. Without marks only the hook reports.
                case TerminalPromptChanged { Foreground: true } prompt when context.AgentRecord(prompt.Terminal)?.Kind == CodexManifest.Id:
                    var last = statuses.Snapshot(prompt.Terminal.WorkspaceId).FirstOrDefault(push => push.TabKey == prompt.Terminal.TabKey);
                    var next = prompt.AtPrompt ? CodexStatus.Idle : CodexStatus.Running;
                    if (last?.Status != next && !(prompt.AtPrompt && last?.Status is CodexStatus.Blocked or CodexStatus.Done))
                    {
                        var push = new CodexStatusPush(prompt.Terminal.WorkspaceId, prompt.Terminal.TabKey, next, prompt.AtPrompt ? "prompt_mark" : "command_mark")
                        { Model = last?.Model, Cwd = last?.Cwd, SessionId = last?.SessionId };
                        statuses.Record(push);
                        context.Publish(CodexContract.Status, push);
                    }
                    break;
                case TerminalClosed closed: watch.Forget(closed.Terminal.WorkspaceId, closed.Terminal.TabKey); Forget(closed.Terminal); break;
                case TerminalAgentChanged { Record: null } cleared: watch.Forget(cleared.Terminal.WorkspaceId, cleared.Terminal.TabKey); Forget(cleared.Terminal); break;
            }
        });

        context.RevivePrefill((_, record) =>
        {
            if (record.Kind != CodexManifest.Id) return null;
            var settings = context.Settings<CodexSettings>();
            string? Line(bool fresh) => CodexResume.Command(record.Command, record.SessionId, settings.Mcp, OperatingSystem.IsWindows(), settings.PermissionMode,
                appendSystemPrompt: settings.AppendSystemPrompt, uiLaunch: true, model: record.Model, effort: record.Effort,
                contributions: context.AgentLaunchContributions(CodexManifest.Id), fresh: fresh);
            return Line(false) is { } text ? new RevivePrefill(text) { Program = true, Fresh = Line(true) } : null;
        });

        await Task.CompletedTask;
        return async () =>
        {
            ide.Dispose();
            watch.Dispose();
            try { CodexSkills.Remove(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { context.Log.Warn("Codex skills were not removed: " + error.Message); }
            CodexAccountReader currentAccount;
            CodexModelReader currentModels;
            lock (gate) { currentAccount = account; currentModels = models; }
            await Task.WhenAll(currentAccount.StopAsync(), currentModels.StopAsync());
        };
    }

    private static ValueTask<PluginHttpResponse> Text(int status, string body) =>
        ValueTask.FromResult(new PluginHttpResponse(status) { Body = Encoding.UTF8.GetBytes(body) });
}