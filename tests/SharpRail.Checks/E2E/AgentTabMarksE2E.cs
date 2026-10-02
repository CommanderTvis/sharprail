using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

internal static class AgentTabMarksE2E
{
    internal static void Run(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "agent-tab-marks"), openFiles: false);
        app.Click(app.Find<Button>("SettingsButton"));
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any(window => window.IsVisible));
        var settings = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        app.Click(settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Settings_Layout"));
        settings.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "VerticalCenterTabs").IsChecked = true;
        settings.GetLogicalDescendants().OfType<CheckBox>().Single(box => box.Name == "VerticalTabsInProjects").IsChecked = true;
        settings.Close();
        Settle();
        foreach (var plugin in new[] { "codex", "claude-code" })
        {
            app.State!.ChangeAsync([HostStateChange.PluginSettings(plugin, "{\"enabled\":true,\"command\":\"/usr/bin/false\"}")]).AsTask().GetAwaiter().GetResult();
            Until(() => app.Workbench.PluginRegistry.Active.Contains(plugin));
        }
        const string tab = "agent-mark-check";
        app.Window.Layout.NewTerminal(app.Center, tab);
        Until(() => app.Terminals.Views.Any(view => view.Started.IsCompletedSuccessfully));
        var home = app.Window.OpenProjectHomeAsync(app.Root);
        Until(() => home.IsCompletedSuccessfully && app.Window.AtProjectHome);
        var terminal = new TerminalRef(app.Root, tab);
        Control Row() => app.Window.GetLogicalDescendants().OfType<Button>().Single(button =>
            button.Name == "WorkspaceTabPreview" && Equals(button.Tag, tab));
        bool Marked(string asset) => Row().GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, asset));
        Until(() => app.Window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "WorkspaceTabPreview" && Equals(button.Tag, tab)));
        foreach (var (kind, asset) in new[] { ("codex", "asset:codex.svg"), ("claude", "asset:claude.svg") })
        {
            app.State!.SetTerminalAgent(terminal, new(kind, "/usr/bin/false"));
            Until(() => app.Workbench.State.Current.TerminalAgents.Any(agent => agent.Terminal == terminal && agent.Record.Kind == kind));
            Require(Marked(asset), $"The inactive workspace's terminal must gain its {kind} mark when host identity arrives.");
            var marked = Row();
            app.State.SetTerminalAgent(terminal, new(kind, "/usr/bin/false") { SessionId = "retained-session" });
            Until(() => app.Workbench.State.Current.TerminalAgents.Any(agent => agent.Terminal == terminal && agent.Record.SessionId == "retained-session"));
            Require(ReferenceEquals(marked, Row()) && Marked(asset), "An unchanged identity must retain the decorated row.");
            app.Click((Button)Row());
            Until(() => !app.Window.AtProjectHome);
            var active = app.Find<Button>("Tab_" + tab);
            Require(active.GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, asset)),
                "Returning to the workspace must retain its terminal's agent mark.");
            var other = Path.Combine(app.Root, "other-workspace");
            Directory.CreateDirectory(other);
            var open = app.Window.OpenProjectAsync(other);
            Until(() => open.IsCompletedSuccessfully);
            open = app.Window.OpenProjectAsync(app.Root);
            Until(() => open.IsCompletedSuccessfully);
            active = app.Find<Button>("Tab_" + tab);
            Require(active.GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, asset)),
                "Switching from a different workspace must restore the agent mark without another host update.");
            home = app.Window.OpenProjectHomeAsync(app.Root);
            Until(() => home.IsCompletedSuccessfully && app.Window.AtProjectHome);
            Require(Marked(asset), "Leaving the workspace must retain its terminal's agent mark in Projects.");
            app.State.SetTerminalAgent(terminal, null);
            Until(() => app.Workbench.State.Current.TerminalAgents.All(agent => agent.Terminal != terminal));
            Require(!Marked(asset), "Clearing host identity must remove the inactive terminal's agent mark.");
        }
        Console.WriteLine("PASS Codex and Claude terminal marks refresh in inactive workspace previews and retain unchanged rows");
    }
}