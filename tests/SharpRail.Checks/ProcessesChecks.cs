using System.Diagnostics;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.Processes;
using SharpRail.Plugins.Processes.Host;

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
        List<(int, int, string)> table =
        [
            (10, 1, "-zsh"), (11, 10, "node /opt/bin/claude --resume"), (12, 11, "claude -p fix chat"), (13, 12, "codex exec lint"), (14, 11, "sleep 600"),
            (20, 1, "-zsh"), (21, 20, "vite --port 5173"), (30, 1, "csharp-ls"), (31, 1, "typescript-language-server --stdio")
        ];
        if (secondWorkspace) table.AddRange([(40, 1, "-zsh"), (41, 40, "codex --model x"), (32, 1, "pyright-langserver")]);
        List<(string, string, int)> servers = [("/w/alpha", "csharp", 30), ("/w/alpha", "typescript", 31)];
        if (secondWorkspace) servers.Add(("/w/beta", "python", 32));
        return new FakeSource
        {
            Inputs = new(terminals, table, servers, [(Tab1, 14, "dev watcher"), (Tab2, 21, "")]),
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
        Require(snapshot.Groups.Select(group => group.Label).SequenceEqual(["Language servers", "AI agents and daemons", "Spawned agents"]), "The group labels changed.");
        var languages = snapshot.Groups[0];
        Require(languages.Children.Select(node => node.Label).SequenceEqual(["csharp", "typescript"]), "Language servers must list one row per running server.");
        Require(languages.Cpu == 61 && languages.MemoryBytes == 61_000_000, "A group's values must be the sum of its rows.");
        var agent = Find(snapshot.Groups, "agent:/w/alpha\0t1");
        Require(agent.Label == "Claude Code" && agent.Pid == 11 && agent.Detail.Contains("pid 11") && agent.Detail.Contains("tab 1"), "The agent row is the agent process below the shell, with pid and tab in its tooltip.");
        Require(snapshot.Groups[1].Children.Count == 1 && snapshot.Groups[1].Children[0].Id == agent.Id, "Only terminals with an agent are in AI agents and daemons.");
        var starter = Find(snapshot.Groups, "parent:/w/alpha\0t1");
        Require(starter.Label == "of Claude Code · tab 1" && starter.Kind == "parent" && starter.CanStop, "Spawned agents nest under a row naming the agent that started them.");
        var child = Find(starter.Children, "spawn:12");
        Require(child.Label == "claude -p fix chat" && child.Children.Single().Id == "spawn:13", "A spawned agent nests under the agent process that started it, whatever its depth.");
        Require(starter.Children.Select(node => node.Id).SequenceEqual(["spawn:12", "owned:14"]) && Find(starter.Children, "owned:14").Label == "dev watcher", "A reported process hangs under the terminal that reported it, named by its description.");
        Require(Find(snapshot.Groups, "parent:/w/alpha\0t2").Label == "of Terminal · tab 2", "A terminal without an agent still names the starter of what it owns.");
        Require(snapshot.Groups[2].Cpu == 12 + 13 + 14 + 21 && snapshot.TotalCpu == snapshot.Groups.Sum(group => group.Cpu) && snapshot.TotalMemoryBytes == snapshot.Groups.Sum(group => group.MemoryBytes), "The total is the sum of the groups and no process is counted twice.");
        Require(Find(snapshot.Groups, "spawn:12").Cpu == 12 + 13, "A row includes the spawned agents nested under it.");

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
        Require(Math.Abs(Find(last.Groups, "spawn:12").Cpu - 100) < 0.5, "A row sums itself and what is nested under it.");
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
        Require(await sampler.StopAsync("agent:/w/alpha\0t1", default) == 1 && source.Stopped.Single() == "pid:11", "An agent row stops its process on the graceful path.");
        source.Stopped.Clear();
        sampler.Sample();
        Require(await sampler.StopAsync(ProcessModel.SpawnedGroup, default) == 4 && source.Stopped.Order().SequenceEqual(["pid:12", "pid:13", "pid:14", "pid:21"]), "Stop group on spawned agents stops every row under it.");
        source.Stopped.Clear();
        Require(await sampler.StopAsync("pid:1", default) == 0 && await sampler.StopAsync("spawn:999", default) == 0 && source.Stopped.Count == 0, "An id the snapshot does not hold stops nothing.");
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