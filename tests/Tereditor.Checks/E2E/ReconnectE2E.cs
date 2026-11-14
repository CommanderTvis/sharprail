using System.Net;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Client;
using Tereditor.Host.Remote;

using static Tereditor.Checks.E2E.E2eWorkspace;
using static Tereditor.Checks.E2E.WorkspaceFixture;

namespace Tereditor.Checks.E2E;

/// <summary>A remote window behind <see cref="CutProxy"/>: what it re-reads when its host connection returns.</summary>
internal static class ReconnectE2E
{
    private const string Token = "reconnect-e2e";

    internal static void Run(string root)
    {
        using var git = new IsolatedGit(Path.Combine(root, "reconnect-e2e-git"));
        Generations(Path.Combine(root, "reconnect-generations"));
        Refused(Path.Combine(root, "reconnect-refused"));
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

    /// <summary>A host that refuses this client's token is a final failure: no retry fixes it, so the header shows Error, not the loader.</summary>
    private static void Refused(string directory)
    {
        var project = IsolatedGit.Repository(Path.Combine(directory, "sample-project"));
        var server = Serve(project);
        try
        {
            using var app = new E2eWorkspace(new Uri($"http://127.0.0.1:{Port(server)}"), "not-" + Token, project, project + "-profile", project, plugins: false);
            var status = app.Find<TextBlock>("ConnectionStatus");
            Until(() => status.Text == "Error");
            Settle(600);
            var icon = app.Find<ContentControl>("ConnectionIcon");
            Require(status.Text == "Error" && Equals(icon.Tag, "unplug") && !icon.Classes.Contains("turning") && ReferenceEquals(status.Foreground, Tereditor.Plugins.UI.Kit.Ui.Danger),
                $"A refused client shows the unplugged glyph and the Error in the danger colour, and keeps it while retrying ({status.Text}, {icon.Tag}).");
        }
        finally { Task.Run(() => server.StopAsync()).GetAwaiter().GetResult(); }
        Console.WriteLine("PASS a host that refuses the client's token is a final Error, not a reconnect");
    }

    private static bool HasFile(E2eWorkspace app, string path) =>
        app.Find<DrawnTree>("FilesTree").Rows.Any(item => item.Tag is ProjectFile file && file.Path == path);

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
            app.ShowTool("files");
            Until(() => HasFile(app, "README.md"));
            Until(() => state.Handshake is not null);
            Require(state.Supports(HostProtocol.ChangeWritePath) && !state.Supports(HostProtocol.Current + 1), "A connected host serves the features up to its version.");

            for (var generation = 2; generation <= 3; generation++)
            {
                proxy.Cut();
                Until(() => state.Connection.Status == HostConnectionStatus.Disconnected);
                Require(state.Generation == generation - 1, "Losing the host does not start a generation.");
                Require(!state.Supports(HostProtocol.ChangeWritePath), "No feature is assumed of a host that has not answered.");
                Until(() => app.Find<TextBlock>("ConnectionStatus").Text == "Reconnecting");
                var dropped = app.Find<ContentControl>("ConnectionIcon");
                Require(Equals(dropped.Tag, "loader") && dropped.Classes.Contains("turning") && !app.Find<TextBlock>("WorkspaceError").IsVisible &&
                    !ReferenceEquals(app.Find<TextBlock>("ConnectionStatus").Foreground, Tereditor.Plugins.UI.Kit.Ui.Danger),
                    $"A dropped connection that is retried turns the loader, not an Error ({dropped.Tag}).");
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
            Require(app.Find<TextBlock>("ConnectionStatus").Text == "Remote" && app.Find<ContentControl>("ConnectionIcon") is { Tag: "plug" } plug &&
                !plug.Classes.Contains("turning"), "The window shows the restored connection with the plug glyph.");

            // A save attempted while the host is away fails once, out loud, and the header stays on the retry.
            app.Open("online-3.txt", keep: true);
            Until(() => app.Window.GetLogicalDescendants().OfType<Tereditor.UI.Editor.CodeDocumentView>().Any());
            var view = app.Window.GetLogicalDescendants().OfType<Tereditor.UI.Editor.CodeDocumentView>().Single();
            view.Editor.ReplaceRange(0, 0, "edited offline ");
            view.Editor.Focus();
            Until(() => view.HasPendingChanges && app.Window.CanSave);
            proxy.Cut();
            Until(() => app.Find<TextBlock>("ConnectionStatus").Text == "Reconnecting");
            const string lostSave = "Couldn't save online-3.txt: the host is not connected.";
            _ = app.Window.SaveSelectedAsync();
            Until(() => app.Window.Toasts.Items.Any(toast => toast.Title == lostSave));
            Settle(600);
            var waiting = app.Find<ContentControl>("ConnectionIcon");
            Require(app.Window.Toasts.Items.Count(toast => toast.Title == lostSave) == 1 && view.HasPendingChanges &&
                app.Find<TextBlock>("ConnectionStatus").Text == "Reconnecting" && Equals(waiting.Tag, "loader") && waiting.Classes.Contains("turning"),
                "A save during the drop raises its one notice, keeps the edits, and the header stays Reconnecting with the turning loader.");
            proxy.Allow();
            Until(() => app.Find<TextBlock>("ConnectionStatus").Text == "Remote");
            view.Editor.Focus();
            _ = app.Window.SaveSelectedAsync();
            Until(() => !view.HasPendingChanges && File.ReadAllText(Path.Combine(project, "online-3.txt")).StartsWith("edited offline ", StringComparison.Ordinal));

            // An Error that is not about the connection outlives the retries that follow it.
            proxy.Cut();
            Until(() => app.Find<TextBlock>("ConnectionStatus").Text == "Reconnecting");
            app.Window.Report(new IOException("Workspace settings could not be saved: disk full"));
            Settle(600);
            Require(app.Find<TextBlock>("ConnectionStatus").Text == "Error" && Equals(app.Find<ContentControl>("ConnectionIcon").Tag, "unplug") &&
                app.Find<TextBlock>("WorkspaceError").IsVisible, "A failure that is not about the connection stays an Error while the host is retried.");
        }
        finally { Task.Run(() => server.StopAsync()).GetAwaiter().GetResult(); }
        Console.WriteLine("PASS a remote window re-reads its workspace and watches again under each new connection generation");
    }
}