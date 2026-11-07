using System.Text.Json.Serialization.Metadata;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.SpecDialect;

namespace SharpRail.Plugins.Blueprint.Host;

public sealed class BlueprintHost : PluginHostModule
{
    public override PluginContract Contract => BlueprintContract.Contract;

    public override IJsonTypeInfoResolver Json => BlueprintHostJson.Default;

    /// <summary>The list item about <c>blueprint_check</c> in the instructions agent plugins give sessions (H20).</summary>
    internal const string Guidance = "`blueprint_check`: you MUST call it to verify SharpRail's rendered view after writing or rewriting BLUEPRINT.md.";

    /// <summary>Where the format specification is written, in the plugin's own directory under the state directory; null when the host has no state directory.</summary>
    internal static string? FormatPath(string? stateDirectory) =>
        stateDirectory is { Length: > 0 } ? Path.Combine(Path.GetFullPath(stateDirectory), "blueprint-format.md") : null;

    /// <summary>The prompt section that names <paramref name="path"/>, the generated format specification.</summary>
    internal static string FormatGuidance(string path) =>
        $"The Blueprint format (BLUEPRINT.md: frontmatter, `!control` blocks, options with reasons, ids, rules) is specified at {path}. When asked to use a blueprint, read that file before writing or editing a BLUEPRINT.md, and call `blueprint_check` afterwards to verify it.";

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        context.Prompt(new PromptContribution { Id = "blueprint-check", Title = "Blueprint check", Description = "Verify the rendered view with blueprint_check after writing BLUEPRINT.md.", Text = Guidance, Order = 30 });
        // Regenerated on every activation, like the system prompt files; a host without a state directory offers no section.
        if (FormatPath(context.StateDirectory) is { } formatPath)
        {
            try
            {
                GeneratedInstructions.Write(formatPath, BlueprintPrompts.FormatDocument);
                context.Prompt(new PromptContribution { Id = "blueprint-format", Title = "Blueprint format", Description = "Where the Blueprint format specification is, for any session that is asked to use a blueprint.", Text = FormatGuidance(formatPath), Placement = PromptPlacement.Section, Order = 30 });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        var gate = new object();
        var sessions = new BlueprintSessions(() => context.ReadState<Dictionary<string, PersistedBlueprint>>("blueprints", []),
            records => context.WriteState("blueprints", records), payload => context.Publish(BlueprintContract.Changed, payload));

        void Deliver(string workspaceId, string? text)
        {
            if (text is not null && sessions.Author(workspaceId) is BlueprintTerminalAuthor author)
                context.WriteTerminal(new(workspaceId, author.TabKey), text + "\r");
        }

        ValueTask<BlueprintAck> Change(Action action)
        {
            lock (gate) action();
            return ValueTask.FromResult(new BlueprintAck());
        }

        context.Method(BlueprintContract.Open, async (parameters, _, ct) =>
        {
            if (parameters.AgentId is not (BlueprintAgentId.Claude or BlueprintAgentId.Codex))
                throw new InvalidOperationException("Blueprint authoring uses a Claude Code or Codex terminal in SharpRail.");
            var workspace = await context.WorkspaceAsync(parameters.WorkspaceId, ct)
                ?? throw new InvalidOperationException($"Unknown workspace: {parameters.WorkspaceId}");
            await context.WatchWorkspaceAsync(parameters.WorkspaceId, ct);
            return await Task.Run(() =>
            {
                lock (gate)
                {
                    var existing = BlueprintSessions.ReadFile(workspace.Path) is not null;
                    var source = existing ? parameters.Source : ResolveSource(workspace.Path, parameters.Source);
                    var state = sessions.Open(parameters.WorkspaceId, workspace.Path, source, parameters.AgentId);
                    return new BlueprintOpened(state, existing ? BlueprintPrompts.ExistingBlueprint() : BlueprintPrompts.Opening(source),
                        BlueprintPrompts.Appendix);
                }
            }, ct);
        });
        context.Method(BlueprintContract.Get, async (parameters, _, ct) =>
        {
            var workspace = await context.WorkspaceAsync(parameters.WorkspaceId, ct);
            if (workspace is not null) await context.WatchWorkspaceAsync(parameters.WorkspaceId, ct);
            return await Task.Run(() =>
            {
                lock (gate) return new BlueprintChangedPayload(parameters.WorkspaceId, sessions.Get(parameters.WorkspaceId, workspace?.Path));
            }, ct);
        });
        context.Method(BlueprintContract.SetAuthor, (parameters, _, _) => Change(() =>
        {
            if (parameters.Author is not BlueprintTerminalAuthor)
                throw new InvalidOperationException("Blueprint authors must run in a visible terminal.");
            sessions.SetAuthor(parameters.WorkspaceId, parameters.Author);
        }));
        context.Method(BlueprintContract.Close, (parameters, _, _) => Change(() => sessions.Close(parameters.WorkspaceId)));
        context.Method(BlueprintContract.Select, (parameters, _, _) => Change(() =>
            Deliver(parameters.WorkspaceId, sessions.Select(parameters.WorkspaceId, parameters.ControlId, parameters.OptionId))));
        context.Method(BlueprintContract.Edit, (parameters, _, _) => Change(() => sessions.Edit(parameters.WorkspaceId, parameters.Target, parameters.Text)));
        context.Method(BlueprintContract.ConfirmEdits, (parameters, _, _) => Change(() => Deliver(parameters.WorkspaceId, sessions.ConfirmEdits(parameters.WorkspaceId))));
        context.Method(BlueprintContract.DiscardEdits, (parameters, _, _) => Change(() => sessions.DiscardEdits(parameters.WorkspaceId)));
        context.OnFilesChanged(change =>
        {
            if (change.Truncated || change.Paths.Any(path => path.EndsWith(BlueprintContract.File, StringComparison.Ordinal)))
                lock (gate) sessions.NoteFileChanged(change.WorkspaceId);
        });
        context.OnTerminal(change =>
        {
            if (change is TerminalAgentChanged { Record.SessionId: { Length: > 0 } id } agent)
                lock (gate) sessions.NoteAuthorSession(agent.Terminal.WorkspaceId, agent.Terminal.TabKey, id);
        });
        context.RevivePrefill((terminal, _) =>
        {
            lock (gate) return sessions.Author(terminal.WorkspaceId) is BlueprintTerminalAuthor author && author.TabKey == terminal.TabKey
                ? new RevivePrefill(Submit: true) : null;
        });
        context.Tool(new PluginTool<BlueprintCheckParams>("blueprint_check", "Check Blueprint", BlueprintCheck.Description,
            async (_, toolContext, ct) =>
            {
                var outcome = await Task.Run(() => BlueprintCheck.Run(toolContext.Cwd), ct);
                if (outcome.IsError || toolContext.WorkspaceId is not { } workspaceId) return outcome;
                try
                {
                    var graph = await context.Dependency(SpecDialectContract.Contract).RequestAsync(SpecDialectContract.Graph, new(workspaceId), ct);
                    if (!graph.Nodes.Any(node => node.Path == BlueprintContract.File))
                        return outcome with { Text = outcome.Text + $"\n\n{BlueprintContract.File} is not indexed as a spec yet — open it with id/type/title frontmatter so the Specs tool lists it." };
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
                return outcome;
            }));
        return ValueTask.FromResult<PluginDisposer?>(null);
    }

    private static BlueprintSource ResolveSource(string root, BlueprintSource source)
    {
        if (source is BlueprintIdea idea)
            return idea.Brief.Trim() is { Length: > 0 } brief ? new BlueprintIdea(brief)
                : throw new ArgumentException("Describe what you want to build first.");
        if (source is BlueprintProduct) return source;
        var spec = (BlueprintSpec)source;
        var absolute = Path.GetFullPath(spec.Path, root);
        var path = Path.GetRelativePath(root, absolute);
        if (path is "." or ".." || path.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(path))
            throw new ArgumentException("Choose a document inside this project.");
        if (!File.Exists(absolute)) throw new ArgumentException($"There is no file at {path}.");
        return new BlueprintSpec(path);
    }
}