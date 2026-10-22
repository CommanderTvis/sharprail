using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

internal static class ProjectLanguageAutostartChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task Run(string fixture)
    {
        foreach (var remote in new[] { false, true }) await Exercise(Path.Combine(fixture, "project-autostart", remote ? "remote" : "local"), remote);
    }

    internal static void RunUi(string fixture)
    {
        using var app = new E2E.E2eWorkspace(Path.Combine(fixture, "project-autostart-ui"), openFiles: false);
        E2E.E2eWorkspace.Until(() => app.State!.Current.Workspaces.Any(item => item.Path == app.Root));
        var prepared = false;
        var attempts = app.Terminals.Attempts;
        app.Host.LanguageReply = request =>
        {
            if (request.Operation != "prepareAgent") return null;
            Require(app.Terminals.Attempts == attempts, "Agent terminal attached before its language startup request.");
            prepared = true;
            return new("ready");
        };
        var launch = app.Window.OpenPluginTerminalAsync(app.Root, new() { Command = "printf autostart-launch" });
        E2E.E2eWorkspace.Until(() => launch.IsCompleted);
        launch.GetAwaiter().GetResult();
        Require(prepared, "Managed command launch omitted project language startup.");
        E2E.E2eWorkspace.Until(() => app.Terminals.Attempts > attempts);
        app.Host.LanguageReply = null;
        var view = new SharpRail.UI.Panels.LanguageConfigurationView(app.Host, app.Root,
            new("typescript", "", "idle", "Fixture", "fixture"));
        var settings = new Avalonia.Controls.Window { Width = 600, Height = 600, Content = view };
        settings.Show(app.Window);
        try
        {
            var checkbox = Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.CheckBox>(view, "ProjectAutoStart")!;
            checkbox.IsChecked = true;
            checkbox.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => app.State!.Current.ProjectLanguageAutostart.ContainsKey(app.Root));
            Require(app.State!.Current.ProjectLanguageAutostart[app.Root] == "[\"typescript\"]", "Project checkbox did not persist its selection.");
        }
        finally { settings.Close(); }
        Console.WriteLine("PASS managed command prelaunch ordering and project autostart checkbox persistence");
    }

    private static async Task Exercise(string root, bool remote)
    {
        var project = Path.Combine(root, "project");
        var child = Path.Combine(root, "child");
        var other = Path.Combine(root, "other");
        foreach (var folder in new[] { project, child, other }) Directory.CreateDirectory(folder);
        var storage = Path.Combine(root, "state");
        await using var server = remote ? RemoteServer.Create(project, IPAddress.Loopback, 0, "autostart-test", storage,
            plugins: seams => seams with { Builtins = SharpRail.Host.Core.Plugins.BuiltinPlugins.LanguageServerEntries }) : null;
        if (server is not null) await server.StartAsync();
        var state = server is null ? new HostStateStore(storage) : (HostStateStore)server.Services.GetRequiredService<IHostStateService>();
        using var remoteHost = server is null ? null : new RemoteProjectAdapter(new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), "autostart-test");
        IProjectServices host = remoteHost ?? (IProjectServices)new LocalProjectAdapter(new ProjectServices(project, state));
        var previousDisabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE");
        Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        try
        {
            await host.OpenProjectAsync(project);
            state.ChangeWorkspaces(items => items.Append(new("project", project, WorkspaceKinds.Default, project, "", ""))
                .Append(new("child", project, WorkspaceKinds.External, child, "", ""))
                .Append(new("other", other, WorkspaceKinds.Default, other, "", "")));
            var executable = Environment.ProcessPath!;
            string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet"
                ? [typeof(LanguageChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
            await state.ChangeAsync([HostStateChange.Setting("language-server:typescript",
                JsonSerializer.Serialize(new LanguageServerOptions(Command: executable, Arguments: arguments), JsonSerializerOptions.Web))]);
            await host.LanguageAsync(new(project, Operation: "setProjectAutostart", Server: "typescript", Configuration: "true"));
            Require((await host.LanguageAsync(new(child, Operation: "projectAutostart"))).Configuration == "[\"typescript\"]", "Project choice was not shared with an existing workspace.");
            Require((await host.LanguageAsync(new(other, Operation: "projectAutostart"))).Configuration == "[]", "Autostart leaked to another project.");
            Require((await host.LanguageAsync(new(project, Operation: "servers"))).Servers.Single(item => item.Id == "typescript").State == "idle", "Saving autostart started a server.");
            Require(new HostStateStore(storage).Current.ProjectLanguageAutostart[project] == "[\"typescript\"]", "Project autostart did not survive reload.");
            if (server is not null)
            {
                using var remoteState = new RemoteStateAdapter(new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), "autostart-test");
                Require((await remoteState.GetStateAsync()).ProjectLanguageAutostart[project] == "[\"typescript\"]", "Project autostart was lost from remote snapshots.");
            }
            await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => await host.LanguageAsync(new(child, Operation: "prepareAgent"))));
            await WaitReady(child);
            var pid = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(child, ".tools", "initialize.json")))!["pid"]!.GetValue<int>();
            await host.LanguageAsync(new(child, Operation: "prepareAgent"));
            Require((await host.LanguageAsync(new(project, Operation: "servers"))).Servers.Single(item => item.Id == "typescript").State == "idle", "Starting another workspace shared runtime markers.");
            Require(JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(child, ".tools", "initialize.json")))!["pid"]!.GetValue<int>() == pid, "Warm agent launch restarted a server.");
            Require(JsonNode.Parse((await host.LanguageAsync(new(child, Operation: "configuration", Server: "typescript"))).Configuration)!["autoStart"]?.GetValue<bool>() != true,
                "Project startup changed the workspace's inherited autostart choice.");
            await host.LanguageAsync(new(child, Operation: "stop", Server: "typescript"));
            await host.LanguageAsync(new(child, Operation: "prepareAgent"));
            await WaitReady(child);
            Require(JsonNode.Parse((await host.LanguageAsync(new(child, Operation: "configuration", Server: "typescript"))).Configuration)!["autoStart"]!.GetValue<bool>() == false,
                "Restarting a selected server changed workspace autostart.");
            await state.ChangeAsync([HostStateChange.Setting("language-server:python",
                JsonSerializer.Serialize(new LanguageServerOptions(Command: Path.Combine(root, "missing-server"), Arguments: []), JsonSerializerOptions.Web))]);
            await host.LanguageAsync(new(child, Operation: "setProjectAutostart", Server: "python", Configuration: "true"));
            Require((await host.LanguageAsync(new(child, Operation: "prepareAgent"))).State == "ready", "Failed server startup blocked agent launch.");
            using (var failed = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while ((await host.LanguageAsync(new(child, Operation: "status", Server: "python"), failed.Token)).State != "error") await Task.Delay(30, failed.Token);
            await state.ChangeAsync([HostStateChange.Setting("language-server:python",
                JsonSerializer.Serialize(new LanguageServerOptions(Command: executable, Arguments: arguments), JsonSerializerOptions.Web))]);
            await host.LanguageAsync(new(child, Operation: "prepareAgent"));
            using (var recovered = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while ((await host.LanguageAsync(new(child, Operation: "status", Server: "python"), recovered.Token)).State != "ready") await Task.Delay(30, recovered.Token);
            await host.LanguageAsync(new(child, Operation: "setProjectAutostart", Server: "python", Configuration: "false"));
            if (!remote && !OperatingSystem.IsWindows())
            {
                await host.LanguageAsync(new(child, Operation: "stop", Server: "typescript"));
                await using var terminals = new PtyTerminalService("/bin/sh");
                state.Terminals = terminals;
                terminals.RevivePrefill = _ => new("printf revived-agent", false);
                try
                {
                    await using var attachment = await terminals.AttachAsync(new("revival-autostart", child, "fixture") { TabKey = "revival" });
                    Require(attachment.Prefill is not null && (await host.LanguageAsync(new(child, Operation: "servers"))).Servers.Single(item => item.Id == "typescript").State != "stopped",
                        "Revived agent prefill was returned before selected server startup.");
                }
                finally { state.Terminals = null; }
            }
            await host.LanguageAsync(new(project, Operation: "setProjectAutostart", Server: "typescript", Configuration: "false"));
            Require((await host.LanguageAsync(new(child, Operation: "projectAutostart"))).Configuration == "[]", "Project autostart could not be cleared from a sibling workspace.");
            Console.WriteLine("PASS project language autostart persistence, isolation, concurrent start and warm reuse " + (remote ? "remote" : "local"));
        }
        finally { await state.StopLanguagesAsync(); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", previousDisabled); }

        async Task WaitReady(string workspace)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while ((await host.LanguageAsync(new(workspace, Operation: "status", Server: "typescript"), timeout.Token)).State != "ready")
                await Task.Delay(30, timeout.Token);
        }
    }
}