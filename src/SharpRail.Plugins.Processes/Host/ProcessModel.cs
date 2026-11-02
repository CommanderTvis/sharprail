using System.Globalization;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Processes.Host;

/// <summary>A terminal as the model sees it: its shell, the agent the roster records for it (null for none) and its tab label.</summary>
internal sealed record TerminalInfo(TerminalRef Terminal, int? ShellPid, string? AgentKind, string Tab);

/// <summary>Everything one snapshot is built from; the host fills it from the real sources and checks from fakes.</summary>
internal sealed record ProcessInputs(
    IReadOnlyList<TerminalInfo> Terminals,
    IReadOnlyList<(int Pid, int ParentPid, string Command)> Table,
    IReadOnlyList<(string Workspace, string ServerId, int Pid)> Servers,
    IReadOnlyList<(TerminalRef Terminal, int Pid, string Description)> Owned,
    Func<string, string>? ServerName = null);

/// <summary>What a stop on a row does: end a process, or stop a language server through the language service.</summary>
internal sealed record StopTarget(int? Pid, string Command, string? Workspace, string? ServerId);

/// <summary>The last <see cref="ProcessesPlugin.TrendLength"/> CPU samples of each row, in memory only.</summary>
internal sealed class ProcessHistory
{
    private readonly Dictionary<string, Queue<double>> series = [];
    internal void Record(string id, double value)
    {
        if (!series.TryGetValue(id, out var queue)) series[id] = queue = new();
        queue.Enqueue(Math.Round(value, 1));
        while (queue.Count > ProcessesPlugin.TrendLength) queue.Dequeue();
    }
    internal IReadOnlyList<double> Trend(string id) => series.TryGetValue(id, out var queue) ? [.. queue] : [];
    /// <summary>Forgets every row that is not in <paramref name="live"/>.</summary>
    internal void Keep(HashSet<string> live) { foreach (var id in series.Keys.Where(id => !live.Contains(id)).ToArray()) series.Remove(id); }
}

/// <summary>
/// The tree (spec, Tree model): Language servers, AI agents and daemons, Spawned agents. Pure: it reads only its inputs and the usage it is given.
/// </summary>
internal static class ProcessModel
{
    internal const string LanguageGroup = "group:language", AgentGroup = "group:agents", SpawnedGroup = "group:spawned";
    private static readonly string[] AgentExecutables = ["claude", "codex", "omp"];

    private sealed class Row(string id, string kind, string label, string detail, int? pid, StopTarget? stop, string? workspace)
    {
        internal string Id = id, Kind = kind, Label = label, Detail = detail;
        internal int? Pid = pid;
        internal StopTarget? Stop = stop;
        internal string? Workspace = workspace;
        internal List<Row> Children = [];
    }

