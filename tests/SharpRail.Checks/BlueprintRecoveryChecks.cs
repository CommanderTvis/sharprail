using System.Net;

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

internal static class BlueprintRecoveryChecks
{
    internal static void Run(string root)
    {
        Recover(root, "codex-recorded", "codex-session", agentId: BlueprintAgentId.Codex);
        Recover(root, "codex-fresh", null, agentId: BlueprintAgentId.Codex);
        Recover(root, "codex-remote-recorded", "codex-remote-session", remote: true, agentId: BlueprintAgentId.Codex);
        Recover(root, "codex-remote-fresh", null, remote: true, agentId: BlueprintAgentId.Codex);
        Recover(root, "recorded", "recorded-session");
        Recover(root, "fresh", null);
        Recover(root, "retry", "retry-session", failFirst: true);
        Recover(root, "remote-recorded", "remote-recorded-session", remote: true);
        Recover(root, "remote-fresh", null, remote: true);
        Recover(root, "remote-retry", "remote-retry-session", failFirst: true, remote: true);
    }

    private static void Recover(string root, string name, string? session, bool failFirst = false, bool remote = false, BlueprintAgentId agentId = BlueprintAgentId.Claude)
    {
        var project = Path.Combine(root, "blueprint-recovery-" + name);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, BlueprintContract.File), "# Existing design\n\nKeep the current document.\n");
        var server = remote ? RemoteServer.Create(project, IPAddress.Loopback, 0, "blueprint-recovery", project + "-state") : null;
        try
        {
            if (server is not null) server.StartAsync().GetAwaiter().GetResult();
            using var app = server is null ? new E2eWorkspace(project) : new E2eWorkspace(
                new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
                "blueprint-recovery", project, project + "-profile", project);
            Until(() => app.Window.WorkspaceMounted && app.Workbench.PluginRegistry.Active.Contains("blueprint"));
            var commands = new List<LauncherCommandOptions>();
            var attempts = 0;
            app.Workbench.PluginRegistry.AddLauncher("fixture", new(agentId == BlueprintAgentId.Codex ? "codex" : "claude", "Fixture author", "terminal", options =>
            {
                attempts++;
                if (failFirst && attempts == 1) throw new InvalidOperationException("Fixture author launch refused.");
                commands.Add(options);
                return ":";
            }, () => new(true)));
            T Call<P, T>(PluginMethod<P, T> method, P parameters)
            {
                var task = app.Workbench.Plugins!.CallAsync(new("blueprint", method.Name, parameters, "recovery-check")).AsTask();
                Until(() => task.IsCompleted);
                return PluginJson.Convert<T>(task.GetAwaiter().GetResult());
            }
            Call<BlueprintOpen, BlueprintOpened>(BlueprintContract.Open, new(project, new BlueprintProduct(), agentId));
            Call<BlueprintSetAuthor, BlueprintAck>(BlueprintContract.SetAuthor, new(project, new BlueprintTerminalAuthor("missing-author", session)));
            var opening = app.Window.OpenDocumentAsync(BlueprintContract.File, true);
            Until(() => opening.IsCompleted);
            opening.GetAwaiter().GetResult();
            if (failFirst)
            {
                Until(() => app.Find<Avalonia.Controls.TextBlock>("GestureToastMessage").Text?.Contains("Fixture author launch refused.", StringComparison.Ordinal) == true);
                Require(commands.Count == 0 && attempts == 1 && app.Find<Avalonia.Controls.Border>("GestureToast").IsVisible,
                    "A failed author launch reports its actual error without creating a terminal.");
                var retry = app.Window.OpenDocumentAsync(BlueprintContract.File, true);
                Until(() => retry.IsCompleted);
                retry.GetAwaiter().GetResult();
            }
            Until(() => commands.Count == 1 && app.Window.GetLogicalDescendants().OfType<Avalonia.Controls.Control>().Any(control => control.Name == "Blueprint"));
            var command = commands.Single();
            var tabs = app.Window.Layout.State.Groups.SelectMany(group => app.Window.Layout.Tabs(group.Id)).Where(tab => tab.Kind == "terminal").ToArray();
            if (session is not null)
            {
                Require(command.ResumeSessionId == session && command.InitialPrompt is null && command.SystemPrompt is null && tabs.Any(tab => tab.Id == "missing-author"),
                    "A missing recorded author resumes its exact session and tab without submitting a new initial prompt.");
            }
            else
            {
                Require(command.ResumeSessionId is null && command.InitialPrompt is { Length: > 0 } && command.SystemPrompt is { Length: > 0 } && tabs.Any(tab => tab.Id == "blueprint-author"),
                    "An author without a recorded session starts a visible fresh author with the existing-document instructions.");
                var state = Call<BlueprintScope, BlueprintChangedPayload>(BlueprintContract.Get, new(project)).State;
                Require(state?.Author is BlueprintTerminalAuthor { TabKey: "blueprint-author" }, "Fresh recovery records the terminal as the Blueprint author.");
            }
            Require(Call<BlueprintScope, BlueprintChangedPayload>(BlueprintContract.Get, new(project)).State?.AgentId == agentId, "Recovery preserves the selected agent identity.");
            Require(File.ReadAllText(Path.Combine(project, BlueprintContract.File)).Contains("Keep the current document.", StringComparison.Ordinal),
                "Author recovery preserves the existing Blueprint document.");
            var reopen = app.Window.OpenDocumentAsync(BlueprintContract.File, true);
            Until(() => reopen.IsCompleted);
            reopen.GetAwaiter().GetResult();
            Settle();
            Require(commands.Count == 1, "Reopening an existing author does not generate another launcher command.");
            Require(attempts == (failFirst ? 2 : 1), "A failed opening is released so retry can launch once, and successful openings reuse their author.");
            Console.WriteLine($"PASS Blueprint {name} author recovery: exact launch options, visible companion, preserved source and reuse");
        }
        finally
        {
            if (server is not null) Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
        }
    }
}