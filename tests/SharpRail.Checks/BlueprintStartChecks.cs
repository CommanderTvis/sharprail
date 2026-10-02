using System.Net;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.Blueprint;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

internal static class BlueprintStartChecks
{
    /// <summary>blueprint.spec.ts: the Claude host is offered only once its plugin is on, through the real Claude Code plugin.</summary>
    internal static void ClaudeGate(string root)
    {
        var project = Path.Combine(root, "blueprint-claude-gate");
        using var app = new E2eWorkspace(project, openFiles: false);
        Until(() => app.Workbench.PluginRegistry.Active.Contains("blueprint"));
        Require(!app.Workbench.PluginRegistry.Active.Contains("claude-code"), "Claude Code is off by default.");
        var home = app.Window.OpenProjectHomeAsync(project);
        Until(() => home.IsCompleted);
        home.GetAwaiter().GetResult();
        app.Click(app.Find<Button>("ProjectDraftBlueprint"));
        Until(() => app.Window.OwnedWindows.Any(window => Equals(window.Tag, "BlueprintStart")));
        var dialog = app.Window.OwnedWindows.Single(window => Equals(window.Tag, "BlueprintStart"));
        T Find<T>(string name) where T : Control => dialog.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        Require(!Find<Button>("Agent").IsVisible && !Find<Button>("BlueprintStart").IsEnabled, "With Claude Code off the dialog offers no Claude host at all.");
        app.State!.ChangeAsync([SharpRail.Host.Abstractions.HostStateChange.PluginEnabled("claude-code", true)]).AsTask().GetAwaiter().GetResult();
        Until(() => Find<Button>("Agent").IsVisible && Find<TextBlock>("AgentLabel").Text == "Claude Code");
        app.State.ChangeAsync([SharpRail.Host.Abstractions.HostStateChange.PluginEnabled("claude-code", false)]).AsTask().GetAwaiter().GetResult();
        Until(() => !Find<Button>("Agent").IsVisible);
        dialog.Close();
        Console.WriteLine("PASS fork plugins/blueprint/blueprint.spec.ts: the Claude host is offered only once its plugin is on");
    }

