using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SharpRail.Plugins.ClaudeCode.Host;

internal interface IProcessSnapshot
{
    IReadOnlyList<int> ChildrenOf(int pid);
    int? ParentOf(int pid);
    string? NameOf(int pid);
}

internal sealed record ProcessRow(int Pid, int Ppid, string Name);

/// <summary>The machine's process table, read with <c>ps</c> (PowerShell on Windows), and the walks the agent detection needs.</summary>
internal static partial class ProcessTree
{
    public const int MaxDescendantDepth = 4;

    public static string WindowsCommandQuery(int pid) =>
        $"$ErrorActionPreference = 'Stop'; (Get-CimInstance Win32_Process -Filter \"ProcessId = {pid}\").CommandLine";

    public const string WindowsProcessList =
        "$ErrorActionPreference = 'Stop'; Get-CimInstance Win32_Process | ForEach-Object { \"$($_.ProcessId) $($_.ParentProcessId) $($_.Name)\" }";

    private static string BaseName(string command)
    {
        var cut = command.LastIndexOf('/');
        var name = cut == -1 ? command : command[(cut + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s+(.*\S)\s*$")]
    private static partial Regex Row();

    public static IReadOnlyList<ProcessRow> ParseRows(string output)
    {
        var rows = new List<ProcessRow>();
        foreach (var line in output.Split('\n'))
            if (Row().Match(line) is { Success: true } match && int.TryParse(match.Groups[1].Value, out var pid) && int.TryParse(match.Groups[2].Value, out var ppid))
                rows.Add(new(pid, ppid, BaseName(match.Groups[3].Value)));
        return rows;
    }

    public static IProcessSnapshot FromRows(IReadOnlyList<ProcessRow> rows) => new Snapshot(rows);

    private sealed class Snapshot : IProcessSnapshot
    {
        private readonly Dictionary<int, List<int>> children = [];
        private readonly Dictionary<int, int> parents = [];
        private readonly Dictionary<int, string> names = [];

        public Snapshot(IReadOnlyList<ProcessRow> rows)
        {
            foreach (var row in rows)
            {
                names[row.Pid] = row.Name;
                parents[row.Pid] = row.Ppid;
                if (children.TryGetValue(row.Ppid, out var siblings)) siblings.Add(row.Pid);
                else children[row.Ppid] = [row.Pid];
            }
        }

        public IReadOnlyList<int> ChildrenOf(int pid) => children.GetValueOrDefault(pid) ?? [];
        public int? ParentOf(int pid) => parents.TryGetValue(pid, out var parent) ? parent : null;
        public string? NameOf(int pid) => names.GetValueOrDefault(pid);
    }

    private static string? Run(string program, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000)) { process.Kill(true); return null; }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    public static IProcessSnapshot? Capture()
    {
        var output = OperatingSystem.IsWindows()
            ? Run("powershell.exe", "-NoProfile", "-Command", WindowsProcessList)
            : Run("ps", "-Ao", "pid=,ppid=,comm=");
        return output is null ? null : FromRows(ParseRows(output));
    }

    /// <summary>The first process named in <paramref name="wanted"/> below <paramref name="rootPid"/>, breadth first.</summary>
    public static (int Pid, string Name)? FindDescendant(IProcessSnapshot snapshot, int rootPid, IReadOnlyCollection<string> wanted, int maxDepth = MaxDescendantDepth)
    {
        var seen = new HashSet<int> { rootPid };
        var frontier = new List<int> { rootPid };
        for (var depth = 0; depth < maxDepth && frontier.Count > 0; depth++)
        {
            var next = new List<int>();
            foreach (var pid in frontier)
                foreach (var child in snapshot.ChildrenOf(pid))
                {
                    if (!seen.Add(child)) continue;
                    if (snapshot.NameOf(child) is { } name && wanted.Contains(name)) return (child, name);
                    next.Add(child);
                }
            frontier = next;
        }
        return null;
    }

    /// <summary>Whether this tab's <paramref name="self"/> agent was started by its <paramref name="outer"/> one, the tab's own agent.</summary>
    public static bool RunsInsideAgent(IProcessSnapshot snapshot, int rootPid, string self, string outer)
    {
        var found = FindDescendant(snapshot, rootPid, [self]);
        var pid = found is { } process ? snapshot.ParentOf(process.Pid) : null;
        for (var depth = 0; pid is { } current && current != rootPid && depth < MaxDescendantDepth; depth++)
        {
            if (snapshot.NameOf(current) == outer) return true;
            pid = snapshot.ParentOf(current);
        }
        return false;
    }

    /// <summary>
    /// The full command line of one process, read on demand for a process already decided to matter rather than widening
    /// the poll: <c>args=</c> is unbounded and would be carried for every process on the machine, every tick.
    /// </summary>
    public static string? CaptureCommand(int pid)
    {
        if (pid <= 0) return null;
        var output = OperatingSystem.IsWindows()
            ? Run("powershell.exe", "-NoProfile", "-Command", WindowsCommandQuery(pid))
            : Run("ps", "-o", "args=", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var line = output?.Split('\n')[0].Trim() ?? "";
        return line.Length == 0 ? null : line;
    }

    /// <summary>Where a process is now: <c>/proc/&lt;pid&gt;/cwd</c> on Linux, <c>lsof</c> elsewhere; nothing on Windows.</summary>
    public static string? CaptureCwd(int pid)
    {
        if (pid <= 0 || OperatingSystem.IsWindows()) return null;
        try
        {
            if (OperatingSystem.IsLinux()) return new FileInfo($"/proc/{pid}/cwd").LinkTarget;
            var output = Run("lsof", "-a", "-d", "cwd", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "-Fn");
            return output?.Split('\n').FirstOrDefault(entry => entry.StartsWith('n'))?[1..];
        }
        catch (IOException) { return null; }
    }
}