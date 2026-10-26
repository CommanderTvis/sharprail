using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Visualize.UI;

public sealed class VisualizeUI : PluginUIModule
{
    private const string Kind = "visualization";

    /// <summary>The document scheme of a drawing whose caller runs no terminal (a chat): one tab per workspace and tab key.</summary>
    private const string DocumentScheme = "visualization";

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        var drawings = new Drawings();
        var subscriptions = new Dictionary<string, IDisposable>();
        // The detached drawings that have a document tab: a new revision updates that tab in place and never refocuses it.
        // Opening a tab only works in the active window's workspace, so a drawing made elsewhere waits for it to become active.
        var tabs = new HashSet<(string Workspace, string Key)>();
        var waiting = new HashSet<(string Workspace, string Key)>();

        // Availability subscribes to the workspace it is asked about, as the fork's useAvailable did per host.
        // Predicates have no unmount, so the activation owns each subscription and releases it on deactivation.
        void Watch(string workspaceId)
        {
            if (!subscriptions.ContainsKey(workspaceId))
                subscriptions[workspaceId] = context.Subscribe(VisualizeContract.Changed, Apply, new VisualizationsQuery(workspaceId));
        }

        bool Available(CompanionHost host)
        {
            Watch(host.WorkspaceId);
            return drawings.Get(host) is not null;
        }

        // A new or updated revision surfaces the drawing even if its pane was closed. A workspace's first snapshot is
        // what the host already held, so it only refocuses companions; a detached drawing's tab is opened by a call
        // made while this UI watches, and restores with the layout otherwise.
        void Apply(VisualizationsChanged payload)
        {
            var snapshot = !drawings.Knows(payload.WorkspaceId);
            var drawn = drawings.Set(payload);
            context.Invalidate();
            foreach (var tabKey in drawn)
            {
                var drawing = payload.Visualizations[tabKey];
                if (!drawing.Detached) context.FocusCompanion(new CompanionHost(payload.WorkspaceId, tabKey), Kind);
                else if (!snapshot) Show(payload.WorkspaceId, tabKey, drawing.Title);
            }
        }

        void Show(string workspaceId, string tabKey, string title)
        {
            if (tabs.Contains((workspaceId, tabKey))) return;
            if (context.Host().ActiveWorkspaceId != workspaceId) { waiting.Add((workspaceId, tabKey)); return; }
            waiting.Remove((workspaceId, tabKey));
            tabs.Add((workspaceId, tabKey));
            _ = OpenAsync(workspaceId, tabKey, title);
        }

        async Task OpenAsync(string workspaceId, string tabKey, string title)
        {
            try { await context.OpenDocumentAsync(workspaceId, DocumentScheme, tabKey, title); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                context.Notify(PluginNotificationKind.Error, "Couldn't open the visualization", error.Message);
            }
        }

        // A chat has no terminal to be a companion of, and a workspace may hold chats alone: watch every workspace.
        void WatchAll(PluginHostProjection host)
        {
            foreach (var workspace in host.Workspaces.Values.SelectMany(workspaces => workspaces)) Watch(workspace.Id);
        }

        WatchAll(context.Host());
        var watching = new CompositeDisposable(
            context.WatchHost(host => host.Workspaces, (_, _) => WatchAll(context.Host())),
            context.WatchHost(host => host.ActiveWorkspaceId, (active, _) =>
            {
                foreach (var (workspaceId, tabKey) in waiting.Where(entry => entry.Workspace == active).ToArray())
                    if (drawings.Get(new CompanionHost(workspaceId, tabKey)) is { } drawing) Show(workspaceId, tabKey, drawing.Title);
                    else waiting.Remove((workspaceId, tabKey));
            }));

        context.Document(new PluginDocumentRegistration(DocumentScheme, props =>
        {
            // A restored tab brings its workspace's snapshot with it.
            tabs.Add((props.WorkspaceId, props.Id));
            Watch(props.WorkspaceId);
            return new VisualizationPane(context, drawings, new CompanionHost(props.WorkspaceId, props.Id), "No visualization");
        })
        {
            Icon = "bar-chart-box-line",
            // The drawing stays in the store (visualize_read still answers it); the next revision opens a tab again.
            Closed = props => tabs.Remove((props.WorkspaceId, props.Id))
        });

        context.Companion(new CompanionRegistration(Kind, "Visualization", "bar-chart-box-line", Available, host => new VisualizationPane(context, drawings, host))
        {
            InstanceTitle = host => drawings.Get(host)?.Title
        });
        return () =>
        {
            watching.Dispose();
            tabs.Clear(); waiting.Clear();
            foreach (var subscription in subscriptions.Values) subscription.Dispose();
            subscriptions.Clear();
        };
    }

    private sealed class CompositeDisposable(params IDisposable[] items) : IDisposable
    {
        public void Dispose() { foreach (var item in items) item.Dispose(); }
    }

    /// <summary>What the host last published, per workspace and terminal tab.</summary>
    internal sealed class Drawings
    {
        private readonly Dictionary<string, IReadOnlyDictionary<string, TerminalVisualization>> byWorkspace = [];

        public event Action? Changed;

        public bool Knows(string workspaceId) => byWorkspace.ContainsKey(workspaceId);

        public TerminalVisualization? Get(CompanionHost host) =>
            byWorkspace.GetValueOrDefault(host.WorkspaceId)?.GetValueOrDefault(host.TabKey);

        /// <summary>Replaces a workspace's drawings and returns the tabs whose revision is new.</summary>
        public IReadOnlyList<string> Set(VisualizationsChanged payload)
        {
            var previous = byWorkspace.GetValueOrDefault(payload.WorkspaceId);
            byWorkspace[payload.WorkspaceId] = payload.Visualizations;
            Changed?.Invoke();
            return [.. payload.Visualizations.Where(entry => previous?.GetValueOrDefault(entry.Key)?.Revision != entry.Value.Revision).Select(entry => entry.Key)];
        }
    }

}