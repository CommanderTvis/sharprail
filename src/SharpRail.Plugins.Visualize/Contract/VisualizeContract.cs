using System.Text.Json;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Visualize;

/// <summary>
/// A visualization drawn by the agent running in a terminal: the MCP <c>visualize</c> tool's live view.
/// <see cref="Args"/> is the tool call's arguments verbatim, which the UI half renders with the kit's
/// visualization card. <see cref="Revision"/> bumps on every rewrite: the same terminal calling again updates
/// its view in place rather than opening a second one.
/// </summary>
public sealed record TerminalVisualization(string Title, JsonElement Args, int Revision);

// The snapshot method (get) and the channel it feeds share this exact shape: the app's state-channel hydration
// passes a snapshot result straight through as the channel payload, and the channel's key needs workspaceId at
// the payload's top level to scope on.
public sealed record VisualizationsChanged(string WorkspaceId, IReadOnlyDictionary<string, TerminalVisualization> Visualizations);

public sealed record VisualizationsQuery(string WorkspaceId);

/// <summary>What the client that drew a revision made of it: no error when it rendered.</summary>
public sealed record RenderReport(string WorkspaceId, string TabKey, int Revision)
{
    public string? Error { get; init; }
}

public sealed record Ack(bool Ok);

public static class VisualizeContract
{
    public const string Id = "visualize";

    public static readonly PluginMethod<RenderReport, Ack> Report = new("report");
    public static readonly PluginMethod<VisualizationsQuery, VisualizationsChanged> Get = new("get");
    public static readonly PluginChannel<VisualizationsChanged> Changed = PluginChannel<VisualizationsChanged>.State("changed", Get, "workspaceId");

    public static readonly PluginContract Contract = PluginContract.Create(Id, 1, [Report, Get], [Changed]);

    public static readonly PluginManifest Manifest = new(Id, "Visualize", "bar-chart-box-line", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Gives the terminal agent a live drawing surface.",
        EnabledByDefault = true,
        Host = "SharpRail.Plugins.Visualize.Host.dll",
        Ui = "SharpRail.Plugins.Visualize.UI.dll"
    };
}