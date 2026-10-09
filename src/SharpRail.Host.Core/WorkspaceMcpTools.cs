using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

/// <summary>Host workspace tools bound to the authenticated terminal's working directory.</summary>
public sealed class WorkspaceMcpTools(HostStateStore state)
{
    private readonly SemaphoreSlim creation = new(1, 1);

    public McpServer.McpTool Create(string workspace) => new(
        "workspace_create", "Create a workspace",
        "Create a Git worktree for this terminal's project in SharpRail's managed directory. The description is displayed in Projects. Returns the path and branch; does not switch your terminal.",
        JsonNode.Parse("""
            {"type":"object","properties":{
              "description":{"type":"string","minLength":1,"description":"A concise description of the work, displayed to the user as the workspace label."},
              "branch":{"type":"string","description":"New branch name. Omit to use SharpRail's next available workspace branch."},
              "baseBranch":{"type":"string","description":"Base branch or commit. Defaults to HEAD of the calling workspace; use the user's requested base."}
            },"required":["description"],"additionalProperties":false}
            """)!.AsObject(),
        async (arguments, token) =>
        {
            if (arguments.Any(argument => argument.Key is not ("description" or "branch" or "baseBranch")))
                throw new ArgumentException("Only description, branch and baseBranch are accepted.");
            var description = Text(arguments, "description")?.Trim();
            if (string.IsNullOrEmpty(description) || description.Contains('\0'))
                throw new ArgumentException("Provide a description of the work for the workspace label.");
            var branch = Text(arguments, "branch");
            var baseBranch = Text(arguments, "baseBranch") ?? "HEAD";
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
                return (JsonSerializer.Serialize(new { path = catalog.SuggestedPath, branch, description }), false);
            }
            finally { creation.Release(); }
        });

    private static string? Text(JsonObject arguments, string key) => arguments[key] is null ? null
        : arguments[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>()
        : throw new ArgumentException($"{key} must be a string.");
}