    /// <param name="usage">CPU percent (of one core) and memory for a pid, or null when unknown.</param>
    /// <param name="workspaceLabel">The label a workspace root is shown under.</param>
    internal static (ProcessSnapshot Snapshot, IReadOnlyDictionary<string, StopTarget> Stops) Build(ProcessInputs inputs, Func<int, (double Cpu, long Memory)?> usage,
        Func<string, string> workspaceLabel, ProcessHistory history, long sequence)
    {
        var table = inputs.Table.ToDictionary(row => row.Pid);
        var children = inputs.Table.ToLookup(row => row.ParentPid, row => row.Pid);
        var claimed = new HashSet<int>();

        var languages = new Row(LanguageGroup, "group", "Language servers", "", null, null, null);
        foreach (var (workspace, server, pid) in inputs.Servers.OrderBy(item => item.Workspace, StringComparer.Ordinal).ThenBy(item => item.ServerId, StringComparer.Ordinal))
        {
            if (!claimed.Add(pid)) continue;
            var command = table.TryGetValue(pid, out var found) ? found.Command : "";
            languages.Children.Add(new($"lang:{workspace}\0{server}", "language", inputs.ServerName?.Invoke(server) ?? server, Describe(pid, workspaceLabel(workspace), command), pid,
                new(null, command, workspace, server), workspace));
        }

        var agents = new Row(AgentGroup, "group", "AI agents and daemons", "", null, null, null);
        var spawned = new Row(SpawnedGroup, "group", "Spawned agents", "", null, null, null);
        var ownedByTerminal = inputs.Owned.ToLookup(item => item.Terminal);
        foreach (var terminal in inputs.Terminals)
        {
            var workspace = terminal.Terminal.WorkspaceId;
            var subtree = terminal.ShellPid is { } shell ? Descendants(children, shell) : [];
            var name = AgentName(terminal.AgentKind);
            Row? root = null;
            if (terminal.AgentKind is not null)
            {
                var rootPid = subtree.Where(pid => table.TryGetValue(pid, out var row) && ExecutableOf(row.Command) is { } exe && exe.Contains(terminal.AgentKind.Split('-')[0], StringComparison.OrdinalIgnoreCase))
                    .Select(pid => (int?)pid).FirstOrDefault() ?? terminal.ShellPid;
                if (rootPid is { } pid && claimed.Add(pid))
                {
                    var command = table.TryGetValue(pid, out var row) ? row.Command : "";
                    root = new($"agent:{workspace}\0{terminal.Terminal.TabKey}", "agent", name, Describe(pid, $"{terminal.Tab} · {workspaceLabel(workspace)}", command), pid, new(pid, command, null, null), workspace);
                    agents.Children.Add(root);
                    subtree = Descendants(children, pid);
                }
            }
            // Spawned agents: agent processes below the terminal, nested under the nearest agent process above them; the owned
            // processes it reported hang from the process that reported them (the terminal's parent row).
            var starter = new Row($"parent:{workspace}\0{terminal.Terminal.TabKey}", "parent", $"of {name} · {terminal.Tab}", workspaceLabel(workspace), null, null, workspace);
            // Under a recognised agent every process forked from it hangs below its row; otherwise only agent-like processes are listed.
            var holder = root ?? starter;
            var nested = new Dictionary<int, Row>();
            var reported = ownedByTerminal[terminal.Terminal].Select(item => item.Pid).ToHashSet();
            foreach (var pid in subtree.OrderBy(pid => pid))
            {
                if (reported.Contains(pid) || !table.TryGetValue(pid, out var row) || root is null && ExecutableOf(row.Command) is null || !claimed.Add(pid)) continue;
                var node = new Row($"spawn:{pid}", "spawned", Short(row.Command), Describe(pid, workspaceLabel(workspace), row.Command), pid, new(pid, row.Command, null, null), workspace);
                var above = Ancestors(table, pid).FirstOrDefault(nested.ContainsKey);
                (above != 0 ? nested[above] : holder).Children.Add(node);
                nested[pid] = node;
            }
            foreach (var (_, pid, description) in ownedByTerminal[terminal.Terminal])
            {
                if (!claimed.Add(pid)) continue;
                var command = table.TryGetValue(pid, out var row) ? row.Command : "";
                var node = new Row($"owned:{pid}", "owned", description.Length > 0 ? description : Short(command), Describe(pid, workspaceLabel(workspace), command), pid, new(pid, command, null, null), workspace);
                var above = Ancestors(table, pid).FirstOrDefault(nested.ContainsKey);
                (above != 0 ? nested[above] : holder).Children.Add(node);
                nested[pid] = node;
            }
            if (starter.Children.Count > 0) spawned.Children.Add(starter);
        }

        foreach (var group in new[] { languages, agents, spawned }) Nest(group);
        var stops = new Dictionary<string, StopTarget>(StringComparer.Ordinal);
        var live = new HashSet<string>(StringComparer.Ordinal);
        var groups = new[] { languages, agents, spawned }.Select(group => Convert(group, usage, history, stops, live, 0)).ToArray();
        history.Keep(live);
        var totalCpu = groups.Sum(group => group.Cpu);
        var totalMemory = groups.Sum(group => group.MemoryBytes);
        return (new ProcessSnapshot(sequence, groups, Math.Round(totalCpu, 1), totalMemory), stops);

        // With rows from several workspaces under one group, the rows sit under their workspace's label (collapsible in the pane).
        void Nest(Row group)
        {
            if (group.Children.Select(row => row.Workspace).Distinct().Count() < 2) return;
            group.Children = [.. group.Children.GroupBy(row => row.Workspace ?? "").OrderBy(item => workspaceLabel(item.Key), StringComparer.Ordinal)
                .Select(item => new Row($"workspace:{group.Id}:{item.Key}", "workspace", workspaceLabel(item.Key), item.Key, null, null, item.Key) { Children = [.. item] })];
        }
    }

