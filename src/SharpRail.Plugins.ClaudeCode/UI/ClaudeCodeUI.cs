using SharpRail.Plugins.Agent.UI;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>The Claude Code configuration pane, launcher and terminal integration.</summary>
public sealed class ClaudeCodeUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        var store = new ClaudeCodeStore();
        var glyph = new ClaudeGlyph(context);
        var actions = new IdeActions(context);
        var hydrated = new HashSet<string>();
        var disposed = false;
        var status = context.Subscribe(ClaudeCodeContract.Status, push =>
        {
            store.Apply(push);
            NotifyAway(context, store, push);
        });

        async void Hydrate(string workspace)
        {
            if (!hydrated.Add(workspace)) return;
            try
            {
                var rows = await context.RequestAsync(ClaudeCodeContract.StatusSnapshot, new(workspace));
                if (!disposed) foreach (var row in rows) store.Apply(row);
            }
            catch (Exception error) when (error is not OperationCanceledException) { }
        }
        context.OnWorkspaceRemoved(workspace =>
        {
            hydrated.Remove(workspace);
            store.EvictWorkspace(workspace);
        });
        context.SettingsSection(new("Claude Code", "asset:claude.svg", () => new ClaudeCodeSettingsSection(context)));
        context.SideTool(new("config", workspace =>
        {
            Hydrate(workspace);
            return new ClaudeConfigPanel(context, glyph, workspace);
        }));
        context.Launcher(new("claude", "Claude Code", "asset:claude.svg", options =>
        {
            var settings = context.Settings<ClaudeCodeSettings>();
            var command = settings.Command.Trim() is { Length: > 0 } configured ? configured : "claude";
            var parts = new List<string>();
            if (options.Model is { } model) parts.Add("--model " + model);
            if (options.SystemPrompt is { } systemPrompt) parts.Add("--append-system-prompt " + ClaudeLaunch.ShellQuotePath(systemPrompt));
            if (options.ResumeSessionId is { } session) parts.Add("--resume " + session);
            else if (options.InitialPrompt is { } prompt) parts.Add(ClaudeLaunch.ShellQuotePath(prompt));
            return ClaudeLaunch.SessionCommand(command, string.Join(' ', parts), settings.DisableAgentView,
                settings.AppendSystemPrompt, context.Host().HostPlatform);
        }, () => new(true))
        { CreateIcon = glyph.Create, Models = () => [.. ClaudeLaunch.Models.Select(model => new LauncherModel(model.Id, model.Label, "asset:claude.svg"))] });
        context.WorkspaceAction(new WorkspaceScopedActionRegistration("claude-launch",
            (workspace, group) => ClaudeLauncherAction.Create(context, glyph, workspace, group)));
        context.WatchHost(host => string.Join("\n", host.Terminals.SelectMany(pair =>
            pair.Value.Where(tab => tab.Agent?.Kind == "claude").Select(tab => pair.Key + "\t" + tab.TabKey))), (_, _) => context.Invalidate());
        context.FileAction(new("mention", target => ClaudeMention.Label(context, target), target => _ = ClaudeMention.SendAsync(context, target))
        { Icon = "asset:claude.svg" });
        context.TabDecoration(tab =>
        {
            if (tab is ToolTabRef tool && tool.Tool == ClaudeCodeManifest.ConfigTool)
                return new() { Icon = "asset:claude.svg" };
            if (tab is not TerminalTabRef terminal ||
                context.Host().Terminals.GetValueOrDefault(tab.WorkspaceId)?.FirstOrDefault(candidate => candidate.TabKey == terminal.TabKey)?.Agent?.Kind != "claude")
                return null;
            Hydrate(tab.WorkspaceId);
            return new() { Icon = "asset:claude.svg", Adornment = () => new ClaudeStatusAdornment(store, terminal.WorkspaceId, terminal.TabKey) };
        });
        context.TerminalAccessory(new(terminal =>
        {
            Hydrate(terminal.WorkspaceId);
            return new ClaudeTerminalAccessory(context, store, glyph, terminal);
        }));
        var editors = context.Editors.OnEvent(editorEvent => _ = Forward(editorEvent));
        async Task Forward(EditorEvent editorEvent)
        {
            try
            {
                if (editorEvent is EditorSelectionEvent selected)
                {
                    var selection = selected.Selection;
                    await context.RequestAsync(ClaudeCodeContract.SelectionChanged, new(selected.Editor.WorkspaceId,
                        selected.Editor.Path, selection?.Text ?? "", new(selection?.StartLine ?? 1, selection?.StartColumn ?? 1,
                            selection?.EndLine ?? 1, selection?.EndColumn ?? 1)));
                }
                else if (editorEvent is EditorLifecycleEvent { Lifecycle: EditorLifecycle.Closed } closed)
                    await context.RequestAsync(ClaudeCodeContract.DocumentClosed, new(closed.Editor.WorkspaceId, closed.Editor.Path));
            }
            catch (Exception error) when (error is not OperationCanceledException) { }
        }
        var ide = context.Subscribe(ClaudeCodeContract.IdeAction, request => _ = actions.HandleAsync(request));
        return () => { disposed = true; status.Dispose(); editors.Dispose(); ide.Dispose(); };
    }

    // Only live pushes ask: a hydrated snapshot row is where a session already stood, not something that just happened.
    private static void NotifyAway(IPluginUIContext context, ClaudeCodeStore store, ClaudeCodeStatusPush push)
    {
        AgentAttentionReason? reason = push.Status switch
        {
            ClaudeCodeStatus.Blocked => AgentAttentionReason.Blocked,
            ClaudeCodeStatus.Done => AgentAttentionReason.Done,
            ClaudeCodeStatus.Failed => AgentAttentionReason.Failed,
            _ => null
        };
        // A status-only event: the turn ended, but something already notified for it.
        if (reason is null || push.Report.Notify == false) return;
        var report = push.Report;
        context.NotifyAttention(new(push.WorkspaceId, push.TabKey, AgentAttention.Title("Claude", reason.Value, report.Project),
            AgentAttention.Body(report.Summary, report.Response, report.Query, report.ErrorType))
        {
            StillNeeded = () => store.Session(push.WorkspaceId, push.TabKey)?.Status == push.Status
        });
    }
}