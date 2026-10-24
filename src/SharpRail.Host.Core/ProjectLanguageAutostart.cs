using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;

namespace SharpRail.Host.Core;

public sealed partial class HostStateStore
{
    private readonly SemaphoreSlim agentLanguageStarts = new(1, 1);
    internal async ValueTask<LanguageResult> ProjectLanguageAutostartAsync(LanguageRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Workspace));
        string project;
        string[] selected;
        var shared = (ProjectOfWorkspace(workspace) ?? (Current.Projects.Contains(workspace) ? workspace : null)) is { } known
            ? ProjectLanguage(known).Servers.Where(item => item.Value["autoStart"]?.GetValue<bool>() == true).Select(item => item.Key).Order(StringComparer.Ordinal).ToArray()
            : [];
        lock (gate)
        {
            project = state.Workspaces.FirstOrDefault(item => item.Path == workspace)?.ProjectRoot
                ?? (state.Projects.Contains(workspace) ? workspace : null)
                ?? throw new ArgumentException("Choose a registered project workspace.");
            // The local list, once it exists, overrides the project file; its first change starts from the file's choices.
            selected = state.ProjectLanguageAutostart.TryGetValue(project, out var local)
                ? JsonSerializer.Deserialize<string[]>(local) ?? []
                : shared;
            if (request.Operation == "setProjectAutostart")
            {
                if (LanguageServers.Known(request.Server) is null) throw new ArgumentException("Unknown language server.");
                if (!bool.TryParse(request.Configuration, out var enabled)) throw new ArgumentException("Choose true or false for project autostart.");
                selected = (enabled ? selected.Append(request.Server) : selected.Where(id => id != request.Server))
                    .Distinct().Order(StringComparer.Ordinal).ToArray();
                var projects = state.ProjectLanguageAutostart.ToDictionary();
                projects[project] = JsonSerializer.Serialize(selected);
                Publish(state with { ProjectLanguageAutostart = projects }, persist: true);
            }
        }
        if (request.Operation != "prepareAgent") return new("ready") { Configuration = JsonSerializer.Serialize(selected) };
        // A server the user hid in this project is never started for agents.
        var hidden = HiddenLanguageServers(project);
        selected = [.. selected.Where(id => !hidden.Contains(id))];
        if (!Current.Settings.LspEnabled) return new("disabled", "Code Intelligence is disabled.");
        var starts = agentLanguageStarts;
        await starts.WaitAsync(token);
        try
        {
            foreach (var server in selected)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var result = await Languages.ExecuteAsync(new(workspace, Operation: "status", Server: server, Origin: LanguageOrigins.Config), token);
                    if (result.State is "error" or "stopped")
                        await Languages.ExecuteAsync(new(workspace, Operation: "restart", Server: server,
                            Configuration: WorkspaceLanguage(workspace, server), Origin: LanguageOrigins.Config), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) { System.Diagnostics.Trace.WriteLine("Language server startup: " + error.Message); }
            }
        }
        finally { starts.Release(); }
        return new("ready", "Selected server startup requested; installation and indexing may continue after the agent starts.");
    }

    /// <summary>The server ids the user hid in <paramref name="project"/>'s Code Intelligence pane.</summary>
    internal IReadOnlySet<string> HiddenLanguageServers(string project) =>
        Current.ProjectLanguageHidden.TryGetValue(project, out var json) ? (JsonSerializer.Deserialize<string[]>(json) ?? []).ToHashSet(StringComparer.Ordinal) : new HashSet<string>();

    /// <summary>
    /// Reads (<c>projectHidden</c>) or changes (<c>setProjectHidden</c>, <c>Configuration</c> "true" or "false") the servers the user
    /// hid in a project's pane. A personal preference kept in host state, never in the shared <c>language.toml</c>.
    /// </summary>
    internal ValueTask<LanguageResult> ProjectLanguageHiddenAsync(LanguageRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Workspace));
        string[] hidden;
        lock (gate)
        {
            // An unregistered folder is its own project, as the pane treats it.
            var project = state.Workspaces.FirstOrDefault(item => item.Path == workspace)?.ProjectRoot ?? workspace;
            hidden = state.ProjectLanguageHidden.TryGetValue(project, out var json) ? JsonSerializer.Deserialize<string[]>(json) ?? [] : [];
            if (request.Operation == "setProjectHidden")
            {
                if (LanguageServers.Known(request.Server) is null) throw new ArgumentException("Unknown language server.");
                if (!bool.TryParse(request.Configuration, out var hide)) throw new ArgumentException("Choose true or false for hiding.");
                hidden = (hide ? hidden.Append(request.Server) : hidden.Where(id => id != request.Server)).Distinct().Order(StringComparer.Ordinal).ToArray();
                var projects = state.ProjectLanguageHidden.ToDictionary();
                if (hidden.Length > 0) projects[project] = JsonSerializer.Serialize(hidden);
                else projects.Remove(project);
                Publish(state with { ProjectLanguageHidden = projects }, persist: true);
            }
        }
        return ValueTask.FromResult(new LanguageResult("ready") { Configuration = JsonSerializer.Serialize(hidden) });
    }
}