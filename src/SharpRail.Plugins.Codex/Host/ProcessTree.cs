using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SharpRail.Plugins.Codex.Host;

public sealed record ProcessRow(int Pid, int ParentPid, string Name);

/// <summary>One reading of the process table. A copy of the Claude Code plugin's, looking for <c>codex</c>: a plugin may not
/// reference another plugin's host half, and each builtin plugin stays extractable on its own.</summary>
public sealed partial class ProcessSnapshot
{
    public const int MaxDescendantDepth = 4;

    private readonly Dictionary<int, List<int>> children = [];
    private readonly Dictionary<int, int> parents = [];
    private readonly Dictionary<int, string> names = [];

    public ProcessSnapshot(IEnumerable<ProcessRow> rows)
    {
        foreach (var row in rows)
        {
            names[row.Pid] = row.Name;
            parents[row.Pid] = row.ParentPid;
            if (children.TryGetValue(row.ParentPid, out var siblings)) siblings.Add(row.Pid);
            else children[row.ParentPid] = [row.Pid];
        }
    }

    public IReadOnlyList<int> ChildrenOf(int pid) => children.TryGetValue(pid, out var found) ? found : [];
    public int? ParentOf(int pid) => parents.TryGetValue(pid, out var parent) ? parent : null;
    public string? NameOf(int pid) => names.GetValueOrDefault(pid);