    internal static void Run(string root)
    {
        var project = Path.Combine(root, "blueprint-start-remote");
        Directory.CreateDirectory(project);
        var document = Path.Combine(project, "requirements.md");
        File.WriteAllText(document, "# Requirements\n");
        var server = RemoteServer.Create(project, IPAddress.Loopback, 0, "blueprint-start", project + "-state");
        server.StartAsync().GetAwaiter().GetResult();
        try
        {
            var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var app = new E2eWorkspace(address, "blueprint-start", project, project + "-profile", project);
            Until(() => app.Workbench.PluginRegistry.Active.Contains("blueprint"));
            var available = true;
            var icons = 0;
            var launches = new List<LauncherCommandOptions>();
            var launcher = new AgentLauncher("claude", "Fixture author", "terminal", options => { launches.Add(options); return ":"; },
                () => new(available, available ? null : "Fixture author is unavailable."))
            { CreateIcon = (size, color) => { icons++; return new Border { Name = "FixtureAuthorIcon", Width = size, Height = size, Background = color }; } };
            app.Workbench.PluginRegistry.AddLauncher("fixture", launcher);
            var home = app.Window.OpenProjectHomeAsync(project);
            Until(() => home.IsCompleted);
            home.GetAwaiter().GetResult();
            app.Click(app.Find<Button>("ProjectDraftBlueprint"));
            Until(() => app.Window.OwnedWindows.Any(window => Equals(window.Tag, "BlueprintStart")));
            var dialog = app.Window.OwnedWindows.Single(window => Equals(window.Tag, "BlueprintStart"));
            T Find<T>(string name) where T : Control => dialog.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
            var start = Find<Button>("BlueprintStart");
            var agent = Find<Button>("Agent");
            Require(agent.IsVisible && agent.IsEnabled && Find<TextBlock>("AgentLabel").Text == "Fixture author" &&
                Find<Border>("FixtureAuthorIcon") is not null && icons == 1,
                "The Blueprint agent chip uses the registered launcher's label and fresh icon control.");
            string Label() => ((TextBlock)start.Content!).Text!;
            Require(!start.IsEnabled && Label() == "Draft it", "An empty idea cannot start a blueprint.");
            Find<TextBox>("Brief").Text = "  Build a light controller  ";
            Until(() => start.IsEnabled);
            available = false;
            app.Workbench.PluginRegistry.Invalidate("fixture");
            Until(() => !start.IsEnabled && !agent.IsEnabled);
            Require(Equals(ToolTip.GetTip(agent), "Fixture author is unavailable.") && agent.Opacity == .6 && icons == 1,
                "Launcher invalidation updates the disabled chip and reason without recreating its icon.");
            available = true;
            app.Workbench.PluginRegistry.Invalidate("fixture");
            Until(() => start.IsEnabled && agent.IsEnabled);
            app.Workbench.PluginRegistry.RemovePlugin("fixture");
            Until(() => !agent.IsVisible && !start.IsEnabled);
            app.Workbench.PluginRegistry.AddLauncher("fixture", launcher);
            Until(() => agent.IsVisible && start.IsEnabled);
            Require(icons == 2, "A restored launcher gets a fresh icon rather than reparenting the previous control.");
            app.Click(Find<RadioButton>("Product"));
            Require(start.IsEnabled && Label() == "Take it over" && Find<TextBlock>("ProductNote").IsVisible && !Find<TextBox>("Brief").IsVisible,
                "The project source needs no brief and shows its explanation.");
            app.Click(Find<RadioButton>("Spec"));
            Require(!start.IsEnabled, "The document source requires a selected file.");

            Window Pick()
            {
                app.Click(Find<Button>("Pick"));
                Until(() => app.Window.OwnedWindows.Any(window => window.Title == "Choose a document by path"));
                return app.Window.OwnedWindows.Single(window => window.Title == "Choose a document by path");
            }
            var picker = Pick();
            T PickControl<T>(string name) where T : Control => picker.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
            Require(PickControl<TextBlock>("DialogExplanation").Text?.Contains("file on the computer running SharpRail", StringComparison.Ordinal) == true &&
                PickControl<TextBox>("OpenProjectPathInput").PlaceholderText == "File path",
                "A remote document picker asks for a host file, rather than a project directory.");
            PickControl<TextBox>("OpenProjectPathInput").Text = document;
            app.Click(PickControl<Button>("OpenProjectPathSubmit"));
            Until(() => start.IsEnabled && Find<TextBlock>("Path").Text == document);
            picker = Pick();
            app.Click(picker.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: "Cancel" } || Equals(button.Content, "Cancel")));
            Require(start.IsEnabled && Find<TextBlock>("Path").Text == document,
                "Cancelling a second document pick preserves the original selection and start availability.");
            app.Click(Find<RadioButton>("Idea"));
            Require(start.IsEnabled && Find<TextBox>("Brief").Text == "  Build a light controller  ",
                "Switching source preserves the idea text.");
            app.Click(dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: "Cancel" } || Equals(button.Content, "Cancel")));
            Until(() => !app.Window.OwnedWindows.Contains(dialog));
            Require(!File.Exists(Path.Combine(project, "BLUEPRINT.md")), "Cancelling the start dialog creates no blueprint.");
            Require(launches.Count == 0, "Cancelling does not launch an author.");

            // blueprint.spec.ts: a takeover starts only from inside the project; an outside document is refused.
            var outside = project + "-outside.md";
            File.WriteAllText(outside, "# Outside\n");
            app.Click(app.Find<Button>("ProjectDraftBlueprint"));
            Until(() => app.Window.OwnedWindows.Any(window => Equals(window.Tag, "BlueprintStart")));
            dialog = app.Window.OwnedWindows.Single(window => Equals(window.Tag, "BlueprintStart"));
            start = Find<Button>("BlueprintStart");
            app.Click(Find<RadioButton>("Spec"));
            picker = Pick();
            PickControl<TextBox>("OpenProjectPathInput").Text = outside;
            app.Click(PickControl<Button>("OpenProjectPathSubmit"));
            Until(() => start.IsEnabled && Label() == "Take it over" && Find<TextBlock>("Path").Text == outside);
            app.Click(start);
            Until(() => app.Find<TextBlock>("GestureToastMessage").Text?.Contains("Choose a document inside this project", StringComparison.Ordinal) == true);
            Require(app.Window.OwnedWindows.Contains(dialog) && launches.Count == 0 && !File.Exists(Path.Combine(project, "BLUEPRINT.md")),
                "An outside document is refused with a notification, the dialog stays open and nothing starts.");
            app.Click(dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Content is TextBlock { Text: "Cancel" } || Equals(button.Content, "Cancel")));
            Until(() => !app.Window.OwnedWindows.Contains(dialog));
            home = app.Window.OpenProjectHomeAsync(project);
            Until(() => home.IsCompleted);
            home.GetAwaiter().GetResult();
            app.Click(app.Find<Button>("ProjectDraftBlueprint"));
            Until(() => app.Window.OwnedWindows.Any(window => Equals(window.Tag, "BlueprintStart")));
            dialog = app.Window.OwnedWindows.Single(window => Equals(window.Tag, "BlueprintStart"));
            app.Click(Find<RadioButton>("Product"));
            app.Click(Find<Button>("BlueprintStart"));
            Until(() => !app.Window.OwnedWindows.Contains(dialog) && launches.Count == 1 &&
                app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "Blueprint"));
            var snapshot = app.Workbench.Plugins!.CallAsync(new("blueprint", BlueprintContract.Get.Name, new BlueprintScope(project), "start-check")).AsTask();
            Until(() => snapshot.IsCompleted);
            var state = PluginJson.Convert<BlueprintChangedPayload>(snapshot.GetAwaiter().GetResult()).State;
            Require(!app.Window.AtProjectHome && app.Window.WorkspaceRoot == project && state is
            { Source: BlueprintProduct, Author: BlueprintTerminalAuthor { TabKey: "blueprint-author" } },
                "Draft enters the project's Default workspace and records its visible terminal author through the remote host.");
            Require(launches[0] is { InitialPrompt.Length: > 0, SystemPrompt.Length: > 0, ResumeSessionId: null },
                "The initial author receives the Blueprint opening and system instructions, without a guessed resume session.");
            home = app.Window.OpenProjectHomeAsync(project);
            Until(() => home.IsCompleted);
            home.GetAwaiter().GetResult();
            app.Click(app.Find<Button>("ProjectDraftBlueprint"));
            Until(() => app.Window.OwnedWindows.Any(window => Equals(window.Tag, "BlueprintStart")));
            dialog = app.Window.OwnedWindows.Single(window => Equals(window.Tag, "BlueprintStart"));
            Find<TextBox>("Brief").Text = "A different idea must not overwrite this project's blueprint.";
            app.Click(Find<Button>("BlueprintStart"));
            Until(() => !app.Window.OwnedWindows.Contains(dialog) && !app.Window.AtProjectHome &&
                app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "Blueprint"));
            snapshot = app.Workbench.Plugins!.CallAsync(new("blueprint", BlueprintContract.Get.Name, new BlueprintScope(project), "start-check")).AsTask();
            Until(() => snapshot.IsCompleted);
            state = PluginJson.Convert<BlueprintChangedPayload>(snapshot.GetAwaiter().GetResult()).State;
            Require(launches.Count == 1 && state is { Source: BlueprintProduct, Author: BlueprintTerminalAuthor { TabKey: "blueprint-author" } },
                "Drafting again reopens the existing author and preserves its original source instead of starting the newly entered idea.");
            Console.WriteLine("PASS Blueprint start sources and remote document selection: host-file copy, retained selection on cancel, unchanged brief, and (fork blueprint.spec.ts) a takeover only from inside the project");
        }
        finally
        {
            Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
        }
    }
}