using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.ClaudeCode.UI;
using SharpRail.Plugins.Codex;

using static SharpRail.Checks.E2E.E2eWorkspace;

using ClaudeProcesses = SharpRail.Plugins.ClaudeCode.Host.ProcessTree;
using CodexProcesses = SharpRail.Plugins.Codex.Host.ProcessSnapshot;

namespace SharpRail.Checks;

internal static class AgentLaunchChecks
{
    internal static void RunHost()
    {
        if (OperatingSystem.IsWindows()) return;
        var executable = CodexLaunch.ShellQuote(Environment.ProcessPath!);
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
            executable += " " + CodexLaunch.ShellQuote(typeof(AgentLaunchChecks).Assembly.Location);
        var invocation = "exec " + executable;
        foreach (var kind in new[] { "claude", "codex" })
        {
            var marked = kind == "claude"
                ? ClaudeLaunch.SessionCommand(invocation, "--fake-agent-launch", disableAgentView: false, appendSystemPrompt: false)
                : CodexLaunch.Line(invocation + " --fake-agent-launch", new(false, false) { UiLaunch = true });
            foreach (var (command, expected) in new[] { (invocation + " --fake-agent-launch", false), (marked, true) })
            {
                var start = new ProcessStartInfo("/bin/sh", ["-c", command]) { UseShellExecute = false };
                start.Environment.Remove("SHARPRAIL_UI_LAUNCH");
                using var process = Process.Start(start)!;
                try
                {
                    bool? Origin() => kind == "claude" ? ClaudeProcesses.CaptureUiLaunch(process.Id) : CodexProcesses.CaptureUiLaunch(process.Id);
                    var deadline = Stopwatch.StartNew();
                    while (Origin() != expected && deadline.Elapsed < TimeSpan.FromSeconds(5)) Thread.Sleep(20);
                    Require(Origin() == expected, $"{kind}: expected UI origin {expected}, got {Origin()}; process detection distinguishes manual and UI launches even with prompts and other additions disabled.");
                }
                finally { if (!process.HasExited) process.Kill(); process.WaitForExit(); }
            }
            var noLeak = new ProcessStartInfo("/bin/sh", ["-c", marked.Replace(invocation + " --fake-agent-launch", "/usr/bin/true", StringComparison.Ordinal)
                + "; test \"${SHARPRAIL_UI_LAUNCH-unset}\" = unset"])
            { UseShellExecute = false };
            noLeak.Environment.Remove("SHARPRAIL_UI_LAUNCH");
            using var shell = Process.Start(noLeak)!;
            shell.WaitForExit();
            Require(shell.ExitCode == 0, $"{kind}: the UI marker must not leak into later manual commands in the same shell.");
        }
        Require(ClaudeProcesses.CaptureUiLaunch(-1) is null && CodexProcesses.CaptureUiLaunch(-1) is null,
            "An unreadable launch origin stays unknown.");
        ReplacementProcesses();
        Console.WriteLine("PASS agent launch origin: real processes, disabled additions, command scope and unknown origin");
    }

    private static void ReplacementProcesses()
    {
        var codex = new CodexProcesses([new(10, 1, "sh"), new(20, 10, "codex")]);
        var codexDetections = new List<int>();
        using var codexWatch = new SharpRail.Plugins.Codex.Host.AgentWatch(() => [new("workspace", "tab", 10)],
            (_, _, pid) => codexDetections.Add(pid), (_, _) => { }, () => codex);
        codexWatch.Sweep();
        codex = new([new(10, 1, "sh"), new(21, 10, "codex")]);
        codexWatch.Sweep();
        codexWatch.Sweep();
        Require(codexDetections.SequenceEqual([20, 21]), "Replacing Codex between polls must recheck the launch origin once.");
        var claude = ClaudeProcesses.FromRows([new(10, 1, "sh"), new(20, 10, "claude")]);
        var claudeDetections = new List<int>();
        var claudeWatch = new SharpRail.Plugins.ClaudeCode.Host.AgentWatch(() => [new("workspace", "tab", 10)],
            _ => { }, onAgentDetected: (_, _, pid) => claudeDetections.Add(pid), capture: () => claude);
        claudeWatch.Sweep();
        claude = ClaudeProcesses.FromRows([new(10, 1, "sh"), new(21, 10, "claude")]);
        claudeWatch.Sweep();
        claudeWatch.Sweep();
        claudeWatch.Stop();
        Require(claudeDetections.SequenceEqual([20, 21]), "Replacing Claude between polls must recheck the launch origin once.");
    }

    internal static void RunUi(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "agent-launch-notices"), openFiles: false);
        foreach (var plugin in new[] { "codex", "claude-code" })
        {
            app.State!.ChangeAsync([HostStateChange.PluginSettings(plugin, "{\"enabled\":true,\"command\":\"/usr/bin/false\",\"appendSystemPrompt\":false}")]).AsTask().GetAwaiter().GetResult();
            Until(() => app.Workbench.PluginRegistry.Active.Contains(plugin));
        }
        const string tab = "manual-agent";
        app.Window.Width = 560;
        app.Window.Layout.NewTerminal(app.Center, tab);
        Until(() => app.Terminals.Views.Any(view => view.Started.IsCompletedSuccessfully));
        var terminal = new TerminalRef(app.Root, tab);
        bool Notice() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "TerminalManualLaunchNotice" && control.IsEffectivelyVisible);
        Require(!Notice(), "An ordinary shell has no agent launch notice.");
        foreach (var kind in new[] { "codex", "claude" })
        {
            app.State!.SetTerminalAgent(terminal, new(kind, kind) { LaunchedByUi = false });
            Until(Notice);
            var shown = app.Window.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "TerminalManualLaunchNotice" && control.IsEffectivelyVisible);
            var text = shown.GetLogicalDescendants().OfType<TextBlock>().Single().Text!;
            Require(shown.Bounds is { Width: > 0, Height: > 0 } && shown.Bounds.Width <= app.Window.Bounds.Width,
                "The persistent notice must wrap within a narrow terminal instead of overflowing the window.");
            Require(text.Contains("does not add", StringComparison.Ordinal) && text.Contains("launcher", StringComparison.Ordinal) && text.Contains("prompt", StringComparison.Ordinal),
                "The notice says what a manual launch does not get and how to get launcher integration.");
            app.State.SetTerminalAgent(terminal, new(kind, kind) { LaunchedByUi = false, SessionId = "reported-session" });
            Until(() => app.Workbench.State.Current.TerminalAgents.Any(agent => agent.Record.SessionId == "reported-session"));
            Require(Notice() && ReferenceEquals(shown, app.Window.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "TerminalManualLaunchNotice" && control.IsEffectivelyVisible)),
                "Session updates retain the persistent notice.");
            app.State.SetTerminalAgent(terminal, new(kind, kind) { LaunchedByUi = true });
            Until(() => !Notice());
            app.State.SetTerminalAgent(terminal, new(kind, kind));
            Until(() => app.Workbench.State.Current.TerminalAgents.Any(agent => agent.Record.LaunchedByUi is null));
            Require(!Notice(), "An unknown origin must not be labelled manual.");
            app.State.SetTerminalAgent(terminal, null);
            Until(() => app.Workbench.State.Current.TerminalAgents.All(agent => agent.Terminal != terminal));
            Require(!Notice(), "An exited agent leaves no launch notice.");
        }
        Console.WriteLine("PASS Claude and Codex persistent manual-launch notices, UI launches with prompts disabled and unknown origins");
    }
}