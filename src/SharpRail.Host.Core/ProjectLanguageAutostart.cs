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
                if (!LanguageServers.Catalog.Any(server => server.Id == request.Server)) throw new ArgumentException("Unknown language server.");
                if (!bool.TryParse(request.Configuration, out var enabled)) throw new ArgumentException("Choose true or false for project autostart.");
                selected = (enabled ? selected.Append(request.Server) : selected.Where(id => id != request.Server))
                    .Distinct().Order(StringComparer.Ordinal).ToArray();
                var projects = state.ProjectLanguageAutostart.ToDictionary();
                projects[project] = JsonSerializer.Serialize(selected);
                Publish(state with { ProjectLanguageAutostart = projects }, persist: true);
            }
        }
        if (request.Operation != "prepareAgent") return new("ready") { Configuration = JsonSerializer.Serialize(selected) };
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
}