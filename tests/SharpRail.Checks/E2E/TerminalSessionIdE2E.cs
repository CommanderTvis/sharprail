using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

/// <summary>The terminal tab menu's Copy session ID (W26): offered for an agent tab whose plugin wires it and whose session id is known.</summary>
internal static class TerminalSessionIdE2E
{
    internal static void Run(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "terminal-session-id"), openFiles: false);
        foreach (var plugin in new[] { "codex", "claude-code" })
        {
            app.State!.ChangeAsync([HostStateChange.PluginSettings(plugin, "{\"enabled\":true,\"command\":\"/usr/bin/false\"}")]).AsTask().GetAwaiter().GetResult();
            Until(() => app.Workbench.PluginRegistry.Active.Contains(plugin));
        }
        const string tab = "session-id-check";
        app.Window.Layout.NewTerminal(app.Center, tab);
        Until(() => app.Terminals.Views.Any(view => view.Started.IsCompletedSuccessfully));
        var terminal = new TerminalRef(app.Root, tab);

        MenuItem[] Offered()
        {
            var button = app.Find<Button>("Tab_" + tab);
            app.Click(button, mouseButton: MouseButton.Right);
            var menu = button.ContextMenu!;
            Until(() => menu.IsOpen);
            var items = menu.Items.OfType<MenuItem>().Where(item => item.Name?.StartsWith("TerminalTabAction_", StringComparison.Ordinal) == true).ToArray();
            if (items.Length == 0) menu.Close();
            return items;
        }
        void Agent(string kind, string? session)
        {
            app.State!.SetTerminalAgent(terminal, new(kind, "/usr/bin/false") { SessionId = session });
            Until(() => app.Workbench.State.Current.TerminalAgents.Any(agent => agent.Terminal == terminal && agent.Record.Kind == kind && agent.Record.SessionId == session));
        }

        Agent("claude", null);
        Require(Offered().Length == 0, "A Claude tab without a known session id offers no Copy session ID.");
        Agent("omp", "omp-session");
        Require(Offered().Length == 0, "An agent whose plugin wires no session copy offers none, even with an id on its record.");
        foreach (var (kind, id) in new[] { ("codex", "019f-codex-session"), ("claude", "5b6f1c52-claude-session") })
        {
            Agent(kind, id);
            var item = Offered().Single();
            Require(Equals(item.Header, "Copy session ID") && item.IsVisible, $"A {kind} tab with a session id offers Copy session ID.");
            TopLevel.GetTopLevel(item)!.UpdateLayout();
            app.Click(item, freshGesture: false);
            Until(() => app.Window.Clipboard?.TryGetTextAsync().GetAwaiter().GetResult() == id);
        }
        Console.WriteLine("PASS terminal tab menu offers Copy session ID only for a known Claude Code or Codex session and copies the bare id");
    }
}