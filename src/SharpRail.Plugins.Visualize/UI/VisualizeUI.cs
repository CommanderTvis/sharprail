using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Visualize.UI;

public sealed class VisualizeUI : PluginUIModule
{
    private const string Kind = "visualization";

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        var drawings = new Drawings();
        var subscriptions = new Dictionary<string, IDisposable>();

        // Availability subscribes to the workspace it is asked about, as the fork's useAvailable did per host.
        // Predicates have no unmount, so the activation owns each subscription and releases it on deactivation.
        bool Available(CompanionHost host)
        {
            if (!subscriptions.ContainsKey(host.WorkspaceId))
                subscriptions[host.WorkspaceId] = context.Subscribe(VisualizeContract.Changed, Apply, new VisualizationsQuery(host.WorkspaceId));
            return drawings.Get(host) is not null;
        }

        // A new or updated revision surfaces the drawing even if its pane was closed.
        void Apply(VisualizationsChanged payload)
        {
            var drawn = drawings.Set(payload);
            context.Invalidate();
            foreach (var tabKey in drawn) context.FocusCompanion(new CompanionHost(payload.WorkspaceId, tabKey), Kind);
        }

        context.Companion(new CompanionRegistration(Kind, "Visualization", "bar-chart-box-line", Available, host => new VisualizationPane(context, drawings, host))
        {
            InstanceTitle = host => drawings.Get(host)?.Title
        });
        return () =>
        {
            foreach (var subscription in subscriptions.Values) subscription.Dispose();
            subscriptions.Clear();
        };
    }

    /// <summary>What the host last published, per workspace and terminal tab.</summary>
    internal sealed class Drawings
    {
        private readonly Dictionary<string, IReadOnlyDictionary<string, TerminalVisualization>> byWorkspace = [];

        public event Action? Changed;

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