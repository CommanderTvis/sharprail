using System.Net;

using Avalonia.Controls;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

/// <summary>The reveal action in the Files tree and Project Home menus: present with the platform's label on a local host, absent on a remote one.</summary>
internal static class FileRevealE2E
{
    internal static void Run(string root)
    {
        foreach (var remote in new[] { false, true })
        {
            var workspace = Path.Combine(root, "file-reveal", remote ? "remote" : "local");
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(workspace, "README.md"), "# reveal\n");
            var server = remote ? RemoteServer.Create(workspace, IPAddress.Loopback, 0, "file-reveal", workspace + "-state") : null;
            try
            {
                if (server is not null) Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
                var endpoint = server is null ? null : new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                using var terminalAdapter = endpoint is null ? null : new RemoteTerminalAdapter(endpoint, "file-reveal");
                using var app = server is null ? new E2eWorkspace(workspace)
                    : new E2eWorkspace(endpoint!, "file-reveal", workspace, workspace + "-profile", workspace, terminals: new E2eTerminals(terminalAdapter!));
                Until(() => app.Window.WorkspaceMounted);

                app.Click(app.FileRow("README.md"), mouseButton: Avalonia.Input.MouseButton.Right);
                var files = app.FileRow("README.md").ContextMenu!;
                Until(() => files.IsOpen);
                var reveal = files.Items.OfType<MenuItem>().SingleOrDefault(item => item.Name == "FileNodeReveal");
                Require(files.Items.OfType<MenuItem>().Any(item => item.Name == "FileNodeCopyPath"), "The Files menu itself opened.");
                var label = FileManagerReveal.Label(OperatingSystem.IsMacOS(), OperatingSystem.IsWindows());
                Require(remote ? reveal is null : reveal is not null && Equals(reveal.Header, label),
                    remote ? "A remote host's Files menu must not offer to reveal a file on another machine." : $"A local Files menu offers '{label}'.");
                WorkspaceFixture.CloseMenu(files);

                // The workspace menu is built by the window for a Projects row and the header; build it directly, as both do.
                var menu = new ContextMenu();
                var record = new SharpRail.Host.Abstractions.WorkspaceRecord("w", workspace, SharpRail.Host.Abstractions.WorkspaceKinds.Default, workspace, "main", "main");
                typeof(SharpRail.UI.WorkbenchWindow).GetMethod("AddWorkspaceActions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(app.Window, [menu, record, false]);
                Require(menu.Items.OfType<MenuItem>().Any(item => item.Name == "WorkspaceCopyPath") &&
                    (menu.Items.OfType<MenuItem>().Any(item => item.Name == "WorkspaceReveal")) == !remote, "Project Home's workspace menu offers reveal only for a local host.");
                Console.WriteLine($"PASS file reveal {(remote ? "remote" : "local")} host: Files and workspace menus");
            }
            finally
            {
                if (server is not null) Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
            }
        }
    }
}