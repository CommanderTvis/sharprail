using System.Text.Json;
using System.Text.Json.Nodes;

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
        "Render a rich visualization in the SharpRail workbench, in a live view beside this terminal (in a Visualization tab when you run in a chat) — " +
        "instead of ASCII art or a plain markdown table. Four kinds, chosen by `type`: 'diagram' renders a " +
        "mermaid diagram (set `mermaid` to raw mermaid source of any kind — flowchart, sequenceDiagram, " +
        "classDiagram, stateDiagram, erDiagram, gantt); 'svg' renders hand-written SVG (set `svg` to a " +
        "complete <svg> document; scripts, event handlers, foreignObject, animation and any reference other than #id or " +
        "a data: raster image are refused, as is anything over 2 MB or 10000 elements; use currentColor or var(--fg), " +
        "var(--bg), var(--accent), var(--muted), var(--border) for colours so it reads in light and dark); 'markdown' renders Markdown text (set `markdown`) with the app's Markdown preview, including tables and code; 'comparison' renders side-by-side option cards " +
        "(set `options` to the alternatives, each with pros/cons, an optional `recommended` flag, and an " +
        "optional inline `mermaid` or `svg`). Calling again replaces this terminal's view in place, so you can " +
        "iterate on a diagram and the user watches it evolve. visualize_read returns what this view shows now and " +
        "what the user selected in it.";

    internal const string ReadDescription =
        "Read this terminal's live visualize view as JSON: whether something is shown, its revision, type and title, " +
        "the `mermaid`, `svg` or `markdown` source or comparison `options` exactly as last drawn, the render verdict (render: ok, error " +
        "with renderError, or pending when no pane has reported), the last refused attempt (rejected) if the latest " +
        "call did not render, and the text the user has selected in the pane (selection, null when none). Use it " +
        "before editing a drawing you no longer have verbatim, or when the user refers to \"this node\" or " +
        "\"the selected option\". Only this terminal's own view is readable.";

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
    private readonly Dictionary<(string Workspace, string Tab), Verdict> verdicts = [];
    private readonly Dictionary<(string Workspace, string Tab), (int Revision, string Text)> selections = [];
    private readonly Dictionary<(string Workspace, string Tab), (int Revision, string Error)> rejected = [];
    private Action<VisualizationsChanged>? publish;

    /// <summary>What a client reported for a revision: no error when it rendered.</summary>
    private sealed record Verdict(int Revision, string? Error);
    private Func<string, string, string?> sessionOf = (_, _) => null;
    private Func<string, string, bool> isTerminal = (_, _) => true;
    private IPersistence persisted = new Nowhere();

    /// <param name="terminalLookup">Whether a workspace and tab key name a live terminal; a caller that is none (a chat) gets a detached drawing. Unset, every caller is a terminal.</param>
    internal void Connect(Action<VisualizationsChanged>? publisher, Func<string, string, string?>? sessionLookup, IPersistence? persistence = null, Func<string, string, bool>? terminalLookup = null)
    {
        lock (gate)
        {
            publish = publisher;
            sessionOf = sessionLookup ?? ((_, _) => null);
            isTerminal = terminalLookup ?? ((_, _) => true);
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
        {
            // Also kept for visualize_read, for the shown revision only, including one reported after the wait ended.
            if (byTerminal.GetValueOrDefault((workspaceId, tabKey))?.Revision == revision) verdicts[(workspaceId, tabKey)] = new Verdict(revision, error);
            if (!pendingVerdicts.Remove((workspaceId, tabKey, revision), out pending)) return;
        }
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

    /// <summary>What the user has selected in a terminal's pane; a selection of an older revision is stale and dropped.</summary>
    internal void ReportSelection(string workspaceId, string tabKey, int revision, string? text)
    {
        lock (gate)
        {
            if (byTerminal.GetValueOrDefault((workspaceId, tabKey))?.Revision != revision) return;
            if (string.IsNullOrEmpty(text)) selections.Remove((workspaceId, tabKey));
            else selections[(workspaceId, tabKey)] = (revision, text);
        }
    }

    /// <summary>The visualize_read answer for one terminal: its shown drawing, verdict, last refusal and selection.</summary>
    internal JsonObject Read(string workspaceId, string tabKey)
    {
        lock (gate)
        {
            var key = (workspaceId, tabKey);
            var result = new JsonObject();
            var current = byTerminal.GetValueOrDefault(key);
            result["shown"] = current is not null;
            if (current is not null)
            {
                result["revision"] = current.Revision;
                result["title"] = current.Title;
                // The arguments as last drawn (type, the explicit title if any, mermaid or options), field for field.
                if (JsonNode.Parse(current.Args.GetRawText()) is JsonObject args)
                    foreach (var (name, value) in args.ToArray())
                    {
                        args.Remove(name);
                        if (name == "title") result["requestedTitle"] = value;
                        else result[name] = value;
                    }
                var verdict = verdicts.GetValueOrDefault(key) is { } reported && reported.Revision == current.Revision ? reported : null;
                result["render"] = verdict is null ? "pending" : verdict.Error is null ? "ok" : "error";
                if (verdict?.Error is { } error) result["renderError"] = error;
                result["selection"] = selections.GetValueOrDefault(key) is { Text: { } text } selected && selected.Revision == current.Revision ? text : null;
            }
            if (rejected.GetValueOrDefault(key) is { Error: { } refusal } last)
                result["rejected"] = new JsonObject { ["revision"] = last.Revision, ["error"] = refusal };
            return result;
        }
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
            // Whatever tab adopts a drawing is a terminal.
            if (stored.Detached) stored = stored with { Detached = false };
            byTerminal[(workspaceId, tabKey)] = stored;
            publish?.Invoke(Snapshot(workspaceId));
            return stored;
        }
    }

    internal void Forget(string workspaceId)
    {
        lock (gate)
        {
            foreach (var key in byTerminal.Keys.Where(key => key.Workspace == workspaceId).ToArray())
            {
                byTerminal.Remove(key);
                verdicts.Remove(key);
                selections.Remove(key);
                rejected.Remove(key);
            }
            var all = persisted.Read();
            if (all.Remove(workspaceId)) persisted.Write(all);
        }
    }

    private void Restore(string workspaceId, string tabKey, TerminalVisualization visualization, Verdict? verdict)
    {
        lock (gate)
        {
            byTerminal[(workspaceId, tabKey)] = visualization;
            if (verdict is null) verdicts.Remove((workspaceId, tabKey));
            else verdicts[(workspaceId, tabKey)] = verdict;
            publish?.Invoke(Snapshot(workspaceId));
        }
    }

    internal TerminalVisualization Record(string workspaceId, string tabKey, VisualizeParams parameters, bool detached = false)
    {
        lock (gate)
        {
            var revision = (byTerminal.GetValueOrDefault((workspaceId, tabKey))?.Revision ?? 0) + 1;
            var title = string.IsNullOrEmpty(parameters.Title) ? parameters.Type switch { VisualizationType.Comparison => "Comparison", VisualizationType.Svg => "SVG", VisualizationType.Markdown => "Markdown", _ => "Diagram" } : parameters.Title;
            var visualization = new TerminalVisualization(title, JsonSerializer.SerializeToElement(parameters, PluginJson.Options), revision) { Detached = detached };
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
        // Asked of the host outside the gate: its terminal list has a lock of its own.
        var detached = !isTerminal(owner.WorkspaceId, owner.TabKey);
        TerminalVisualization? previous;
        Verdict? previousVerdict;
        TerminalVisualization visualization;
        Task<string?> verdict;
        lock (gate)
        {
            previous = Get(owner.WorkspaceId, owner.TabKey);
            previousVerdict = verdicts.GetValueOrDefault((owner.WorkspaceId, owner.TabKey));
            // An in-process client can report during publication; register its verdict before publishing.
            verdict = AwaitRenderVerdict(owner.WorkspaceId, owner.TabKey, (previous?.Revision ?? 0) + 1, cancellationToken);
            visualization = Record(owner.WorkspaceId, owner.TabKey, arguments, detached);
        }
        var failure = await verdict;
        if (failure is not null)
        {
            // A drawing that does not render is not this terminal's view: the last one that did stands, so a typo
            // in an iteration does not cost the user the picture they had.
            lock (gate) rejected[(owner.WorkspaceId, owner.TabKey)] = (visualization.Revision, failure);
            if (previous is not null) Restore(owner.WorkspaceId, owner.TabKey, previous, previousVerdict);
            return new PluginToolResult($"The diagram did not render: {failure}\nFix the {(arguments.Type == VisualizationType.Svg ? "SVG" : "mermaid")} source and call visualize again.") { IsError = true };
        }
        lock (gate) rejected.Remove((owner.WorkspaceId, owner.TabKey));
        return new PluginToolResult($"Rendered \"{visualization.Title}\" in {(visualization.Detached ? "a Visualization tab" : "SharpRail")} (revision {visualization.Revision}). Call visualize again to update it in place.");
    }
}