using System.Text.Json;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Visualize.Host;

/// <summary>
/// The per-terminal drawings, keyed by workspace and tab, and their record per agent session. It lives as long as
/// the host half's module, so a toggle keeps the live views as the fork's module state did; an activation plugs
/// in the publisher, the session lookup and the persistence, and unplugs the first two when it ends.
/// </summary>
internal sealed class VisualizationStore(TimeSpan? verdictTimeout = null)
{
    internal const string StateName = "visualizations";

    internal const string ToolDescription =
        "Render a rich visualization in the SharpRail workbench, in a live view beside this terminal — " +
        "instead of ASCII art or a plain markdown table. Two kinds, chosen by `type`: 'diagram' renders a " +
        "mermaid diagram (set `mermaid` to raw mermaid source of any kind — flowchart, sequenceDiagram, " +
        "classDiagram, stateDiagram, erDiagram, gantt); 'comparison' renders side-by-side option cards " +
        "(set `options` to the alternatives, each with pros/cons, an optional `recommended` flag, and an " +
        "optional inline `mermaid`). Calling again replaces this terminal's view in place, so you can " +
        "iterate on a diagram and the user watches it evolve.";

    internal interface IPersistence
    {
        Dictionary<string, Dictionary<string, TerminalVisualization>> Read();
        void Write(Dictionary<string, Dictionary<string, TerminalVisualization>> value);
    }

    private sealed class Nowhere : IPersistence
    {
        public Dictionary<string, Dictionary<string, TerminalVisualization>> Read() => [];
        public void Write(Dictionary<string, Dictionary<string, TerminalVisualization>> value) { }
    }

    private readonly TimeSpan verdictTimeout = verdictTimeout ?? TimeSpan.FromSeconds(5);
    private readonly Lock gate = new();
    private readonly Dictionary<(string Workspace, string Tab), TerminalVisualization> byTerminal = [];
    private readonly Dictionary<(string Workspace, string Tab, int Revision), TaskCompletionSource<string?>> pendingVerdicts = [];
    private Action<VisualizationsChanged>? publish;
    private Func<string, string, string?> sessionOf = (_, _) => null;
    private IPersistence persisted = new Nowhere();

    internal void Connect(Action<VisualizationsChanged>? publisher, Func<string, string, string?>? sessionLookup, IPersistence? persistence = null)
    {
        lock (gate)
        {
            publish = publisher;
            sessionOf = sessionLookup ?? ((_, _) => null);
            if (persistence is not null) persisted = persistence;
        }
    }

    /// <summary>
    /// The renderer decides. Mermaid is parsed where it is drawn, so whether a diagram is valid is not something
    /// the host can answer: the client that draws it reports back, and the tool call resolves with that verdict.
    /// </summary>
    internal void ReportRender(string workspaceId, string tabKey, int revision, string? error)
    {
        TaskCompletionSource<string?>? pending;
        lock (gate)
            if (!pendingVerdicts.Remove((workspaceId, tabKey, revision), out pending)) return;
        pending.TrySetResult(error);
    }

