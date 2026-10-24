using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;

namespace SharpRail.Host.Core;

/// <summary>Host workspace tools bound to the authenticated terminal's working directory.</summary>
public sealed class WorkspaceMcpTools(HostStateStore state)
{
    private readonly SemaphoreSlim creation = new(1, 1);

    public McpServer.McpTool Create(string workspace, TerminalRef? terminal = null) => new(
        "workspace_create", "Create a workspace",
        "Create a Git worktree for this terminal's project in SharpRail's managed directory. The description is displayed in Projects. Returns the path and branch; does not switch your terminal. Example: workspace_create({description, languageServers:[\"csharp\"]}) also starts the C# server, which keeps running for the life of the workspace.",
        JsonNode.Parse("""
            {"type":"object","properties":{
              "description":{"type":"string","minLength":1,"description":"A concise description of the work, displayed to the user as the workspace label."},
              "branch":{"type":"string","description":"New branch name. Omit to use SharpRail's next available workspace branch."},
              "baseBranch":{"type":"string","description":"Base branch or commit. Defaults to HEAD of the calling workspace; use the user's requested base."},
              "languageServers":{"type":"array","items":{"type":"string"},"description":"Language servers to start for the new workspace, as ids or language names (\"csharp\", \"typescript\", \"go\"), or \"auto\" for every server with files in the project. Starts in the background; the result lists each server's state. List the servers the agents of this workspace need."}
            },"required":["description"],"additionalProperties":false}
            """)!.AsObject(),
        async (arguments, token) =>
        {
            if (arguments.Any(argument => argument.Key is not ("description" or "branch" or "baseBranch" or "languageServers")))
                throw new ArgumentException("Only description, branch, baseBranch and languageServers are accepted.");
            var description = Text(arguments, "description")?.Trim();
            if (string.IsNullOrEmpty(description) || description.Contains('\0'))
                throw new ArgumentException("Provide a description of the work for the workspace label.");
            var branch = Text(arguments, "branch");
            var baseBranch = Text(arguments, "baseBranch") ?? "HEAD";
            var languages = arguments["languageServers"] switch
            {
                null => [],
                JsonArray list => list.Select(item => item is JsonValue value && value.TryGetValue<string>(out var name) ? name : throw new ArgumentException("languageServers holds server ids or language names.")).ToArray(),
                JsonValue single when single.TryGetValue<string>(out var one) => [one],
                _ => throw new ArgumentException("languageServers is an array of server ids or language names, or \"auto\".")
            };
            await creation.WaitAsync(token);
            try
            {
                var project = new ProjectServices(workspace, state);
                var calling = await project.OpenProjectAsync(workspace, token);
                if (string.IsNullOrWhiteSpace(baseBranch) || baseBranch.Trim() == "HEAD")
                    baseBranch = (await GitRepository.RunAsync(calling.RootPath, token, "rev-parse", "--verify", "HEAD^{commit}")).Trim();
                var catalog = await project.ListBranchesAsync(false, token);
                branch ??= catalog.SuggestedBranch;
                await project.ApplyGitActionAsync(new("create-worktree", catalog.SuggestedPath, branch, baseBranch), token);
                await state.ChangeAsync([HostStateChange.Label(catalog.SuggestedPath, description)], CancellationToken.None);
                if (terminal is not null) state.RecordWorkspaceCreator(catalog.SuggestedPath, terminal);
                var started = languages.Length == 0 ? [] : await state.StartRequestedLanguagesAsync(catalog.SuggestedPath, languages, token);
                return (JsonSerializer.Serialize(new { path = catalog.SuggestedPath, branch, description, languageServers = started.Count == 0 ? null : started.Select(row => new { server = row.Server, state = row.State, detail = row.Detail }).ToArray() },
                    new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }), false);
            }
            finally { creation.Release(); }
        });

    public McpServer.McpTool Title(TerminalRef? terminal) => new(
        "set_title", "Title this terminal",
        "Title the SharpRail terminal tab you run in. Call it once, as soon as the task is clear, before your other tool calls, and again only when the terminal moves to another task. An unnamed managed workspace on a generated workspace-N branch takes this first title as its workspace name and branch slug; manual names are kept.",
        JsonNode.Parse("""
            {"type":"object","properties":{
              "title":{"type":"string","minLength":1,"description":"3-6 words naming what this terminal is working on, in the user's language. For a PR, issue, or ticket: `<Verb> #<number> <its exact title>`."}
            },"required":["title"],"additionalProperties":false}
            """)!.AsObject(),
        async (arguments, token) =>
        {
            if (arguments.Any(argument => argument.Key is not "title")) throw new ArgumentException("Only title is accepted.");
            var title = ProjectServices.DisplayName(Text(arguments, "title"));
            if (title is null || !title.Any(char.IsLetterOrDigit) || title.Contains('\0'))
                throw new ArgumentException("Pass a title with at least one letter or digit.");
            if (terminal is null) throw new InvalidOperationException("This session has no terminal tab to title.");
            using (await state.LockWorkspacesAsync(token))
            {
                var workspace = state.Current.Workspaces.FirstOrDefault(workspace => workspace.Path == terminal.WorkspaceId);
                if (workspace is { Kind: WorkspaceKinds.Managed } && !state.Current.WorkspaceLabels.ContainsKey(workspace.Path) &&
                    System.Text.RegularExpressions.Regex.IsMatch(workspace.Branch, @"^workspace-\d+$"))
                {
                    var actual = (await GitRepository.RunAsync(workspace.Path, token, "branch", "--show-current")).Trim();
                    if (actual == workspace.Branch)
                    {
                        var slug = System.Text.RegularExpressions.Regex.Replace(title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
                        if (slug.Length == 0) slug = "task";
                        var branches = (await GitRepository.RunAsync(workspace.Path, token, "for-each-ref", "--format=%(refname:short)", "refs/heads")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        var branch = slug;
                        for (var suffix = 2; branches.Any(known => known == branch || known.StartsWith(branch + "/", StringComparison.Ordinal) || branch.StartsWith(known + "/", StringComparison.Ordinal)); suffix++)
                            branch = slug + "-" + suffix;
                        if (state.Current.WorkspaceLabels.ContainsKey(workspace.Path)) return Titled();
                        await GitRepository.RunAsync(workspace.Path, token, "branch", "-m", branch);
                        state.ChangeWorkspaces(workspaces => workspaces.Select(known => known.Path == workspace.Path ? known with { Branch = branch } : known));
                        state.SetInferredWorkspaceLabel(workspace.Path, title);
                    }
                }
            }
            return Titled();

            // The user's own name for the tab outranks an agent's title.
            (string, bool) Titled() => state.SetTerminalTitle(terminal, title, TerminalTitleSource.Agent)
                ? ($"Terminal titled \"{title}\".", false)
                : ("The user named this terminal's tab; it keeps that name.", false);
        });

    public McpServer.McpTool Delete(string workspace) => new(
        "workspace_delete", "Delete a workspace",
        "Remove a workspace of this terminal's project from SharpRail and from disk, keeping its branch. Use it for a workspace you created once its work is preserved. Refuses a checkout with uncommitted changes, the workspace this terminal runs in, the Default workspace and a worktree SharpRail did not create.",
        JsonNode.Parse("""
            {"type":"object","properties":{
              "path":{"type":"string","minLength":1,"description":"The workspace's path, as workspace_create returned it."}
            },"required":["path"],"additionalProperties":false}
            """)!.AsObject(),
        async (arguments, token) =>
        {
            if (arguments.Any(argument => argument.Key is not "path"))
                throw new ArgumentException("Only path is accepted.");
            var path = Text(arguments, "path");
            if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')) throw new ArgumentException("Pass the path of the workspace to delete.");
            return (await new ProjectServices(workspace, state).DeleteWorkspaceAsync(workspace, path, token), false);
        });

    /// <summary>The agents whose plugins resume a session in a revived tab, so an emigrated tab continues its conversation.</summary>
    private static readonly string[] ResumableAgents = ["claude", "codex"];

    /// <summary>How long an approved emigration waits for the tool's reply to reach the agent before its process ends.</summary>
    public TimeSpan EmigrationGrace { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long an emigration request waits for the user before it is refused.</summary>
    public TimeSpan EmigrationTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The move an approved emigration started, for checks to await; null before the first.</summary>
    public Task? LastEmigration { get; private set; }

    public McpServer.McpTool Emigrate(TerminalRef? terminal) => new(
        "workspace_emigrate", "Move this terminal into a workspace you created",
        "Move ONLY the calling terminal tab into a workspace this same terminal created with workspace_create, and relaunch this agent there resumed (same tab, same conversation). " +
        "Use it ONLY when BOTH hold: (1) the user explicitly instructed you to work in a new workspace, and (2) you are 100% certain you will never again read, edit, build or run anything in the workspace you were started in. " +
        "If either is in doubt, do NOT call it; keep working in the new workspace's path from here instead. The user must approve each move in SharpRail; a refusal changes nothing. " +
        "On approval this session ends within seconds and resumes with its working directory, language servers and SharpRail tools in the target: end your turn immediately after the reply, without further tool calls.",
        JsonNode.Parse("""
            {"type":"object","properties":{
              "path":{"type":"string","minLength":1,"description":"The target workspace's path, as workspace_create returned it to this terminal."},
              "reason":{"type":"string","description":"One sentence for the user's approval prompt: why this terminal should move."}
            },"required":["path"],"additionalProperties":false}
            """)!.AsObject(),
        async (arguments, token) =>
        {
            if (arguments.Any(argument => argument.Key is not ("path" or "reason"))) throw new ArgumentException("Only path and reason are accepted.");
            var target = Text(arguments, "path")?.Trim();
            if (string.IsNullOrEmpty(target) || target.Contains('\0')) throw new ArgumentException("Pass the path of the workspace to move into.");
            var reason = (Text(arguments, "reason") ?? "").Trim();
            if (reason.Contains('\0') || reason.Length > 500) throw new ArgumentException("The reason is one sentence of at most 500 characters.");
            if (EmigrationRefusal(terminal, ref target) is { } refusal) return ("Refused: " + refusal + " Nothing changed.", true);
            var record = state.Current.TerminalAgents.First(agent => agent.Terminal == terminal).Record;
            bool approved;
            var request = new TerminalEmigrationRequest(Guid.NewGuid().ToString("N"), terminal!, target, reason);
            using (var waiting = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                waiting.CancelAfter(EmigrationTimeout);
                try { approved = await state.RequestEmigrationAsync(request, waiting.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { return ("Refused: the user did not answer the approval prompt in time. Nothing changed.", true); }
            }
            if (!approved) return ("Refused: the user denied moving this terminal. Nothing changed; keep working where you are or in the target's path.", true);
            // The reply must reach the agent before its process ends, so the move starts after it is sent.
            LastEmigration = Task.Run(() => MoveAsync(request, record), CancellationToken.None);
            return (JsonSerializer.Serialize(new
            {
                status = "approved",
                from = terminal!.WorkspaceId,
                to = target,
                next = $"This session ends in about {Math.Ceiling(EmigrationGrace.TotalSeconds)} seconds and is relaunched, resumed under the same session id, in this same tab with {target} as its working directory and SharpRail workspace. End your turn now without further tool calls; the resumed session continues the work there."
            }), false);
        });

    // Why a terminal may not move into a target, or null when every guard holds; the target becomes its registry path.
    private string? EmigrationRefusal(TerminalRef? terminal, ref string target)
    {
        if (terminal is null) return "this session has no terminal tab to move.";
        if (state.Terminals is null) return "this host runs no terminals.";
        var current = state.Current;
        if (current.TerminalAgents.FirstOrDefault(agent => agent.Terminal == terminal)?.Record is not { } record || !ResumableAgents.Contains(record.Kind))
            return "only a Claude Code or Codex terminal can be moved, because only those agents resume in a relaunched tab.";
        if (string.IsNullOrEmpty(record.SessionId)) return "this agent's session id is not known yet, so it could not be resumed.";
        var wanted = target;
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(wanted)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return "the path is not valid."; }
        var destination = current.Workspaces.FirstOrDefault(workspace => workspace.Path == wanted || workspace.Path == full);
        if (destination is null) return "the path is not a SharpRail workspace.";
        target = destination.Path;
        var source = current.Workspaces.FirstOrDefault(workspace => workspace.Path == terminal.WorkspaceId);
        if (source is null || source.ProjectRoot != destination.ProjectRoot) return "the target belongs to another project.";
        if (destination.Kind == WorkspaceKinds.Default) return "the Default workspace is never a target.";
        if (destination.Path == terminal.WorkspaceId) return "this terminal already runs in that workspace.";
        if (!Directory.Exists(destination.Path)) return "the target's checkout no longer exists.";
        if (state.WorkspaceCreator(destination.Path) != terminal) return "only a workspace this terminal created with workspace_create can be the target.";
        if (state.Terminals.RevivePrefill?.Invoke(terminal) is not { Text.Length: > 0 }) return "the agent's plugin cannot relaunch this session (is the plugin enabled?).";
        // The move resumes the session without a further question, so nobody else may have it open: this tab's own
        // agent is the one process allowed to name it.
        var own = state.Terminals.List().Where(process => process.Terminal == terminal && process.Pid is not null).Select(process => process.Pid!.Value).ToArray();
        if (state.AgentSessions.OwnedElsewhere(record.Kind, record.SessionId, own) is { } why)
            return $"this session is also open elsewhere ({why}), so resuming it in the target would run it twice.";
        return null;
    }

    // Moves the tab's records and catalog entry to the target, ends the old shell, and starts the target's shell, whose
    // revive hook resumes the agent. A start that fails puts the tab back where it was, to be resumed there.
    private async Task MoveAsync(TerminalEmigrationRequest request, TerminalAgentRecord record)
    {
        var (terminal, target) = (request.Terminal, request.Target);
        var terminals = state.Terminals!;
        await Task.Delay(EmigrationGrace);
        var moved = new TerminalRef(target, terminal.TabKey);
        var titles = state.Current.TerminalTitles.Where(title => title.Terminal == terminal && title.Source != TerminalTitleSource.Program).ToArray();
        var tab = terminals.Catalog.Workspaces.GetValueOrDefault(terminal.WorkspaceId)?.FirstOrDefault(known => known.Key == terminal.TabKey)
            ?? new TerminalTab(terminal.TabKey, titles.FirstOrDefault()?.Title ?? TerminalTab.InitialTitle);
        void Carry(TerminalRef to)
        {
            state.SetTerminalAgent(to, record with { Cwd = to.WorkspaceId });
            foreach (var title in titles) state.SetTerminalTitle(to, title.Title, title.Source);
        }
        Carry(moved);
        await terminals.ReserveAsync(target, tab);
        await terminals.CloseTabAsync(terminal.WorkspaceId, terminal.TabKey);
        try
        {
            // The user approved this resume: it runs as the new shell's program instead of waiting on a resume offer, and the
            // moved tab takes the session's lock from the closed one.
            var resume = terminals.RevivePrefill?.Invoke(moved) is { Text.Length: > 0 } prefill ? prefill.Text
                : throw new InvalidOperationException("the agent's plugin cannot relaunch this session.");
            var agents = state.AgentSessions;
            await Task.Run(() => agents.Release(terminal));
            if (record.SessionId is { Length: > 0 } id && !await Task.Run(() => agents.Hold(moved, record.Kind, id)))
                throw new InvalidOperationException("another SharpRail process opened this session meanwhile.");
            var session = await terminals.AttachAsync(new TerminalAttachRequest(TerminalTab.SessionFor(target, terminal.TabKey), target, "sharprail-emigration")
            { TabKey = terminal.TabKey, Yield = true, Program = resume });
            await session.DisposeAsync();
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine($"Emigrating {terminal.TabKey} to {target} failed; the tab returns to {terminal.WorkspaceId}: {error.Message}");
            await terminals.CloseTabAsync(target, terminal.TabKey);
            await Task.Run(() => state.AgentSessions.Release(moved));
            Carry(terminal);
            await terminals.ReserveAsync(terminal.WorkspaceId, tab);
            state.PublishEmigrationFailure(request with { Reason = error.Message });
        }
    }

    private static string? Text(JsonObject arguments, string key) => arguments[key] is null ? null
        : arguments[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>()
        : throw new ArgumentException($"{key} must be a string.");
}