    private static string BaseName(string command)
    {
        var cut = command.LastIndexOf('/');
        var name = cut == -1 ? command : command[(cut + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    public static IReadOnlyList<ProcessRow> ParseRows(string output)
    {
        var rows = new List<ProcessRow>();
        foreach (var line in output.Split('\n'))
        {
            var match = Row().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var pid) && int.TryParse(match.Groups[2].Value, out var parent))
                rows.Add(new(pid, parent, BaseName(match.Groups[3].Value)));
        }
        return rows;
    }

    public static ProcessSnapshot? Capture()
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ps", ["-Ao", "pid=,ppid=,comm="]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { process.Kill(); return null; }
            return process.ExitCode == 0 ? new(ParseRows(output.GetAwaiter().GetResult())) : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    /// <summary>A process's working directory: <c>/proc</c> on Linux, <c>lsof</c> elsewhere; null on Windows or failure.</summary>
    public static string? CaptureCwd(int pid)
    {
        if (pid <= 0 || OperatingSystem.IsWindows()) return null;
        if (OperatingSystem.IsLinux())
        {
            try { return new DirectoryInfo($"/proc/{pid}/cwd").ResolveLinkTarget(false)?.FullName; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }
        return Run("lsof", ["-a", "-d", "cwd", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "-Fn"])?
            .Split('\n').FirstOrDefault(line => line.StartsWith('n'))?[1..];
    }

    /// <summary>A process's full command line, as <c>ps</c> prints it; null on Windows or failure.</summary>
    public static string? CaptureCommand(int pid) =>
        pid <= 0 || OperatingSystem.IsWindows() ? null
            : Run("ps", ["-o", "args=", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)])?.Trim() is { Length: > 0 } line ? line : null;

    internal static bool? CaptureUiLaunch(int pid)
    {
        if (pid <= 0 || OperatingSystem.IsWindows()) return null;
        try
        {
            if (OperatingSystem.IsLinux())
                return File.ReadAllText($"/proc/{pid}/environ").Split('\0').Contains(CodexLaunch.UiLaunchPrefix);
            var environment = Run("ps", ["-Eww", "-o", "args=", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            return string.IsNullOrWhiteSpace(environment) ? null : environment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(CodexLaunch.UiLaunchPrefix);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? Run(string file, string[] arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000)) { process.Kill(); return null; }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    /// <summary>The nearest descendant of <paramref name="rootPid"/> named one of <paramref name="wanted"/>, breadth first.</summary>
    public (int Pid, string Name)? FindDescendant(int rootPid, IReadOnlyCollection<string> wanted, int maxDepth = MaxDescendantDepth)
    {
        var seen = new HashSet<int> { rootPid };
        var frontier = new List<int> { rootPid };
        for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<int>();
            foreach (var pid in frontier)
                foreach (var child in ChildrenOf(pid))
                {
                    if (!seen.Add(child)) continue;
                    if (NameOf(child) is { } name && wanted.Contains(name)) return (child, name);
                    next.Add(child);
                }
            frontier = next;
        }
        return null;
    }

    /// <summary>Whether this tab's <paramref name="self"/> agent was started by its <paramref name="outer"/> one, the tab's own agent.</summary>
    public bool RunsInsideAgent(int rootPid, string self, string outer)
    {
        var found = FindDescendant(rootPid, [self]);
        var pid = found is { } agent ? ParentOf(agent.Pid) : null;
        for (var depth = 0; pid is { } current && current != rootPid && depth < MaxDescendantDepth; depth++)
        {
            if (NameOf(current) == outer) return true;
            pid = ParentOf(current);
        }
        return false;
    }

    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s+(.*\S)\s*$")]
    private static partial Regex Row();
}

public sealed record AgentWatchTarget(string WorkspaceId, string TabKey, int Pid);

/// <summary>
/// Polls the process table while any terminal exists and reports when a tab's shell gains or loses a <c>codex</c>
/// descendant. Arming never sweeps inline, which would block an attach.
/// </summary>
public sealed class AgentWatch(Func<IReadOnlyList<AgentWatchTarget>> listTargets, Action<string, string, int> detected, Action<string, string> cleared,
    Func<ProcessSnapshot?>? capture = null) : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2500);
    private static readonly string[] DetectedAgents = ["codex"];

    private readonly Lock gate = new();
    private readonly Dictionary<(string, string), (int Pid, string Name)> agents = [];
    private readonly Func<ProcessSnapshot?> capture = capture ?? ProcessSnapshot.Capture;
    private Timer? timer;
    private bool stopped;
    private int sweeping;

    public string? AgentFor(string workspaceId, string tabKey)
    {
        lock (gate) return agents.GetValueOrDefault((workspaceId, tabKey)).Name;
    }

    public void Poke()
    {
        lock (gate)
        {
            if (stopped || timer is not null || listTargets().Count == 0) return;
            timer = new Timer(_ => Sweep(), null, PollInterval, PollInterval);
        }
    }

    public void Forget(string workspaceId, string tabKey)
    {
        lock (gate) agents.Remove((workspaceId, tabKey));
    }

    // A slow process listing must not let timer ticks overlap.
    public void Sweep()
    {
        if (Interlocked.Exchange(ref sweeping, 1) == 1) return;
        try { SweepOnce(); }
        finally { Volatile.Write(ref sweeping, 0); }
    }

    private void SweepOnce()
    {
        var targets = listTargets();
        var detections = new List<(string, string, int)>();
        var clears = new List<(string, string)>();
        if (targets.Count == 0)
        {
            lock (gate)
            {
                timer?.Dispose();
                timer = null;
                agents.Clear();
            }
            return;
        }
        if (capture() is not { } snapshot) return;
        lock (gate)
        {
            if (stopped) return;
            var live = new HashSet<(string, string)>();
            foreach (var target in targets)
            {
                var index = (target.WorkspaceId, target.TabKey);
                live.Add(index);
                var previous = agents.GetValueOrDefault(index);
                var found = snapshot.FindDescendant(target.Pid, DetectedAgents);
                var next = found ?? default;
                if (previous == next) continue;
                if (found is null)
                {
                    agents.Remove(index);
                    if (previous.Name is not null) clears.Add(index);
                }
                else
                {
                    agents[index] = next;
                    detections.Add((target.WorkspaceId, target.TabKey, found!.Value.Pid));
                }
            }
            foreach (var index in agents.Keys.Where(index => !live.Contains(index)).ToArray()) agents.Remove(index);
        }
        foreach (var (workspace, tab, pid) in detections) detected(workspace, tab, pid);
        foreach (var (workspace, tab) in clears) cleared(workspace, tab);
    }

    public void Dispose()
    {
        lock (gate)
        {
            stopped = true;
            timer?.Dispose();
            timer = null;
            agents.Clear();
        }
    }
}