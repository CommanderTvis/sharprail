using System.Net;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Remote;
using SharpRail.UI.State;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks.E2E;

/// <summary>
/// Upstream cases where several clients share one host. A second window of the same app stands in
/// for another in-process client; a real gRPC host behind <see cref="CutProxy"/> stands in for
/// remote clients whose socket drops and reconnects.
/// </summary>
internal static class MultiClientE2E
{
    private const string Token = "multi-client-test";

    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "multi-client-git"));
        RemovalPropagates(Path.Combine(root, "sync-removal"));
        CreationPropagates(Path.Combine(root, "sync-creation"));
        RenamePropagates(Path.Combine(root, "sync-rename"));
        RenameSurvivesReconnect(Path.Combine(root, "sync-rename-reconnect"));
        HandshakeSurvivesReconnect(Path.Combine(root, "sync-handshake-reconnect"));
        TransientReadFailure(Path.Combine(root, "sync-transient-read"));
        WindowsKeepPlacement(Path.Combine(root, "sync-placement"));
    }

    private sealed class RemoteHost : IDisposable
    {
        private readonly WebApplication server;
        internal int Port { get; }

        internal RemoteHost(string root)
        {
            server = RemoteServer.Create(root, IPAddress.Loopback, 0, Token);
            Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
            Port = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
        }

        internal Uri Endpoint => new($"http://127.0.0.1:{Port}");

        public void Dispose() => Task.Run(() => server.StopAsync()).GetAwaiter().GetResult();
    }

    private static string FixtureProject(string directory)
    {
        var project = IsolatedGit.Repository(Path.Combine(directory, "sample-project"));
        return project;
    }

    private static E2eWorkspace RemoteClient(Uri endpoint, string project, string profile, string? startPath = null)
    {
        var client = new E2eWorkspace(endpoint, Token, project, profile, startPath ?? project);
        if (startPath is null)
        {
            Until(() => client.Window.WorkspaceMounted);
            GoProjectHome(client);
            Until(() => client.Find<TextBlock>("BranchLabel").Text == "main");
        }
        return client;
    }

    private static string Status(E2eWorkspace app) => app.Find<TextBlock>("ConnectionStatus").Text ?? "";

    private static TextBox StartRename(E2eWorkspace app, string workspace)
    {
        Choose(app, OpenWorkspaceMenu(app, workspace), "WorkspaceRename");
        Until(() => Item(app, workspace).GetLogicalChildren().OfType<TextBox>().Any(box => box.IsFocused));
        return RenameInput(app, workspace)!;
    }

    /// <summary>The row's displayed name, or null while its rename input replaces it.</summary>
    private static string? Shown(E2eWorkspace app, string workspace) =>
        Item(app, workspace).GetLogicalDescendants().OfType<TextBlock>().SingleOrDefault(text => text.Name == "WorkspaceName")?.Text;

    private static TextBox? RenameInput(E2eWorkspace app, string workspace) =>
        Item(app, workspace).GetLogicalChildren().OfType<TextBox>().SingleOrDefault();

    private static void RemovalPropagates(string directory)
    {
        using var app = OpenFixtureProject(directory);
        var created = CreateWorkspaceViaDialog(app);
        Until(() => WorktreePaths(app).Count() == 1);
        using var peer = app.NewWindow();
        Until(() => WorktreePaths(peer).Count() == 1);
        peer.Click(Select(peer, created));
        Until(() => Active(peer, created));

        RemoveWorkspace(app, created);
        Until(() => !WorktreePaths(app).Any());
        Until(() => !WorktreePaths(peer).Any() && peer.Window.AtProjectHome && HasWelcome(peer));
        var toast = peer.Find<Border>("GestureToast");
        Require(toast.IsVisible && peer.Find<TextBlock>("GestureToastMessage").Text!.Contains(Path.GetFileName(created), StringComparison.Ordinal),
            "The peer window must say which workspace was removed.");
        Console.WriteLine("PASS upstream workspace-lifecycle.spec.ts: workspace removal propagates — no zombie row in a second tab");
    }

    private static void CreationPropagates(string directory)
    {
        using var app = OpenFixtureProject(directory);
        using var peer = app.NewWindow();
        Until(() => peer.Window.AtProjectHome && peer.Window.ProjectRoot == app.Root && HasWelcome(peer));
        Require(!WorktreePaths(peer).Any(), "The peer starts without workspace rows.");
        var created = CreateWorkspaceViaDialog(app);
        Require(WorktreePaths(app).Count() == 1, "The creating window lists the workspace.");
        Until(() => WorktreePaths(peer).SequenceEqual([created]));
        Console.WriteLine("PASS upstream workspace-lifecycle.spec.ts: workspace creation propagates to a second tab's rail");
    }

    private static void RenamePropagates(string directory)
    {
        var project = FixtureProject(directory);
        using var host = new RemoteHost(project);
        using var proxy = new CutProxy(host.Port);
        using var source = RemoteClient(host.Endpoint, project, project + "-source-profile");
        var created = CreateWorkspaceViaDialog(source);
        var name = Path.GetFileName(created);
        var branch = ItemText(source, created, "WorkspaceBranch");
        using var peer = RemoteClient(proxy.Endpoint, project, project + "-peer-profile");
        Until(() => WorktreePaths(peer).Contains(created));
        var peerInput = StartRename(peer, created);
        Require(peerInput.Text == name, "The peer's rename draft starts from the current name.");

        var input = StartRename(source, created);
        input.Text = "Shared Rename";
        Press(input, Key.Enter);
        Until(() => Shown(source, created) == "Shared Rename");
        Until(() => RenameInput(peer, created)?.Text == name);
        Require(ReferenceEquals(RenameInput(peer, created), peerInput), "A shared rename must retain the peer's existing input and draft.");
        Require(peerInput.Focus(), "The peer's retained rename input must accept focus before its Enter key.");
        Press(peerInput, Key.Enter);
        Until(() => RenameInput(peer, created) is null && Shown(peer, created) == "Shared Rename");
        Require(ItemText(source, created, "WorkspaceName") == "Shared Rename", "An unchanged peer draft must not overwrite the shared name.");

        proxy.Cut();
        Until(() => Status(peer) != "Remote");
        input = StartRename(source, created);
        input.Text = "Offline Rename";
        Press(input, Key.Enter);
        Until(() => Shown(source, created) == "Offline Rename");
        Settle(300);
        Require(ItemText(peer, created, "WorkspaceName") == "Shared Rename", "A disconnected peer keeps its last snapshot.");

        proxy.Allow();
        Until(() => Status(peer) == "Remote" && Shown(peer, created) == "Offline Rename");
        Require(proxy.Accepted > 1, "The peer must have reconnected.");
        foreach (var app in new[] { source, peer })
            Require(ItemText(app, created, "WorkspaceName") == "Offline Rename" && ItemText(app, created, "WorkspaceBranch") == branch,
                "Both clients converge on the name the peer missed, with the branch unchanged.");
        Console.WriteLine("PASS upstream workspace-lifecycle.spec.ts: workspace rename propagates live and rehydrates a tab that missed a later snapshot");
    }

    private static void RenameSurvivesReconnect(string directory)
    {
        var project = FixtureProject(directory);
        using var host = new RemoteHost(project);
        using var proxy = new CutProxy(host.Port);
        using var app = RemoteClient(proxy.Endpoint, project, project + "-profile");
        var created = CreateWorkspaceViaDialog(app);
        var branch = ItemText(app, created, "WorkspaceBranch");
        StartRename(app, created);

        proxy.Cut();
        Until(() => Status(app) != "Remote");
        Require(RenameInput(app, created) is not null, "The open rename survives the disconnect.");
        RenameInput(app, created)!.Text = "Rename After Reconnect";
        Press(RenameInput(app, created)!, Key.Enter);
        Settle(300);
        Require(RenameInput(app, created)?.Text == "Rename After Reconnect", "An offline commit keeps the input open with its draft.");

        proxy.Allow();
        Until(() => Status(app) == "Remote" && RenameInput(app, created) is null && Shown(app, created) == "Rename After Reconnect");
        Require(proxy.Accepted > 1 && ItemText(app, created, "WorkspaceBranch") == branch, "The rename commits after reconnecting, with the branch unchanged.");
        Console.WriteLine("PASS upstream workspace-actions.spec.ts: an open inline rename survives reconnect");
    }

    private static void HandshakeSurvivesReconnect(string directory)
    {
        var project = FixtureProject(directory);
        using var host = new RemoteHost(project);
        using var proxy = new CutProxy(host.Port);
        using var app = RemoteClient(proxy.Endpoint, project, project + "-profile");
        var state = app.Workbench.State;
        Until(() => state.Handshake is { ProtocolVersion: HostProtocol.Current });

        proxy.Cut();
        Until(() => state.Handshake is null);
        proxy.Allow();
        Until(() => state.Handshake is { ProtocolVersion: HostProtocol.Current });
        Console.WriteLine("PASS host handshake is cleared when the connection drops and fetched again on reconnect");
    }

    private static void TransientReadFailure(string directory)
    {
        var project = FixtureProject(directory);
        var profile = project + "-profile";
        using var host = new RemoteHost(project);
        using var proxy = new CutProxy(host.Port);
        string workspace;
        using (var app = RemoteClient(proxy.Endpoint, project, profile))
            workspace = CreateWorkspaceViaDialog(app);
        Require(new ProfileStore(profile).Data.Windows[0].LastProject == workspace, "The window remembers its workspace.");

        proxy.Cut();
        using var reloaded = RemoteClient(proxy.Endpoint, project, profile, workspace);
        Until(() => Status(reloaded) == "Error");
        Require(!reloaded.Window.WorkspaceMounted && new ProfileStore(profile).Data.Windows[0].LastProject == workspace,
            "A failed restore read must keep the remembered workspace.");
        proxy.Allow();
        Until(() => Active(reloaded, workspace) && Status(reloaded) == "Remote");
        Until(() => WorktreePaths(reloaded).Contains(workspace));
        Require(new ProfileStore(profile).Data.Windows[0].LastProject == workspace, "The restored window keeps its location.");
        Console.WriteLine("PASS upstream reload-navigation.spec.ts: a transient workspace read failure preserves the URL and restores after reconnect");
    }

    private static void WindowsKeepPlacement(string directory)
    {
        using var app = new E2eWorkspace(IsolatedGit.Repository(Path.Combine(directory, "sample-project")), openFiles: false);
        static int Terminals(E2eWorkspace app) => TerminalsE2E.TerminalTabs(app).Length;
        Until(() => Terminals(app) == 1);
        using var peer = app.NewWindow();
        peer.Click(Select(peer, app.Root));
        Until(() => peer.Window.WorkspaceMounted && !peer.Window.AtProjectHome && peer.Window.WorkspaceRoot == app.Root);
        Until(() => Terminals(peer) == 1);
        var bottom = peer.Window.Layout.State.Groups.First(group => group.Region == "bottom" && peer.Window.Layout.Tabs(group.Id).Any(tab => tab.Kind == "terminal"));
        peer.Click(peer.Find<Button>("NewTerminal_" + bottom.Id));
        Until(() => Terminals(peer) == 2);
        Settle(200);
        Require(Terminals(app) == 1, "A terminal opened in one window must not add a tab to another.");

        app.Click(app.Find<Button>("Tab_files"));
        app.Open("README.md", keep: true);
        Require(!peer.Window.Layout.State.Workspaces.Values.SelectMany(view => view.Documents.Values).SelectMany(tabs => tabs).Any(tab => tab.Path == "README.md"),
            "A file opened in one window must not appear in another.");
        peer.Click(peer.Find<Button>("Tab_files"));
        peer.Open("README.md", keep: true);
        peer.Click(peer.Find<Control>("DockTab_markdown_README.md").GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "CloseTab"));
        Until(() => !peer.Tabs.Any(tab => tab.Path == "README.md"));
        Require(app.Tabs.Any(tab => tab.Path == "README.md"), "Closing a window's file tab must keep the other window's tab.");
        Console.WriteLine("PASS upstream layout.spec.ts: frontend windows keep chat and file placement independent (terminal and file placement; chat excluded)");
    }
}