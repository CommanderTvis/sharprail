using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.Processes;
using SharpRail.Plugins.Processes.Host;
using SharpRail.Plugins.Processes.UI;

namespace SharpRail.Checks;

// The Processes plugin (Plugins.Processes/SPEC.md): the tree and its sums, the capped trend, sampling only when asked, the stop paths, and the
// plugin switch: disabled, the agent-facing owned-process management is gone; enabled, it is the #52 behaviour.
internal static class ProcessesChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeSource : IProcessSource
    {
        internal ProcessInputs Inputs = new([], [], [], []);
        internal Dictionary<int, TimeSpan> Cpu = [];
        internal readonly List<string> Stopped = [];
        public ProcessInputs Read() => Inputs;
        public (TimeSpan Cpu, long MemoryBytes)? Usage(int pid) => Cpu.TryGetValue(pid, out var cpu) ? (cpu, pid * 1_000_000L) : null;
        public string WorkspaceLabel(string workspace) => Path.GetFileName(workspace);
        public ValueTask StopLanguageServerAsync(string workspace, string serverId, CancellationToken cancellationToken) { lock (Stopped) Stopped.Add($"lang:{Path.GetFileName(workspace)}/{serverId}"); return ValueTask.CompletedTask; }
        public ValueTask StopProcessAsync(int pid, string command, CancellationToken cancellationToken) { lock (Stopped) Stopped.Add("pid:" + pid); return ValueTask.CompletedTask; }
    }

    private static readonly TerminalRef Tab1 = new("/w/alpha", "t1"), Tab2 = new("/w/alpha", "t2"), Tab3 = new("/w/beta", "t3");

    // Claude Code in alpha tab 1 (pid 11) with a spawned claude -p (12) that spawned codex exec (13) and a reported sleep (14);
    // a plain terminal in alpha tab 2 that reported vite (21); two alpha language servers. Optionally a Codex tab and a Python server in beta.
    private static FakeSource Fixture(bool secondWorkspace)
    {
        var terminals = new List<TerminalInfo> { new(Tab1, 10, "claude-code", "tab 1"), new(Tab2, 20, null, "tab 2") };
        if (secondWorkspace) terminals.Add(new(Tab3, 40, "codex", "tab 1"));
        List<(int, int, string, TimeSpan?)> table =
        [
            (10, 1, "-zsh", null), (11, 10, "node /opt/bin/claude --resume", null), (12, 11, "claude -p fix chat", null), (13, 12, "codex exec lint", null), (14, 11, "sleep 600", null), (15, 13, "make build", null),
            (20, 1, "-zsh", null), (21, 20, "vite --port 5173", null), (30, 1, "csharp-ls", null), (31, 1, "typescript-language-server --stdio", null),
            // A plain terminal's long-running dev server with its child, and a command that has only just started.
            (22, 20, "npm run dev", TimeSpan.FromMinutes(2)), (23, 22, "node vite", TimeSpan.FromMinutes(2)), (24, 20, "grep x", TimeSpan.FromSeconds(1))
        ];
        if (secondWorkspace) table.AddRange([(40, 1, "-zsh", null), (41, 40, "codex --model x", null), (32, 1, "pyright-langserver", null)]);
        List<(string, string, int)> servers = [("/w/alpha", "csharp", 30), ("/w/alpha", "typescript", 31)];
        if (secondWorkspace) servers.Add(("/w/beta", "python", 32));
        return new FakeSource
        {
            Inputs = new(terminals, table, servers, [(Tab1, 14, "dev watcher"), (Tab2, 21, "")], id => id == "csharp" ? "C# (Roslyn)" : id),
            Cpu = table.ToDictionary(row => row.Item1, _ => TimeSpan.Zero)
        };
    }

    internal static async Task Run(string root)
    {
        Tree();
        Sampling();
        await Stopping();
        if (OperatingSystem.IsWindows()) { Console.WriteLine("SKIP processes plugin switch: the checks start sleep and a POSIX shell"); return; }
        await PluginSwitch(Path.Combine(root, "processes-switch"));
        Console.WriteLine("PASS processes: the tree groups language servers, agents and spawned agents with nesting and sums, the trend is capped, sampling happens only when asked, stops take the right path, and disabling the plugin removes owned-process management while enabling keeps it");
    }

    // Each pid's CPU percent is its own number, so sums are checkable by addition.
    private static ProcessSnapshot Snapshot(FakeSource source) =>
        ProcessModel.Build(source.Inputs, pid => (pid, pid * 1_000_000L), source.WorkspaceLabel, new ProcessHistory(), 1).Snapshot;

    private static ProcessNode? TryFind(IEnumerable<ProcessNode> nodes, string id) =>
        nodes.Select(node => node.Id == id ? node : TryFind(node.Children, id)).FirstOrDefault(found => found is not null);

    private static ProcessNode Find(IEnumerable<ProcessNode> nodes, string id) => TryFind(nodes, id) ?? throw new InvalidOperationException("No node " + id);

    private static void Tree()
    {
        var snapshot = Snapshot(Fixture(false));
        Require(snapshot.Groups.Select(group => group.Id).SequenceEqual([ProcessModel.LanguageGroup, ProcessModel.AgentGroup, ProcessModel.SpawnedGroup]), "The top groups must be Language servers, AI agents and daemons, Spawned agents.");
        Require(snapshot.Groups.Select(group => group.Label).SequenceEqual(["Language servers", "AI agents and daemons", "Spawned processes"]), "The group labels changed.");
        var languages = snapshot.Groups[0];
        Require(languages.Children.Select(node => node.Label).SequenceEqual(["C# (Roslyn)", "typescript"]), "Language servers must list one row per running server under the name Code Intelligence shows, or the id when unnamed.");
        Require(languages.Cpu == 61 && languages.MemoryBytes == 61_000_000, "A group's values must be the sum of its rows.");
        var longLine = Snapshot(new FakeSource { Inputs = new([], [(40, 1, "codex -c " + new string('x', 5000), null)], [("/w/alpha", "csharp", 40)], []) });
        Require(longLine.Groups[0].Children.Single().Detail.Length < ProcessModel.DetailCommandLimit + 60, "A tooltip must not carry a whole launch line.");
        var agent = Find(snapshot.Groups, "agent:/w/alpha\0t1");
        Require(agent.Label == "Claude Code" && agent.Pid == 11 && agent.Detail.Contains("pid 11") && agent.Detail.Contains("tab 1"), "The agent row is the agent process below the shell, with pid and tab in its tooltip.");
        Require(snapshot.Groups[1].Children.Count == 1 && snapshot.Groups[1].Children[0].Id == agent.Id, "Only terminals with an agent are in AI agents and daemons.");
        Require(TryFind(snapshot.Groups, "parent:/w/alpha\0t1") is null && agent.CanStop, "Everything forked from an agent hangs under its own row, with no separate parent row.");
        var child = Find(agent.Children, "spawn:12");
        Require(child.Label == "claude -p fix chat" && child.Children.Single().Id == "spawn:13", "A process nests under the process that forked it, whatever its depth.");
        Require(Find(child.Children, "spawn:13").Children.Single() is { Id: "spawn:15", Label: "make build" }, "Every forked process is listed under the agent, not only agents.");
        Require(agent.Children.Select(node => node.Id).SequenceEqual(["spawn:12", "owned:14"]) && Find(agent.Children, "owned:14").Label == "dev watcher", "A reported process hangs under its agent, named by its description.");
        var plain = Find(snapshot.Groups, "parent:/w/alpha\0t2");
        Require(plain.Label == "of Terminal · tab 2", "A terminal without an agent still names the starter of what it owns.");
        var server = Find(plain.Children, "spawn:22");
        Require(server.Label == "npm run dev" && server.Detail.Contains("up 2m") && server.Children.Single().Id == "spawn:23" && TryFind(snapshot.Groups, "spawn:24") is null,
            "A plain terminal lists what has been running a while, nested under its parent, and not a command that has only just started.");
        Require(snapshot.Groups[1].Cpu == 11 + 12 + 13 + 14 + 15 && snapshot.Groups[2].Cpu == 21 + 22 + 23 && snapshot.TotalCpu == snapshot.Groups.Sum(group => group.Cpu) && snapshot.TotalMemoryBytes == snapshot.Groups.Sum(group => group.MemoryBytes), "The total is the sum of the groups and no process is counted twice.");
        Require(Find(snapshot.Groups, "spawn:12").Cpu == 12 + 13 + 15, "A row includes the spawned agents nested under it.");

        var two = Snapshot(Fixture(true));
        Require(two.Groups[0].Children.Select(node => node.Kind).SequenceEqual(["workspace", "workspace"]) && two.Groups[0].Children.Select(node => node.Label).SequenceEqual(["alpha", "beta"]), "With rows from two workspaces, each group nests its rows under workspace labels.");
        Require(Find(two.Groups, "workspace:" + ProcessModel.AgentGroup + ":/w/beta").Children.Single().Label == "Codex", "A workspace node holds that workspace's rows.");
        Require(snapshot.Groups[0].Children.All(node => node.Kind == "language"), "With one workspace there are no workspace nodes.");
    }

    private static void Sampling()
    {
        var source = Fixture(false);
        long now = 0;
        var sampler = new ProcessSampler(source) { Clock = () => now, Frequency = 1, MinimumInterval = TimeSpan.FromSeconds(1) };
        Require(sampler.Samples == 0, "Nothing may sample before it is asked.");
        var first = sampler.Sample();
        Require(sampler.Samples == 1 && first.Sequence == 1, "A request samples.");
        Require(ReferenceEquals(sampler.Sample(), first) && sampler.Samples == 1, "Calls closer than the minimum interval share one sample.");
        for (var step = 1; step <= 40; step++)
        {
            now += 2;
            foreach (var pid in source.Cpu.Keys.ToArray()) source.Cpu[pid] += TimeSpan.FromSeconds(1);
            sampler.Sample();
        }
        var last = sampler.Sample();
        Require(sampler.Samples == 41, "Every spaced request samples once: " + sampler.Samples);
        var trend = Find(last.Groups, "spawn:12").Trend;
        Require(trend.Count == ProcessesPlugin.TrendLength, "A trend keeps at most " + ProcessesPlugin.TrendLength + " samples, got " + trend.Count);
        Require(Math.Abs(Find(last.Groups, "owned:14").Cpu - 50) < 0.5, "One CPU second per two wall seconds is 50 percent, got " + Find(last.Groups, "owned:14").Cpu);
        Require(Math.Abs(Find(last.Groups, "spawn:12").Cpu - 150) < 0.5, "A row sums itself and what is nested under it.");
        source.Inputs = source.Inputs with { Owned = [] };
        now += 2;
        Require(TryFind(sampler.Sample().Groups, "owned:14") is null && sampler.Samples == 42, "A process that left the registry leaves the tree on the next sample.");
    }

    private static async Task Stopping()
    {
        var source = Fixture(false);
        var sampler = new ProcessSampler(source);
        Require(await sampler.StopAsync(ProcessModel.LanguageGroup, default) == 0, "Nothing is stoppable before a snapshot.");
        sampler.Sample();
        Require(await sampler.StopAsync("lang:/w/alpha\0csharp", default) == 1 && source.Stopped.Single() == "lang:alpha/csharp", "A language server stops through the language service, never a signal.");
        source.Stopped.Clear();
        Require(await sampler.StopAsync(ProcessModel.LanguageGroup, default) == 2 && source.Stopped.All(item => item.StartsWith("lang:", StringComparison.Ordinal)), "Stop group stops each language server through the service.");
        source.Stopped.Clear();
        sampler.Sample();
        Require(await sampler.StopAsync("agent:/w/alpha\0t1", default) == 5 && source.Stopped.Count == 5 && source.Stopped.Contains("pid:11") && source.Stopped.Contains("pid:15"), "An agent row stops its process and everything forked from it on the graceful path.");
        source.Stopped.Clear();
        var ownOnly = new ProcessSampler(source) { StopAgentChildren = () => false };
        ownOnly.Sample();
        Require(await ownOnly.StopAsync("agent:/w/alpha\0t1", default) == 1 && source.Stopped.Single() == "pid:11", "With the setting off an agent row stops only its own process.");
        source.Stopped.Clear();
        Require(await ownOnly.StopAsync("spawn:12", default) == 3 && source.Stopped.Order().SequenceEqual(["pid:12", "pid:13", "pid:15"]), "A child row named directly still stops itself and what it forked.");
        source.Stopped.Clear();
        sampler.Sample();
        Require(await sampler.StopAsync(ProcessModel.SpawnedGroup, default) == 3 && source.Stopped.Order().SequenceEqual(["pid:21", "pid:22", "pid:23"]), "Stop group on spawned processes stops every row under it.");
        source.Stopped.Clear();
        Require(await sampler.StopAsync("pid:1", default) == 0 && await sampler.StopAsync("spawn:999", default) == 0 && source.Stopped.Count == 0, "An id the snapshot does not hold stops nothing.");
    }

    private static ProcessNode Leaf(string id, double cpu, int trend = 3) =>
        new(id, "spawned", id, "pid " + id, 1, cpu, 1_000_000, [.. Enumerable.Range(0, trend).Select(item => cpu + item)], [], true);

    private static ProcessSnapshot Pane(int round, params string[] without)
    {
        var spawned = Enumerable.Range(0, 30).Select(index => $"spawn:{index}").Where(id => !without.Contains(id)).Select(id => Leaf(id, round)).ToArray();
        ProcessNode[] groups =
        [
            new(ProcessModel.LanguageGroup, "group", "Language servers", "", null, round, 5, [round], [Leaf("lang:a", round), Leaf("lang:b", round)], true),
            new(ProcessModel.AgentGroup, "group", "AI agents and daemons", "", null, round, 5, [round], [], false),
            new(ProcessModel.SpawnedGroup, "group", "Spawned processes", "", null, round, 5, [round], spawned, true)
        ];
        return new(round, groups, round, 15);
    }

    /// <summary>
    /// Polling must not rebuild the pane: snapshots with the same structure and new numbers keep the row controls, the scroll offset, the
    /// selection and a collapsed group; rows of vanished processes go; an equal snapshot touches nothing; a hidden pane asks for nothing.
    /// </summary>
    // Needs the headless platform: the gate runs it inside UiChecks.Run, the focused modes set the platform up first. A host-section case must not create
    // the platform itself, or later host cases run beside a live Avalonia platform and one of them hangs.
    internal static void RunUi()
    {
        var shown = false;
        var next = Pane(0);
        var panel = new ProcessesPanel(() => Task.FromResult(next), _ => Task.CompletedTask, message => throw new InvalidOperationException(message), () => shown);
        var window = new Window { Width = 400, Height = 150, Content = panel };
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
        window.Show();
        try
        {
            panel.Poll(); panel.Poll(); panel.Poll();
            Require(panel.Requests == 0, "A hidden pane must not ask the host for a snapshot.");
            shown = true;
            panel.Poll();
            Require(panel.Requests == 1 && panel.RowCount == 3 + 2 + 30, $"The first visible poll asks once and shows the whole tree, got {panel.Requests} requests and {panel.RowCount} rows.");
            Avalonia.Threading.Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var ids = Enumerable.Range(0, 30).Select(index => $"spawn:{index}").Concat([ProcessModel.LanguageGroup, ProcessModel.SpawnedGroup, "lang:a"]).ToArray();
            var first = ids.ToDictionary(id => id, id => panel.RowOf(id) ?? throw new InvalidOperationException("No row " + id));

            panel.Scroll.Offset = new Vector(0, 60);
            window.UpdateLayout();
            var offset = panel.Scroll.Offset.Y;
            Require(offset > 0, "The pane must be taller than its window for this check.");
            panel.Choose("spawn:12", false);
            panel.Choose(ProcessModel.LanguageGroup, true);
            Require(panel.RowOf("lang:a") is null && panel.Selected == ProcessModel.LanguageGroup, "Collapsing a group removes its rows and selects it.");
            panel.Choose("spawn:12", false);
            for (var round = 1; round <= 6; round++)
            {
                panel.Apply(Pane(round));
                window.UpdateLayout();
            }
            foreach (var id in ids.Where(id => id != "lang:a"))
                Require(ReferenceEquals(first[id], panel.RowOf(id)), $"Row {id} was replaced by a poll.");
            Require(panel.Scroll.Offset.Y == offset, "A poll moved the scroll offset.");
            Require(panel.Selected == "spawn:12", "A poll lost the selection.");
            Require(panel.RowOf("lang:a") is null && panel.RowOf("lang:b") is null, "A collapsed group re-expanded on a poll.");

            panel.Apply(Pane(7, "spawn:7"));
            Require(panel.RowOf("spawn:7") is null && panel.RowCount == 3 + 29, "The row of a vanished process must go and no other.");
            foreach (var id in ids.Where(id => id is not ("lang:a" or "spawn:7")))
                Require(ReferenceEquals(first[id], panel.RowOf(id)), $"Row {id} was replaced when a sibling vanished.");
            // An equal snapshot is skipped entirely: a mark made on a row survives it.
            var marked = panel.RowOf("spawn:3")!;
            marked.Margin = new Thickness(1);
            panel.Apply(Pane(7, "spawn:7"));
            Require(marked.Margin == new Thickness(1), "An unchanged snapshot rewrote rows.");
            panel.Choose(ProcessModel.LanguageGroup, true);
            Require(panel.RowOf("lang:a") is not null, "Toggling the group again expands it.");
            panel.Apply(Pane(8, "spawn:7"));
            Require(panel.RowOf("lang:a") is not null && ReferenceEquals(first["spawn:3"], panel.RowOf("spawn:3")), "A returning node keeps its place.");
            ScopeChecks(window);
            Console.WriteLine("PASS processes pane: polls reconcile rows in place, keeping controls, scroll, selection and collapse, dropping vanished rows, skipping equal snapshots; a hidden pane asks for nothing");
        }
        finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static ProcessNode Owned(string id, string workspace, double cpu) =>
        Leaf(id, cpu) with { Workspace = workspace };

    /// <summary>The project scope: a pane shows only its project's rows and sums, the toggle reveals the rest in place, stops stay on visible rows.</summary>
    private static void ScopeChecks(Window window)
    {
        ProcessNode[] mine = [Owned("spawn:1", "/p/a", 10), Owned("spawn:2", "/p/a2", 20)];
        ProcessNode[] other = [Owned("spawn:3", "/q/b", 40)];
        ProcessSnapshot Mixed() => new(1,
        [
            new(ProcessModel.LanguageGroup, "group", "Language servers", "", null, 0, 0, [], [], false),
            new(ProcessModel.SpawnedGroup, "group", "Spawned processes", "", null, 70, 3_000_000, [70], [.. mine, .. other], true)
        ], 70, 3_000_000);
        var filtered = ProcessesPanel.Filter(Mixed(), new HashSet<string> { "/p/a", "/p/a2" });
        Require(filtered.TotalCpu == 30 && filtered.TotalMemoryBytes == 2_000_000 && filtered.Groups[1].Children.Count == 2 && filtered.Groups[1].Cpu == 30 && filtered.Groups[1].Trend.Count == 3,
            "The filter keeps the project's rows and recomputes group sums and the total.");

        var stopped = new List<string>();
        var saved = new List<bool>();
        var view = new ProcessesView { Persist = saved.Add };
        var panel = new ProcessesPanel(() => Task.FromResult(Mixed()), id => { stopped.Add(id); return Task.CompletedTask; }, message => throw new InvalidOperationException(message), () => true,
            () => new HashSet<string> { "/p/a", "/p/a2" }, view);
        window.Content = panel;
        panel.Apply(Mixed());
        Require(panel.RowCount == 2 + 2 && panel.RowOf("spawn:3") is null, "By default only the pane's own project is shown.");
        var box = panel.GetLogicalDescendants().OfType<CheckBox>().Single();
        box.IsChecked = true; box.IsChecked = false;
        Require(saved.SequenceEqual([true, false]) && !view.AllProjects, "Changing the All projects box tells the plugin so it can keep the choice for the next run.");
        var row = panel.RowOf("spawn:1")!;
        panel.Choose(ProcessModel.SpawnedGroup, false);
        Require(panel.StopTargets(panel.Snapshot!.Groups[1]).SequenceEqual(["spawn:1", "spawn:2"]), "Stop group in a scoped pane names only visible rows.");
        view.AllProjects = true;
        panel.Apply(Mixed());
        Require(panel.RowCount == 2 + 3 && panel.RowOf("spawn:3") is not null && ReferenceEquals(row, panel.RowOf("spawn:1")) && panel.Selected == ProcessModel.SpawnedGroup,
            "Showing all projects adds rows in place and keeps controls and selection.");
        Require(panel.StopTargets(panel.Snapshot!.Groups[1]).SequenceEqual([ProcessModel.SpawnedGroup]), "Unscoped, Stop group stops the whole group.");
        view.AllProjects = false;
        panel.Apply(new ProcessSnapshot(2, [.. Mixed().Groups.Select(group => group with { Children = [.. group.Children.Where(child => child.Workspace != "/p/a" && child.Workspace != "/p/a2")] })], 40, 1_000_000));
        Require(panel.RowCount == 0 && panel.Snapshot!.TotalCpu == 0, "A project with nothing running shows no rows (and the message).");
    }

    private static async Task PluginSwitch(string directory)
    {
        Directory.CreateDirectory(directory);
        var state = new HostStateStore(directory);
        await using var service = new PtyTerminalService("/bin/sh", recordingsDirectory: Path.Combine(directory, "terminals"));
        state.Terminals = service;
        await using var runtime = new PluginRuntime(new PluginHostSeams { StateDirectory = directory, State = state, Builtins = BuiltinPlugins.All });
        await runtime.Start();
        async Task Set(bool on)
        {
            await state.ChangeAsync([HostStateChange.PluginEnabled(ProcessesPlugin.Id, on)]);
            await runtime.Schedule();
        }
        var tab = new TerminalRef(directory, "tab");
        bool HasTools() => runtime.McpTools(tab, directory).Select(tool => tool.Name).Intersect([ProcessesPlugin.ReportTool, ProcessesPlugin.ReleaseTool]).Count() == 2;
        bool HasPrompt() => runtime.PromptTexts(ClaudeCodeContract.Id, PromptPlacement.Section).Any(text => text.Contains("owned_process_report", StringComparison.Ordinal));

        Require(ProcessesPlugin.Manifest.EnabledByDefault, "The Processes plugin must be on by default.");
        Require(service.Owned.Enabled && HasTools() && HasPrompt(), "Enabled (the default), the plugin owns the report and release tools, the prompt section and the registry switch.");
        using var survivor = Process.Start(new ProcessStartInfo("sleep", "600") { UseShellExecute = false }) ?? throw new InvalidOperationException("sleep did not start.");
        try
        {
            service.Owned.Report(tab, survivor.Id, "kept alive", []);
            Require(service.Owned.List().Count == 1, "An enabled plugin keeps the registry working.");

            await Set(false);
            Require(!service.Owned.Enabled && !HasTools() && !HasPrompt(), "Disabled, there is no owned-process registration tool, no prompt section and no management.");
            Require(service.Owned.List().Count == 0 && !survivor.HasExited, "Disabling forgets the records and leaves the processes running.");
            try { service.Owned.Report(tab, survivor.Id, null, []); throw new InvalidOperationException("A disabled plugin must refuse to register a process."); }
            catch (ArgumentException) { }
            await service.Owned.StopAllAsync();
            Require(!survivor.HasExited, "Disabled, the quit sweep stops no agent-owned process.");

            await Set(true);
            Require(service.Owned.Enabled && HasTools() && HasPrompt(), "Enabling again restores the tools, the prompt section and the registry without a restart.");
            service.Owned.Report(tab, survivor.Id, "again", []);
            Require(service.Owned.List().Count == 1, "Management works again after re-enabling.");
        }
        finally
        {
            if (!survivor.HasExited) { survivor.Kill(); survivor.WaitForExit(); }
        }
    }
}