    // Returns the node and its totals; groups, workspace and parent nodes sum the rows under them, rows keep their own usage plus their children's.
    private static ProcessNode Convert(Row row, Func<int, (double Cpu, long Memory)?> usage, ProcessHistory history, Dictionary<string, StopTarget> stops, HashSet<string> live, int depth)
    {
        var nodes = row.Children.Select(child => Convert(child, usage, history, stops, live, depth + 1)).ToArray();
        var own = row.Pid is { } pid ? usage(pid) : null;
        var cpu = (own?.Cpu ?? 0) + nodes.Sum(node => node.Cpu);
        var memory = (own?.Memory ?? 0) + nodes.Sum(node => node.MemoryBytes);
        if (row.Stop is { } stop) stops[row.Id] = stop;
        live.Add(row.Id);
        history.Record(row.Id, cpu);
        var canStop = row.Stop is not null || nodes.Any(node => node.CanStop);
        return new(row.Id, row.Kind, row.Label, row.Detail, row.Pid, Math.Round(cpu, 1), memory, history.Trend(row.Id), nodes, canStop, row.Workspace);
    }

    /// <summary>The stop targets at or below <paramref name="id"/>, in tree order; empty for an unknown id. With <paramref name="agentChildren"/> off an agent row contributes only itself.</summary>
    internal static IReadOnlyList<(string Id, StopTarget Target)> StopsUnder(ProcessSnapshot snapshot, IReadOnlyDictionary<string, StopTarget> stops, string id, bool agentChildren = true)
    {
        var found = new List<(string, StopTarget)>();
        bool Walk(ProcessNode node, bool inside)
        {
            inside |= node.Id == id;
            var hit = node.Id == id;
            if (inside && stops.TryGetValue(node.Id, out var target)) found.Add((node.Id, target));
            // Without agentChildren an agent's row stands for its own process only: what it forked is stopped by naming it.
            var below = inside && !agentChildren && node.Kind == "agent" ? false : inside;
            foreach (var child in node.Children) hit |= Walk(child, below);
            return hit;
        }
        foreach (var group in snapshot.Groups) Walk(group, false);
        return found;
    }

    /// <summary>The longest command line a tooltip shows; an agent's launch line can run to kilobytes of injected instructions.</summary>
    internal const int DetailCommandLimit = 160;

    private static string Describe(int pid, string where, string command) =>
        string.Join(" · ", new[] { "pid " + pid.ToString(CultureInfo.InvariantCulture), where, Shorten(command) }.Where(part => part.Length > 0));

    private static string Shorten(string command)
    {
        var line = string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= DetailCommandLimit ? line : line[..DetailCommandLimit].TrimEnd() + "…";
    }

    private static string AgentName(string? kind) => kind switch
    {
        null => "Terminal",
        var value when value.Contains("claude", StringComparison.OrdinalIgnoreCase) => "Claude Code",
        var value when value.Contains("codex", StringComparison.OrdinalIgnoreCase) => "Codex",
        var value when value.Contains("omp", StringComparison.OrdinalIgnoreCase) => "omp",
        var value => value
    };

    // The agent executable a command runs, looking through an interpreter ("node /x/claude"): its first two tokens' base names.
    internal static string? ExecutableOf(string command)
    {
        foreach (var token in command.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2))
        {
            var name = token[(token.LastIndexOfAny(['/', '\\']) + 1)..];
            if (AgentExecutables.Contains(name, StringComparer.OrdinalIgnoreCase)) return name.ToLowerInvariant();
        }
        return null;
    }

    private static string Short(string command)
    {
        var text = command.Trim();
        var first = text.Split(' ', 2)[0];
        var name = first[(first.LastIndexOfAny(['/', '\\']) + 1)..];
        var rest = text.Length > first.Length ? text[first.Length..].Trim() : "";
        var line = rest.Length > 0 ? name + " " + rest : name;
        return line.Length > 60 ? line[..59] + "…" : line;
    }

    private static List<int> Descendants(ILookup<int, int> children, int root)
    {
        var found = new List<int>();
        var queue = new Queue<int>([root]);
        var seen = new HashSet<int> { root };
        while (queue.Count > 0)
            foreach (var child in children[queue.Dequeue()])
                if (seen.Add(child)) { found.Add(child); queue.Enqueue(child); }
        return found;
    }

    // The parent chain of a pid, nearest first, ending where the table does.
    private static IEnumerable<int> Ancestors(Dictionary<int, (int Pid, int ParentPid, string Command)> table, int pid)
    {
        var seen = new HashSet<int> { pid };
        while (table.TryGetValue(pid, out var row) && seen.Add(row.ParentPid)) { pid = row.ParentPid; yield return pid; }
    }
}