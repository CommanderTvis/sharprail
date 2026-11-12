namespace SharpRail.Plugins.Api.Host;

/// <summary>What a tool run for a workspace environment step produced.</summary>
/// <param name="exitCode">The exit code, or null when the tool could not start or did not finish within its deadline.</param>
/// <param name="output">Standard output.</param>
/// <param name="error">Standard error, or the reason the tool did not run.</param>
public sealed class EnvironmentToolResult(int? exitCode, string output, string error)
{
    /// <summary>The exit code, or null when the tool could not start or did not finish within its deadline.</summary>
    public int? ExitCode { get; } = exitCode;

    /// <summary>Standard output.</summary>
    public string Output { get; } = output;

    /// <summary>Standard error, or the reason the tool did not run.</summary>
    public string Error { get; } = error;

    /// <summary>Whether the tool ran and exited with zero.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// One workspace's shell environment while the enabled <see cref="IPluginHostContext.WorkspaceEnvironmentStep"/> steps
/// edit it in order. It starts as the host's own environment without the host's session token, and a step sees what the
/// steps before it produced.
/// </summary>
public interface IWorkspaceEnvironmentEdit
{
    /// <summary>The workspace's root directory.</summary>
    string Directory { get; }

    /// <summary>The project's default workspace when <see cref="Directory"/> is another workspace of it, otherwise null.</summary>
    string? DefaultDirectory { get; }

    /// <summary>The step's plugin logger.</summary>
    IPluginLogger Log { get; }

    /// <summary>Reads a variable, or null when it is not set.</summary>
    /// <param name="name">The variable name.</param>
    string? Get(string name);

    /// <summary>Whether an earlier step set or removed the variable. A value the shell would inherit from the host is not an edit.</summary>
    /// <param name="name">The variable name.</param>
    bool Edited(string name);

    /// <summary>Sets a variable, or removes it when <paramref name="value"/> is null. A removal only affects later steps: the shell still inherits the host's value.</summary>
    /// <param name="name">The variable name.</param>
    /// <param name="value">The new value, or null.</param>
    void Set(string name, string? value);

    /// <summary>Puts a directory first on PATH, moving it when it is already there.</summary>
    /// <param name="directory">The directory.</param>
    void PrependPath(string directory);

    /// <summary>Finds an executable on the edited PATH.</summary>
    /// <param name="program">The program name without an extension.</param>
    /// <returns>Its full path, or null when it is not there.</returns>
    string? Find(string program);

    /// <summary>
    /// Runs a program with the edited environment, no console and a 30 second deadline after which its process tree is
    /// killed. The caller's token cancels it.
    /// </summary>
    /// <param name="program">The path from <see cref="Find"/>.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="directory">The working directory; the workspace root when null.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    ValueTask<EnvironmentToolResult> RunAsync(string program, IReadOnlyList<string> arguments, string? directory, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a script to the shell the workspace's terminals start, run after the user's own start-up files so the
    /// shell keeps the environment in sync as it changes directory (mise's and direnv's hooks). Only zsh and fish are
    /// supported; other shells ignore it. The script is the shell's own syntax.
    /// </summary>
    /// <param name="shell">The shell name: <c>zsh</c> or <c>fish</c>.</param>
    /// <param name="script">The commands to run.</param>
    void ShellInit(string shell, string script);

    /// <summary>
    /// Reports that the step applied an environment, so terminals show it as a chip (<see cref="IPluginHostContext.OnWorkspaceEnvironment"/>).
    /// The host adds the variables the step set to the tooltip.
    /// </summary>
    /// <param name="label">The chip text, such as <c>venv</c> or <c>node 20.11.1</c>.</param>
    /// <param name="detail">Optional tooltip text, such as the directory the environment came from.</param>
    void Report(string label, string? detail = null);

    /// <summary>
    /// Whether the project's default workspace has these files (workspace-relative) byte for byte as this workspace has
    /// them, with at least one present: the condition for a step to carry over a trust decision the user made there.
    /// </summary>
    /// <param name="files">The configuration files.</param>
    bool MatchesDefault(IReadOnlyList<string> files);
}

/// <summary>The order of workspace environment steps, supplied by the plugin that owns the policy (<see cref="IPluginHostContext.WorkspaceEnvironment"/>).</summary>
public interface IWorkspaceEnvironmentPolicy
{
    /// <summary>Where a step runs; lower runs first. A step id the policy does not know should rank <see cref="int.MaxValue"/>.</summary>
    /// <param name="stepId">The id the step was registered with.</param>
    int Rank(string stepId);
}

/// <summary>One environment a workspace's terminals start with, as a step reported it.</summary>
/// <param name="pluginId">The plugin whose step applied it.</param>
/// <param name="label">The short name, such as <c>mise</c> or <c>venv</c>.</param>
/// <param name="detail">Tooltip text including the variables the step set, or null.</param>
public sealed class WorkspaceEnvironmentFact(string pluginId, string label, string? detail)
{
    /// <summary>The plugin whose step applied it.</summary>
    public string PluginId { get; } = pluginId;

    /// <summary>The short name, such as <c>mise</c> or <c>venv</c>.</summary>
    public string Label { get; } = label;

    /// <summary>Tooltip text including the variables the step set, or null.</summary>
    public string? Detail { get; } = detail;
}