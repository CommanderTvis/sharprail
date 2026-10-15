using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Checks.E2E;
using SharpRail.Plugins.BranchGraph;
using SharpRail.UI.Docking;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static class GitlessPaneChecks
{
    private const string Graph = "plugin:branch-graph:graph";
    private static readonly string[] GitTools = ["changes", "review", Graph];

    internal static void Run(string root)
    {
        var session = new LayoutSession();
        session.SetExtraTools([new(Graph, "Git Graph", "git-branch", "right", RequiresGit: true), new("agent", "Agent", "terminal", "right")]);
        session.RestoreTool(Graph);
        session.RestoreTool("agent");
        session.SwitchWorkspace("repository");
        var graphGroup = session.State.Groups.Single(group => group.Tools.Any(tab => tab.Id == Graph));
        session.Select(graphGroup.Id, Graph);
        var saved = session.State.Copy();
        session.SetGitAvailable(false);
        AssertBlocked(session);
        foreach (var tool in GitTools)
        {
            var group = session.State.Groups.First(item => item.Tools.Any(tab => tab.Id == tool));
            var selected = session.Selected(group.Id)?.Id;
            session.Select(group.Id, tool);
            session.Reseat(group.Id, tool);
            session.RestoreTool(tool);
            Require(session.Selected(group.Id)?.Id == selected, "Direct selection and restoration must not activate a Git-only pane.");
        }
        session.ApplyPreset(DockState.Preset("balanced"));
        AssertBlocked(session);
        var restored = new LayoutSession(saved);
        restored.SetExtraTools([new(Graph, "Git Graph", "git-branch", "right", RequiresGit: true)]);
        restored.SetGitAvailable(false);
        AssertBlocked(restored);
        restored.SetGitAvailable(true);
        Require(restored.Selected(graphGroup.Id)?.Id == Graph, "A saved Git pane selection must survive a gitless workspace without being shown there.");
        Require(session.Tools.Any(tool => tool.Id == "files") && session.Tools.Any(tool => tool.Id == "agent"), "Files and agent tools remain available without Git.");

        var fixture = Path.Combine(root, "gitless-panes");
        using var app = new E2eWorkspace(Path.Combine(fixture, "plain"), openFiles: false);
        Until(() => app.Workbench.PluginRegistry.Active.Contains(BranchGraphPlugin.Id));
        AssertWindowBlocked(app);
        foreach (var tool in GitTools) app.Window.RevealToolFor(app.Root, tool);
        AssertWindowBlocked(app);
        app.Window.Layout.ApplyPreset(DockState.Preset("balanced"));
        AssertWindowBlocked(app);

        var unborn = Path.Combine(fixture, "unborn");
        Directory.CreateDirectory(unborn);
        IsolatedGit.Run(unborn, "init", "-b", "main");
        var opening = app.Window.OpenProjectAsync(unborn);
        Until(() => opening.IsCompleted && GitTools.All(tool => app.Window.Layout.Tools.Any(item => item.Id == tool)));
        opening.GetAwaiter().GetResult();
        foreach (var tool in GitTools)
        {
            app.Window.RevealToolFor(unborn, tool);
            var group = app.Window.Layout.State.Groups.Single(item => item.Tools.Any(tab => tab.Id == tool));
            Require(app.Window.Layout.Selected(group.Id)?.Id == tool, "An initialized repository without commits must allow Git panes.");
        }
        opening = app.Window.OpenProjectAsync(app.Root);
        Until(() => opening.IsCompleted && app.Window.WorkspaceRoot == app.Root && !app.Window.Layout.Tools.Any(tool => GitTools.Contains(tool.Id)));
        opening.GetAwaiter().GetResult();
        AssertWindowBlocked(app);
        using var peer = app.NewWindow();
        Until(() => peer.Window.WorkspaceMounted && peer.Window.WorkspaceRoot == app.Root);
        AssertWindowBlocked(peer);
        Console.WriteLine("PASS gitless pane capability: catalog, rails, direct reveal, presets, saved selections, project switches and unborn repositories");
    }

    private static void AssertBlocked(LayoutSession session)
    {
        Require(!session.Tools.Any(tool => GitTools.Contains(tool.Id)), "Git-only panes must be absent from the reveal catalog without a repository.");
        Require(!session.State.Groups.SelectMany(group => session.Tabs(group.Id)).Any(tab => GitTools.Contains(tab.Id)), "Saved Git-only tabs must be withheld from pane projection.");
        Require(!session.State.Groups.Any(group => GitTools.Contains(session.Selected(group.Id)?.Id ?? "")), "A saved Git-only selection must not render without a repository.");
    }

    private static void AssertWindowBlocked(E2eWorkspace app)
    {
        AssertBlocked(app.Window.Layout);
        Require(!app.Window.GetLogicalDescendants().OfType<Button>().Any(button => GitTools.Any(tool => button.Name == "Tab_" + tool.Replace(':', '_'))), "The rail must have no Git-only entry in a gitless workspace.");
        Require(app.Window.Layout.Tools.Any(tool => tool.Id == "files") && app.Window.Layout.Tools.Any(tool => tool.Id == DockState.SpecsTool), "Files and Specs remain available without Git.");
    }
}