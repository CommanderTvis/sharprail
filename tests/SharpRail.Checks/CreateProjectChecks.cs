using System.Net;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Remote;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static class CreateProjectChecks
{
    internal static void Run(string root)
    {
        Scenario(Path.Combine(root, "create-project-local"), false);
        Scenario(Path.Combine(root, "create-project-remote"), true);
    }

    private static void Scenario(string parent, bool remote)
    {
        var original = Path.Combine(parent, "original");
        Directory.CreateDirectory(original);
        using var server = remote ? RemoteServer.Create(original, IPAddress.Loopback, 0, "create-project", parent + "-state") : null;
        if (server is not null)
        {
            var starting = Task.Run(() => server.StartAsync());
            Until(() => starting.IsCompleted);
            starting.GetAwaiter().GetResult();
        }
        using var app = server is null ? new E2eWorkspace(original) : new E2eWorkspace(
            new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
            "create-project", original, parent + "-profile", original);
        Until(() => app.Window.WorkspaceMounted);
        var previous = app.Window.WorkspaceRoot;
        app.Window.FolderPicker = () => Task.FromResult<string?>(parent);
        app.Click(app.Find<Button>("AddProjectMenu"));
        var menu = app.Find<Button>("AddProjectMenu").ContextMenu!;
        menu.Items.OfType<MenuItem>().Single(item => item.Name == "CreateProjectMenu").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Until(() => app.Window.OwnedWindows.Any(window => window.Title == "Create project"));
        var dialog = app.Window.OwnedWindows.Single(window => window.Title == "Create project");
        T Find<T>(string name) where T : Control => dialog.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        var into = Find<TextBox>("CreateProjectParent");
        var name = Find<TextBox>("CreateProjectName");
        var create = Find<Button>("CreateProjectAccept");
        var error = Find<TextBlock>("CreateProjectError");
        Require(!create.IsEnabled && Find<Button>("CreateProjectBrowse").IsVisible == !remote,
            "Creation needs a project name and offers a folder picker only locally.");
        if (remote) into.Text = parent;
        else app.Click(Find<Button>("CreateProjectBrowse"));
        name.Text = "../escaped";
        app.Click(create);
        Until(() => error.IsVisible && create.IsEnabled);
        Require(!Directory.Exists(Path.Combine(parent, "..", "escaped")) && app.Window.WorkspaceRoot == previous,
            "A traversal name must not create a folder or change the current workspace.");
        var sentinel = Path.Combine(original, "keep.txt");
        File.WriteAllText(sentinel, "keep");
        name.Text = "original";
        app.Click(create);
        Until(() => error.IsVisible && create.IsEnabled);
        Require(File.ReadAllText(sentinel) == "keep" && app.Window.WorkspaceRoot == previous,
            "An existing target must remain unchanged and leave the dialog open.");
        name.Text = "new-project";
        app.Click(create);
        var created = Path.Combine(parent, "new-project");
        Until(() => app.Window.WorkspaceMounted && app.Window.AtProjectHome && app.Window.ProjectRoot == created);
        Until(() => app.Workbench.State.Current.Projects.Contains(created));
        Require(Directory.Exists(created), "Creation must make the folder on the host.");
        Console.WriteLine($"PASS create project ({(remote ? "remote" : "local")}): picker/path, traversal, existing folder, retry and Project Home");
    }
}