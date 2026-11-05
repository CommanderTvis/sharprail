using System.Text.Json.Serialization;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Processes;

/// <summary>
/// One row of the process tree. <paramref name="Kind"/> is group, workspace, language, agent, parent (a spawn's starter), spawned or owned.
/// Cpu is percent of one core, so several busy cores exceed 100; a group's values are the sum of the rows under it.
/// </summary>
/// <param name="Id">Stable across snapshots; the stop methods take it.</param>
/// <param name="Detail">The tooltip: pid, workspace and command.</param>
/// <param name="Workspace">The id (root path) of the workspace the row belongs to, so a client can scope the tree to one project; null on the three top groups.</param>
/// <param name="Trend">The last CPU samples, oldest first, at most <see cref="ProcessesPlugin.TrendLength"/>.</param>
public sealed record ProcessNode(string Id, string Kind, string Label, string Detail, int? Pid, double Cpu, long MemoryBytes,
    IReadOnlyList<double> Trend, IReadOnlyList<ProcessNode> Children, bool CanStop, string? Workspace = null);

/// <param name="Sequence">Increases with every fresh sample.</param>
public sealed record ProcessSnapshot(long Sequence, IReadOnlyList<ProcessNode> Groups, double TotalCpu, long TotalMemoryBytes);

public sealed record ProcessSnapshotParams;

/// <param name="Id">A node's id: a row, or a group or workspace node to stop everything below it.</param>
public sealed record ProcessStopParams(string Id);

/// <param name="Stopped">How many processes or language servers were asked to stop.</param>
public sealed record ProcessStopResult(int Stopped);

/// <param name="Pid">The process id; for a command that starts children, the leader of its process group.</param>
/// <param name="Description">A few words for the user: what the process is.</param>
public sealed record OwnedProcessReportParams(int Pid, string? Description = null);

/// <param name="Pid">The pid that was reported.</param>
public sealed record OwnedProcessReleaseParams(int Pid);

/// <summary>The plugin's settings (Settings › Processes).</summary>
public sealed record ProcessesSettings
{
    /// <summary>Whether stopping an agent row also stops every process forked from it; off, only the agent's own process is stopped.</summary>
    public bool StopAgentChildren { get; set; } = true;

    /// <summary>The pane's "All projects" check box as the user left it, kept across restarts; false shows only the pane's own project.</summary>
    public bool ShowAllProjects { get; init; }
}

public static class ProcessesContract
{
    /// <summary>Samples now (at most once a second; a closer call returns the last sample). Only a visible pane calls it, so nothing samples while it is hidden.</summary>
    public static readonly PluginMethod<ProcessSnapshotParams, ProcessSnapshot> Snapshot = new("snapshot");
    public static readonly PluginMethod<ProcessStopParams, ProcessStopResult> Stop = new("stop");
    public static readonly PluginContract Contract = PluginContract.Create<ProcessesSettings>(ProcessesPlugin.Id, 1, [Snapshot, Stop], [], ProcessesJson.Default);
}

public static class ProcessesPlugin
{
    public const string Id = "processes";
    public const string Tool = "processes";
    public const int TrendLength = 30;
    public const string ReportTool = "owned_process_report";
    public const string ReleaseTool = "owned_process_release";

    public static readonly PluginManifest Manifest = new(Id, "Processes", "node-tree", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Agents,
        Description = "Shows the language servers, agents and spawned agents SharpRail runs, with CPU and memory, and stops the processes agents report as theirs.",
        EnabledByDefault = true,
        Host = "SharpRail.Plugins.Processes.Host.dll",
        Ui = "SharpRail.Plugins.Processes.UI.dll",
        Contributes = new()
        {
            SideTools = [new(Tool, "Processes", "node-tree", PluginToolSide.Right)]
        }
    };
}

[JsonSerializable(typeof(ProcessesSettings))]
[JsonSerializable(typeof(ProcessSnapshotParams))]
[JsonSerializable(typeof(ProcessSnapshot))]
[JsonSerializable(typeof(ProcessStopParams))]
[JsonSerializable(typeof(ProcessStopResult))]
[JsonSerializable(typeof(OwnedProcessReportParams))]
[JsonSerializable(typeof(OwnedProcessReleaseParams))]
internal sealed partial class ProcessesJson : JsonSerializerContext;