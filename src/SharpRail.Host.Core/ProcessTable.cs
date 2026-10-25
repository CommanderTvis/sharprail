using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SharpRail.Host.Core;

/// <summary>One live (non-zombie) process of the machine's process table. Elapsed and Tty are empty on Windows.</summary>
internal sealed record ProcessRow(int Pid, int Ppid, TimeSpan? Elapsed, string Tty, string Arguments);

/// <summary>
/// A snapshot of the machine's processes (<c>ps</c>, or CIM on Windows) and the signalling built on it: the descendants of
/// a process, and ending a set of processes by escalating signals. Every member runs processes or waits: call it off the UI thread.
/// </summary>
internal static partial class ProcessTable
{
    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(.*\S)\s*$")]
    private static partial Regex PosixRow();

    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s+(.*\S)\s*$")]
    private static partial Regex WindowsRow();

    /// <summary>Every live process; empty when the table cannot be read.</summary>
    internal static List<ProcessRow> Read()
    {
        var windows = OperatingSystem.IsWindows();
        var output = windows
            ? Run("powershell.exe", "-NoProfile", "-Command",
                "$ErrorActionPreference = 'Stop'; Get-CimInstance Win32_Process | ForEach-Object { \"$($_.ProcessId) $($_.ParentProcessId) $($_.CommandLine)\" }")
            : Run("ps", "-Aww", "-o", "pid=,ppid=,stat=,etime=,tty=,args=");
        var rows = new List<ProcessRow>();
        foreach (var line in (output ?? "").Split('\n'))
        {
            if (windows)
            {
                if (WindowsRow().Match(line) is { Success: true } w && int.TryParse(w.Groups[1].Value, CultureInfo.InvariantCulture, out var wp) &&
                    int.TryParse(w.Groups[2].Value, CultureInfo.InvariantCulture, out var wpp))
                    rows.Add(new(wp, wpp, null, "", w.Groups[3].Value));
                continue;
            }
            var match = PosixRow().Match(line);
            if (!match.Success || match.Groups[3].Value.StartsWith('Z')) continue;
            if (int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var pid) && int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var ppid))
                rows.Add(new(pid, ppid, Elapsed(match.Groups[4].Value), match.Groups[5].Value, match.Groups[6].Value));
        }
        return rows;
    }

    /// <summary>Parses ps's <c>[[dd-]hh:]mm:ss</c> elapsed time.</summary>
    internal static TimeSpan? Elapsed(string text)
    {
        var days = 0;
        var dash = text.IndexOf('-');
        if (dash > 0 && !int.TryParse(text[..dash], CultureInfo.InvariantCulture, out days)) return null;
        var parts = text[(dash + 1)..].Split(':');
        if (parts.Length is < 2 or > 3 || parts.Any(part => !int.TryParse(part, CultureInfo.InvariantCulture, out _))) return null;
        var numbers = parts.Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        return parts.Length == 3 ? new(days, numbers[0], numbers[1], numbers[2]) : new(days, 0, numbers[0], numbers[1]);
    }

    /// <summary>The process itself and everything below it by parent links (processes already reparented to init are not found).</summary>
    internal static HashSet<int> Subtree(IEnumerable<ProcessRow> rows, int root)
    {
        var children = rows.ToLookup(row => row.Ppid, row => row.Pid);
        var found = new HashSet<int> { root };
        var queue = new Queue<int>([root]);
        while (queue.Count > 0)
            foreach (var child in children[queue.Dequeue()])
                if (found.Add(child)) queue.Enqueue(child);
        return found;
    }

    /// <summary>The pids a terminal's shell leaves to end: its descendants, plus members of the terminal's foreground process group.</summary>
    internal static Dictionary<int, string> Capture(int shell, int foregroundGroup)
    {
        var rows = Read();
        var found = Subtree(rows, shell);
        if (foregroundGroup > 1) found.UnionWith(Subtree(rows, foregroundGroup));
        found.Remove(Environment.ProcessId);
        return rows.Where(row => found.Contains(row.Pid)).ToDictionary(row => row.Pid, row => row.Arguments);
    }

    /// <summary>The captured processes that still run (same pid and command line, so a reused pid is not mistaken for one).</summary>
    internal static List<int> Alive(IReadOnlyDictionary<int, string> captured) =>
        [.. Read().Where(row => captured.TryGetValue(row.Pid, out var arguments) && arguments == row.Arguments).Select(row => row.Pid)];

    /// <summary>Sends a signal to each pid; a pid already gone is ignored.</summary>
    internal static void Signal(IEnumerable<int> pids, int signal)
    {
        foreach (var pid in pids)
            if (pid != Environment.ProcessId) Posix.Kill(pid, signal);
    }

    /// <summary>
    /// Ends the captured processes: each signal in <paramref name="signals"/> in turn, waiting up to <paramref name="grace"/>
    /// for them to go before the next, then SIGKILL; true when none is left. Windows ends the trees with taskkill instead.
    /// </summary>
    internal static async Task<bool> EndAsync(IReadOnlyDictionary<int, string> captured, TimeSpan grace, params int[] signals)
    {
        if (captured.Count == 0) return true;
        if (OperatingSystem.IsWindows())
        {
            foreach (var pid in captured.Keys) Run("taskkill", "/PID", pid.ToString(CultureInfo.InvariantCulture), "/T", "/F");
            return Alive(captured).Count == 0;
        }
        foreach (var signal in signals.Append(Posix.Sigkill))
        {
            var alive = Alive(captured);
            if (alive.Count == 0) return true;
            Signal(alive, signal);
            var deadline = Stopwatch.GetTimestamp() + (long)(grace.TotalSeconds * Stopwatch.Frequency);
            var last = signal == Posix.Sigkill;
            do
            {
                await Task.Delay(last ? 100 : 50);
                if ((alive = Alive(captured)).Count == 0) return true;
            } while (Stopwatch.GetTimestamp() < deadline);
        }
        return false;
    }

    private static string? Run(string program, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
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
}