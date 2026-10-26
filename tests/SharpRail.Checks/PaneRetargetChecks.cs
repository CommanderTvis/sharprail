using System.Reflection;

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Plugins.UI.Kit;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>
/// The Claude Code, Codex and omp panes and the Language pane are one instance each: switching workspaces of one project and
/// of another, A to B to C and back 20 times, keeps the same control, shows the new workspace's content and none of the old one's,
/// shows on return what the first visit showed, and registers no further handlers on the shared events.
/// </summary>
internal static class PaneRetargetChecks
{
    private static void Await(Task task) { Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }

    private static string Signature(Control pane)
    {
        Dispatcher.UIThread.RunJobs();
        var all = new[] { pane }.Concat(pane.GetLogicalDescendants().OfType<Control>()).ToArray();
        var lines = new List<string>();
        foreach (var control in all)
        {
            if (control is TextBlock { Text: { Length: > 0 } text }) lines.Add(text);
            if (control.Tag is string tag) lines.Add("tag:" + tag);
        }
        return string.Join("\n", lines);
    }

    // The handlers on the events a pane may subscribe to: a leak shows as growth after many retargets.
    private static int Handlers(E2eWorkspace app)
    {
        static int Count(object? owner, Type type, string name)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return (field?.GetValue(owner) as Delegate)?.GetInvocationList().Length ?? 0;
        }
        return Count(app.Workbench, typeof(SharpRail.UI.Workbench), "ProjectionChanged") +
            Count(app.Workbench.PluginRegistry, typeof(SharpRail.UI.Plugins.PluginRegistry), "Changed") +
            Count(app.Workbench.State, typeof(SharpRail.UI.State.SharedState), "Changed") +
            Count(null, typeof(Ui), "ThemeChanged");
    }

    internal static void Run(string root)
    {
        // The host reports macOS temp paths through /private.
        static string Canon(string path) => path.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + path : path;
        string Repo(string name, string note)
        {
            var path = Canon(IsolatedGit.Repository(Path.Combine(root, name)));
            File.WriteAllText(Path.Combine(path, "AGENTS.md"), note + "\n");
            File.WriteAllText(Path.Combine(path, "CLAUDE.md"), note + "\n");
            return path;
        }
        var a = Repo("retarget-a", "alpha");
        var b = Canon(Path.Combine(root, "retarget-b-worktree"));
        IsolatedGit.Run(a, "worktree", "add", "-b", "other", b);
        File.WriteAllText(Path.Combine(b, "AGENTS.md"), "beta\n");
        var c = Repo("retarget-c", "gamma");
        using var app = new E2eWorkspace(a);
        var window = app.Window;
        Until(() => window.WorkspaceMounted);

        foreach (var id in new[] { "omp", "claude-code", "codex" })
            _ = app.Workbench.State.ChangeAsync(HostStateChange.PluginEnabled(id, true));
        var tools = new (string Tool, string Name)[]
        {
            (SharpRail.Plugins.Omp.OmpManifest.ContextTool, "OmpPanel"),
            (SharpRail.Plugins.ClaudeCode.ClaudeCodeManifest.ConfigTool, "ClaudeConfigPanel"),
            (SharpRail.Plugins.Codex.CodexManifest.ConfigTool, "CodexConfig"),
            ("lsp", "LspPanel")
        };
        foreach (var (tool, _) in tools) Until(() => window.Layout.IsToolAvailable(tool));

        Control Show(string tool, string name)
        {
            window.RevealToolFor(window.WorkspaceRoot, tool);
            Until(() => Panes(name).Count == 1);
            return Panes(name).Single();
        }
        List<Control> Panes(string name) { Dispatcher.UIThread.RunJobs(); return [.. window.GetLogicalDescendants().OfType<Control>().Where(item => item.Name == name)]; }
        bool Loaded(string signature) => !signature.Contains("Reading", StringComparison.Ordinal) && signature.Contains(window.WorkspaceRoot, StringComparison.Ordinal);

        var panes = new Dictionary<string, Control>();
        var firstVisit = new Dictionary<string, string>();
        foreach (var (tool, name) in tools)
        {
            var pane = panes[tool] = Show(tool, name);
            Until(() => Loaded(Signature(pane)));
            Settle(300);
            firstVisit[tool] = Signature(pane);
        }
        var before = Handlers(app);

        var others = new[] { b, c, a };
        var visited = new Dictionary<string, HashSet<Control>>(tools.ToDictionary(item => item.Tool, _ => new HashSet<Control>(ReferenceEqualityComparer.Instance)));
        for (var round = 0; round < 21; round++)
        {
            var target = others[round % 3];
            Await(window.OpenProjectAsync(target));
            Until(() => window.WorkspaceRoot == target);
            foreach (var (tool, name) in tools)
            {
                var pane = Show(tool, name);
                visited[tool].Add(pane);
                Require(ReferenceEquals(pane, panes[tool]), $"{tool} must keep its one instance in {target} (round {round}).");
                Until(() => Loaded(Signature(pane)));
                var shown = Signature(pane);
                foreach (var other in new[] { a, b, c }.Where(path => path != target && !target.StartsWith(path, StringComparison.Ordinal) && !path.StartsWith(target, StringComparison.Ordinal)))
                    Require(!shown.Contains(other, StringComparison.Ordinal), $"{tool} in {target} must not show {other}:\n{shown}");
                if (target == a && tool != "lsp") Require(shown == firstVisit[tool], $"{tool} back in A must equal its first visit:\n--- first\n{firstVisit[tool]}\n--- now\n{shown}");
            }
        }
        Settle(300);
        foreach (var (tool, _) in tools) Require(visited[tool].Count == 1, $"{tool} must be one instance over 21 switches, saw {visited[tool].Count}.");
        var after = Handlers(app);
        Require(after <= before, $"Handlers on the shared events must not grow over 21 retargets: {before} became {after}.");
        Console.WriteLine($"PASS pane retarget: one instance per tool over 21 switches across projects, content per workspace, handlers {before} -> {after}");
    }
}