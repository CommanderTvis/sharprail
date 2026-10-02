using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Visualize.Host;

public sealed class VisualizeHost : PluginHostModule
{
    internal VisualizationStore Store { get; } = new();

    public override PluginContract Contract => VisualizeContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        Store.Connect(
            payload => context.Publish(VisualizeContract.Changed, payload),
            (workspaceId, tabKey) => context.AgentRecord(new TerminalRef(workspaceId, tabKey))?.SessionId,
            new Persistence(context));

        context.Method(VisualizeContract.Report, (report, _, _) =>
        {
            Store.ReportRender(report.WorkspaceId, report.TabKey, report.Revision, report.Error);
            return ValueTask.FromResult(new Ack(true));
        });
        context.Method(VisualizeContract.Get, (query, _, _) => ValueTask.FromResult(Store.ForWorkspace(query.WorkspaceId)));

        context.OnTerminal(change =>
        {
            if (change is TerminalAgentChanged { Record.SessionId: { Length: > 0 } sessionId } agent)
                Store.AdoptForSession(agent.Terminal.WorkspaceId, agent.Terminal.TabKey, sessionId);
        });
        context.OnWorkspace(change =>
        {
            if (change is WorkspaceRemoved removed) Store.Forget(removed.Id);
        });

        context.Tool(new PluginTool<VisualizeParams>("visualize", "Visualize", VisualizationStore.ToolDescription, async (arguments, call, cancellationToken) =>
            call.Terminal is { } terminal
                ? await Store.RunTool(terminal, arguments, cancellationToken)
                : new PluginToolResult("visualize is only available from a terminal.") { IsError = true }));

        return ValueTask.FromResult<PluginDisposer?>(() =>
        {
            Store.Connect(null, null);
            return ValueTask.CompletedTask;
        });
    }

    private sealed class Persistence(IPluginHostContext context) : VisualizationStore.IPersistence
    {
        public Dictionary<string, Dictionary<string, TerminalVisualization>> Read() =>
            context.ReadState<Dictionary<string, Dictionary<string, TerminalVisualization>>>(VisualizationStore.StateName, []);

        public void Write(Dictionary<string, Dictionary<string, TerminalVisualization>> value) => context.WriteState(VisualizationStore.StateName, value);
    }
}