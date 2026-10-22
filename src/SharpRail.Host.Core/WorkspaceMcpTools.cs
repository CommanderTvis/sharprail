using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;

namespace SharpRail.Host.Core;

/// <summary>Host workspace tools bound to the authenticated terminal's working directory.</summary>
public sealed class WorkspaceMcpTools(HostStateStore state)
{
    private readonly SemaphoreSlim creation = new(1, 1);

    public McpServer.McpTool Create(string workspace) => new(
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

    private static string? Text(JsonObject arguments, string key) => arguments[key] is null ? null
        : arguments[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>()
        : throw new ArgumentException($"{key} must be a string.");
}