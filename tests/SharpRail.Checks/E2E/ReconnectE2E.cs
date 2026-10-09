using System.Net;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Remote;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>A remote window behind <see cref="CutProxy"/>: what it re-reads when its host connection returns.</summary>
internal static class ReconnectE2E
{
    private const string Token = "reconnect-e2e";

    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "reconnect-e2e-git"));
        Generations(Path.Combine(root, "reconnect-generations"));
        Rows(Path.Combine(root, "reconnect-rows"));
    }

    private static Microsoft.AspNetCore.Builder.WebApplication Serve(string project)
    {
        var server = RemoteServer.Create(project, IPAddress.Loopback, 0, Token);
        Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
        return server;
    }

    private static int Port(Microsoft.AspNetCore.Builder.WebApplication server) =>
        new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;

    /// <summary>A workspace row whose branch moved while the client was away is read again; nothing pushes that change.</summary>
    private static void Rows(string directory)
    {
        var project = IsolatedGit.Repository(Path.Combine(directory, "sample-project"));
        var server = Serve(project);
        try
        {
            using var proxy = new CutProxy(Port(server));
            using var app = new E2eWorkspace(proxy.Endpoint, Token, project, project + "-profile", project);
            var state = app.Workbench.State;
            Until(() => app.Window.WorkspaceMounted && state.Connected);
            GoProjectHome(app);
            Until(() => app.Find<TextBlock>("BranchLabel").Text == "main");
            var created = CreateWorkspaceViaDialog(app);
            var branch = ItemText(app, created, "WorkspaceBranch");

            proxy.Cut();
            Until(() => !state.Connected);
            IsolatedGit.Run(created, "checkout", "-b", "moved-while-away");
            IsolatedGit.Run(project, "checkout", "-b", "default-moved");
            Settle(600);
            Require(ItemText(app, created, "WorkspaceBranch") == branch && WorktreePaths(app).SequenceEqual([created]), "A disconnected window keeps the rows it knows.");
            proxy.Allow();
            Until(() => state.Generation == 2 && ItemText(app, created, "WorkspaceBranch") == "moved-while-away" && ItemText(app, project, "WorkspaceBranch") == "default-moved");
            Require(WorktreePaths(app).SequenceEqual([created]) && Active(app, created), "Re-reading rows neither adds nor removes one, nor navigates.");
        }
        finally { Task.Run(() => server.StopAsync()).GetAwaiter().GetResult(); }
        Console.WriteLine("PASS known workspace rows are read again after a reconnect");
    }

    private static bool HasFile(E2eWorkspace app, string path) =>
        app.Find<TreeView>("FilesTree").GetLogicalDescendants().OfType<TreeViewItem>().Any(item => item.Tag is ProjectFile file && file.Path == path);

    private static void Generations(string directory)
    {
        var project = IsolatedGit.Repository(Path.Combine(directory, "sample-project"));
        var server = Serve(project);
        try
        {
            using var proxy = new CutProxy(Port(server));
            using var app = new E2eWorkspace(proxy.Endpoint, Token, project, project + "-profile", project);
            var state = app.Workbench.State;
            var changes = new List<(HostConnectionStatus, int)>();
            state.Connection.Changed += () => changes.Add((state.Connection.Status, state.Generation));
            Until(() => app.Window.WorkspaceMounted && state.Connected);
            Require(state.Generation == 1 && state.Connection.Status == HostConnectionStatus.Connected, "The first connection is generation 1.");
            app.Click(app.Find<Button>("Tab_files"));
            Until(() => HasFile(app, "README.md"));
            Until(() => state.Handshake is not null);
            Require(state.Supports(HostProtocol.ChangeWritePath) && !state.Supports(HostProtocol.Current + 1), "A connected host serves the features up to its version.");

            for (var generation = 2; generation <= 3; generation++)
            {
                proxy.Cut();
                Until(() => state.Connection.Status == HostConnectionStatus.Disconnected);
                Require(state.Generation == generation - 1, "Losing the host does not start a generation.");
                Require(!state.Supports(HostProtocol.ChangeWritePath), "No feature is assumed of a host that has not answered.");
                File.WriteAllText(Path.Combine(project, $"offline-{generation}.txt"), "written while disconnected\n");
                Settle(600);
                proxy.Allow();
                Until(() => state.Connected);
                Require(state.Generation == generation, "Each reconnect is one new generation.");
                Until(() => HasFile(app, $"offline-{generation}.txt"));
                File.WriteAllText(Path.Combine(project, $"online-{generation}.txt"), "written after reconnecting\n");
                Until(() => HasFile(app, $"online-{generation}.txt"));
            }
            Require(changes.Where(change => change is (HostConnectionStatus.Connected, > 1)).Select(change => change.Item2).SequenceEqual([2, 3]) &&
                changes.Count(change => change.Item1 == HostConnectionStatus.Disconnected) == 2,
                "The connection reports each transition once: " + string.Join(", ", changes));
            Require(app.Find<TextBlock>("ConnectionStatus").Text == "Remote", "The window shows the restored connection.");
        }
        finally { Task.Run(() => server.StopAsync()).GetAwaiter().GetResult(); }
        Console.WriteLine("PASS a remote window re-reads its workspace and watches again under each new connection generation");
    }
}