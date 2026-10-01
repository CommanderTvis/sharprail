using Avalonia.Controls;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.UI.Plugins;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private sealed record CachedTool(string Project, SideToolRegistration? Registration, Control Mount);
    private readonly Dictionary<(string Workspace, string Tool), CachedTool> cachedTools = [];
    private sealed record CachedActions(string Project, IReadOnlyList<PluginRow<WorkspaceScopedActionRegistration>> Registrations, Control[] Controls);
    private readonly Dictionary<(string Workspace, string Group), CachedActions> cachedActions = [];

    private IEnumerable<Control> RetainedCenterActions(string group)
    {
        if (atHome) return [];
        var key = (workspaceRoot, group);
        if (!WorkspaceMounted)
        {
            if (!cachedActions.TryGetValue(key, out var pending)) return [];
            foreach (var control in pending.Controls) control.IsHitTestVisible = false;
            return pending.Controls;
        }
        if (!cachedActions.TryGetValue(key, out var cached) || !cached.Registrations.SequenceEqual(Plugins.WorkspaceActions))
        {
            var workspace = workspaceRoot;
            cachedActions[key] = cached = new(projectRoot, Plugins.WorkspaceActions,
                [.. Plugins.WorkspaceActions.Select(action => Mount(action.PluginId, () => action.Value.Create(workspace, group)))]);
        }
        foreach (var control in cached.Controls) control.IsHitTestVisible = true;
        return cached.Controls;
    }

    private Control RetainedPluginTool(string toolId)
    {
        var registration = LiveTool(toolId);
        var key = (workspaceRoot, toolId);
        if (cachedTools.TryGetValue(key, out var saved) && Equals(saved.Registration, registration)) return saved.Mount;
        if (registration?.Retarget is { } retarget)
            foreach (var (oldKey, candidate) in cachedTools.ToArray())
            {
                if (oldKey.Tool != toolId || !Equals(candidate.Registration, registration) ||
                    candidate.Mount is not ContentControl { Content: Control content }) continue;
                try { if (!retarget(content, workspaceRoot)) continue; }
                catch (Exception error) { Console.Error.WriteLine($"Plugin tool {toolId} failed to retarget: {error}"); continue; }
                cachedTools.Remove(oldKey);
                cachedTools[key] = candidate with { Project = projectRoot };
                return candidate.Mount;
            }
        var pluginId = PluginIdentity.ParseToolId(toolId)!.Value.PluginId;
        var workspace = workspaceRoot;
        var mount = registration is null ? DormantTool(toolId, pluginId) : Mount(pluginId, () => registration.Create(workspace));
        cachedTools[key] = new(projectRoot, registration, mount);
        return mount;
    }

    private void InvalidatePluginTools(string? project = null)
    {
        foreach (var entry in cachedActions.Where(entry => project is null ? !entry.Value.Registrations.SequenceEqual(Plugins.WorkspaceActions)
            : entry.Value.Project == project).ToArray()) cachedActions.Remove(entry.Key);
        var stale = cachedTools.Where(entry => project is null ? !Equals(entry.Value.Registration, LiveTool(entry.Key.Tool))
            : entry.Value.Project == project).ToArray();
        var mounted = new List<string>();
        foreach (var (key, cached) in stale)
        {
            cachedTools.Remove(key);
            if (ReferenceEquals(toolContent.GetValueOrDefault(key.Tool), cached.Mount))
            {
                toolContent.Remove(key.Tool);
                mounted.Add(key.Tool);
            }
            if (cached.Mount is ContentControl { Content: IDisposable disposable }) disposable.Dispose();
        }
        if (project is null && mounted.Count > 0) surface.RefreshContents([.. mounted]);
    }

    private void ClearRetainedTools(string? workspace = null)
    {
        foreach (var (key, cached) in cachedTools.Where(entry => workspace is null || entry.Key.Workspace == workspace).ToArray())
        {
            cachedTools.Remove(key);
            if (ReferenceEquals(toolContent.GetValueOrDefault(key.Tool), cached.Mount)) toolContent.Remove(key.Tool);
            if (cached.Mount is ContentControl { Content: IDisposable disposable }) disposable.Dispose();
        }
        foreach (var key in cachedActions.Keys.Where(key => workspace is null || key.Workspace == workspace).ToArray()) cachedActions.Remove(key);
    }
}