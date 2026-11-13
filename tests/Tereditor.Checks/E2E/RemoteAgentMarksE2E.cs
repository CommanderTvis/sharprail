using Avalonia.Controls;
using Avalonia.LogicalTree;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.Api;
using Tereditor.UI.Docking;
using Tereditor.UI.Rendering;

using static Tereditor.Checks.E2E.E2eWorkspace;
using static Tereditor.Checks.E2E.TerminalsE2E;
using static Tereditor.Checks.E2E.TerminalSessionsE2E;

namespace Tereditor.Checks.E2E;

/// <summary>
/// A remote client's rail marks an agent's terminal with the agent's icon as the host's own window does, also for
/// terminals the client never placed (a phone's Projects page lists the desktop's), in the wide frame and on phone pages.
/// </summary>
internal static class RemoteAgentMarksE2E
{
    internal static void Run(string root)
    {
        var project = IsolatedGit.Repository(Path.Combine(root, "remote-agent-marks"));
        using var host = new RemoteHost(project);
        using var desk = new E2eWorkspace(host.Address, Token, project, project + "-desk-profile", project, terminals: host.Client());
        Until(() => desk.Window.WorkspaceMounted);
        foreach (var plugin in new[] { "codex", "claude-code" })
        {
            host.State.ChangeAsync([HostStateChange.PluginSettings(plugin, "{\"enabled\":true,\"command\":\"/usr/bin/false\"}")]).AsTask().GetAwaiter().GetResult();
            Until(() => desk.Workbench.PluginRegistry.Active.Contains(plugin));
        }
        var workspace = CreateTerminalWorkspace(desk, "workspace-1");
        desk.Window.Layout.NewTerminal(desk.Center);
        Until(() => TerminalTabs(desk).Length == 2 && (host.Host.HostService!.Catalog.Workspaces.GetValueOrDefault(workspace)?.Count ?? 0) == 2);
        var marks = TerminalTabs(desk).Zip(new[] { ("claude", "asset:claude.svg"), ("codex", "asset:codex.svg") }, (tab, agent) => (tab.Id, agent.Item1, agent.Item2)).ToArray();
        foreach (var (tab, kind, _) in marks)
        {
            host.State.SetTerminalAgent(new TerminalRef(workspace, tab), new(kind, "/usr/bin/false"));
            Until(() => desk.Workbench.State.Current.TerminalAgents.Any(agent => agent.Terminal == new TerminalRef(workspace, tab) && agent.Record.Kind == kind));
        }
        foreach (var (tab, kind, asset) in marks)
            Until(() => desk.Window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "Tab_" + tab.Replace(':', '_').Replace('/', '_') && Drawn(button, asset)),
                $"The desktop client's own {kind} tab must wear its agent's icon.");

        foreach (var compact in new[] { false, true })
        {
            using var client = new E2eWorkspace(host.Address, Token, project, project + (compact ? "-phone" : "-wide") + "-profile", project, terminals: host.Client(), compact: compact,
                prepare: compact ? profile => { profile.Data.Windows[0].Layout = DockState.Preset("focus"); profile.Data.Windows[0].DefaultPreset = "focus"; }
            : null);
            Until(() => client.Window.WorkspaceMounted);
            if (compact) client.Click(client.Find<Button>("PageProjects"));
            var frame = compact ? "phone pages" : "wide frame";
            Until(() => Listed(client, workspace).Length == 2, $"The {frame} lists the workspace's terminals it never opened.");
            Require(client.Window.Layout.State.Workspaces.GetValueOrDefault(workspace) is null, $"The {frame} client must not have placed the listed terminals.");
            foreach (var (tab, kind, asset) in marks)
                Until(() => Listed(client, workspace).Any(row => Equals(row.Tag, tab) && Drawn(row, asset)),
                    $"The {frame} rail must mark the unplaced {kind} terminal with the agent's icon, as the host does.");
            Console.WriteLine($"PASS remote agent marks: the {frame} rail draws the Claude and Codex icons on host terminals it never placed");
        }
    }

    private static Button[] Listed(E2eWorkspace app, string workspace) => [.. app.Window.GetLogicalDescendants().OfType<ContentControl>()
        .Where(host => host.Name == "WorkspaceTabs" && Equals(host.Tag, workspace))
        .SelectMany(host => host.GetLogicalDescendants().OfType<Button>()).Where(button => button.Name == "WorkspaceTabPreview")];

    // The mark is there and its drawing arrived: a remote client reads the asset over the plugin RPC.
    private static bool Drawn(Control row, string asset) => row.GetLogicalDescendants().OfType<AssetIcon>()
        .Any(icon => Equals(icon.Tag, asset) && icon.FindControl<ContentControl>("Image")?.Content is Image);
}