    private async Task<string?> AwaitRenderVerdict(string workspaceId, string tabKey, int revision, CancellationToken cancellationToken)
    {
        var key = (workspaceId, tabKey, revision);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) pendingVerdicts[key] = pending;
        try { return await pending.Task.WaitAsync(verdictTimeout, cancellationToken); }
        catch (TimeoutException) { return null; }
        finally { lock (gate) if (pendingVerdicts.TryGetValue(key, out var current) && current == pending) pendingVerdicts.Remove(key); }
    }

    internal TerminalVisualization? Get(string workspaceId, string tabKey)
    {
        lock (gate) return byTerminal.GetValueOrDefault((workspaceId, tabKey));
    }

    internal VisualizationsChanged ForWorkspace(string workspaceId)
    {
        lock (gate) return Snapshot(workspaceId);
    }

    private VisualizationsChanged Snapshot(string workspaceId) => new(workspaceId,
        byTerminal.Where(entry => entry.Key.Workspace == workspaceId).ToDictionary(entry => entry.Key.Tab, entry => entry.Value));

    private void RememberForSession(string workspaceId, string sessionId, TerminalVisualization visualization)
    {
        var all = persisted.Read();
        if (!all.TryGetValue(workspaceId, out var sessions)) all[workspaceId] = sessions = [];
        sessions[sessionId] = visualization;
        persisted.Write(all);
    }

    /// <summary>
    /// A resumed conversation reclaims its drawing: resuming an agent session lands in whatever terminal the user
    /// opened, which is rarely the one that drew, so the session's last visualization is re-attached to the tab now
    /// reporting it and pushed as if just drawn. A tab that drew before it said which session it is binds what it
    /// has to that session now, so a later resume can find it.
    /// </summary>
    internal TerminalVisualization? AdoptForSession(string workspaceId, string tabKey, string sessionId)
    {
        lock (gate)
        {
            var stored = persisted.Read().GetValueOrDefault(workspaceId)?.GetValueOrDefault(sessionId);
            var current = byTerminal.GetValueOrDefault((workspaceId, tabKey));
            if (stored is null)
            {
                if (current is not null) RememberForSession(workspaceId, sessionId, current);
                return null;
            }
            if (current is not null && current.Revision >= stored.Revision) return null;
            byTerminal[(workspaceId, tabKey)] = stored;
            publish?.Invoke(Snapshot(workspaceId));
            return stored;
        }
    }

    internal void Forget(string workspaceId)
    {
        lock (gate)
        {
            foreach (var key in byTerminal.Keys.Where(key => key.Workspace == workspaceId).ToArray()) byTerminal.Remove(key);
            var all = persisted.Read();
            if (all.Remove(workspaceId)) persisted.Write(all);
        }
    }

    private void Restore(string workspaceId, string tabKey, TerminalVisualization visualization)
    {
        lock (gate)
        {
            byTerminal[(workspaceId, tabKey)] = visualization;
            publish?.Invoke(Snapshot(workspaceId));
        }
    }

    internal TerminalVisualization Record(string workspaceId, string tabKey, VisualizeParams parameters)
    {
        lock (gate)
        {
            var revision = (byTerminal.GetValueOrDefault((workspaceId, tabKey))?.Revision ?? 0) + 1;
            var title = string.IsNullOrEmpty(parameters.Title) ? parameters.Type == VisualizationType.Comparison ? "Comparison" : "Diagram" : parameters.Title;
            var visualization = new TerminalVisualization(title, JsonSerializer.SerializeToElement(parameters, PluginJson.Options), revision);
            byTerminal[(workspaceId, tabKey)] = visualization;
            if (sessionOf(workspaceId, tabKey) is { } sessionId) RememberForSession(workspaceId, sessionId, visualization);
            publish?.Invoke(Snapshot(workspaceId));
            return visualization;
        }
    }

    /// <summary>Runs the tool for one terminal: record the drawing, then await the client's render verdict.</summary>
    internal async Task<PluginToolResult> RunTool(TerminalRef owner, VisualizeParams arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.ShapeError() is { } shape) return new PluginToolResult(shape) { IsError = true };
        TerminalVisualization? previous;
        TerminalVisualization visualization;
        Task<string?> verdict;
        lock (gate)
        {
            previous = Get(owner.WorkspaceId, owner.TabKey);
            // An in-process client can report during publication; register its verdict before publishing.
            verdict = AwaitRenderVerdict(owner.WorkspaceId, owner.TabKey, (previous?.Revision ?? 0) + 1, cancellationToken);
            visualization = Record(owner.WorkspaceId, owner.TabKey, arguments);
        }
        var failure = await verdict;
        if (failure is not null)
        {
            // A drawing that does not render is not this terminal's view: the last one that did stands, so a typo
            // in an iteration does not cost the user the picture they had.
            if (previous is not null) Restore(owner.WorkspaceId, owner.TabKey, previous);
            return new PluginToolResult($"The diagram did not render: {failure}\nFix the mermaid source and call visualize again.") { IsError = true };
        }
        return new PluginToolResult($"Rendered \"{visualization.Title}\" in SharpRail (revision {visualization.Revision}). Call visualize again to update it in place.");
    }
}