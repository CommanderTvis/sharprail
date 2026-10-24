using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using SharpRail.Plugins.Api;

namespace SharpRail.Host.Core.Plugins;

/// <summary>
/// Who has an agent session open on this machine, so one session is never resumed by two processes. Two signals count:
/// a live process whose command line names the session id (a Claude, Codex or omp started by any SharpRail, or by hand),
/// and a lock file per session in a machine-wide directory every SharpRail process of the user shares, whatever its
/// state directory (<c>SHARPRAIL_STATE_DIR</c> separates profiles and dev builds, which still share the agents' homes).
/// A lock names the host process that holds it (pid and start time); one whose process is gone is stale and ignored.
/// Every member does file or process I/O: call it off the UI thread.
/// </summary>
public sealed partial class AgentSessionOwnership(string? directory = null)
{
    /// <summary>Overrides the lock directory, for checks and for users whose local data directory is unsuitable.</summary>
    public const string DirectoryVariable = "SHARPRAIL_AGENT_SESSIONS_DIR";

    private readonly Lock gate = new();
    private readonly Dictionary<TerminalRef, string> held = [];

    /// <summary>The lock directory: the override, else <c>SharpRail/agent-sessions</c> under the user's local application data.</summary>
    public string Directory { get; } = directory ?? (Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } configured ? configured
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "SharpRail", "agent-sessions"));

    /// <summary>The lock file for one agent's session.</summary>
    public string PathFor(string agent, string sessionId) => Path.Combine(Directory, $"{Safe(agent)}-{Safe(sessionId)}.lock");

    /// <summary>
    /// Why the session may not be resumed here, or null when nobody else has it open: a live lock held by another
    /// process, or a running process that names the id, outside the process trees of <paramref name="ownShells"/>.
    /// </summary>
    public string? OwnedElsewhere(string agent, string sessionId, IReadOnlyCollection<int> ownShells)
    {
        if (sessionId.Length == 0) return null;
        if (Holder(PathFor(agent, sessionId)) is { } holder && holder != Environment.ProcessId) return $"another SharpRail process ({holder}) holds it";
        return Running(sessionId, ownShells) is { } pid ? $"process {pid} has it open" : null;
    }

    /// <summary>
    /// Records that this host's <paramref name="terminal"/> runs the session, taking its lock unless another live process
    /// holds it; a lock this terminal held for another session is released. Returns whether the lock is now this process's.
    /// </summary>
    public bool Hold(TerminalRef terminal, string agent, string sessionId)
    {
        if (sessionId.Length == 0) { Release(terminal); return false; }
        var path = PathFor(agent, sessionId);
        string? stale = null;
        lock (gate)
        {
            if (held.GetValueOrDefault(terminal) == path) return true;
            if (held.Remove(terminal, out var previous) && !held.ContainsValue(previous)) stale = previous;
        }
        if (stale is not null && Holder(stale) == Environment.ProcessId) Delete(stale);
        lock (gate)
        {
            if (held.ContainsValue(path) && held.GetValueOrDefault(terminal) != path) return true;
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    stream.Write(Encoding.UTF8.GetBytes(Content(Environment.ProcessId, StartTicks(Process.GetCurrentProcess()), terminal)));
                lock (gate) held[terminal] = path;
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                var holder = Holder(path);
                if (holder == Environment.ProcessId) { lock (gate) held[terminal] = path; return true; }
                if (holder is not null) return false;
                Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
        return false;
    }

    /// <summary>Releases the lock a terminal of this host holds, if any.</summary>
    public void Release(TerminalRef terminal)
    {
        string? path;
        lock (gate) if (held.Remove(terminal, out path) && held.ContainsValue(path)) path = null;
        if (path is not null && Holder(path) == Environment.ProcessId) Delete(path);
    }

    /// <summary>Releases every lock this host holds.</summary>
    public void ReleaseAll()
    {
        string[] paths;
        lock (gate) { paths = [.. held.Values.Distinct()]; held.Clear(); }
        foreach (var path in paths) if (Holder(path) == Environment.ProcessId) Delete(path);
    }

    /// <summary>The lock file text: the holder's pid, its start time in UTC ticks, and the terminal, one per line.</summary>
    public static string Content(int pid, long startTicks, TerminalRef terminal) =>
        string.Create(CultureInfo.InvariantCulture, $"{pid}\n{startTicks}\n{terminal.WorkspaceId}\n{terminal.TabKey}\n");

    /// <summary>The live process holding a lock, or null when there is none or it is stale (unreadable, or its process is gone or reused).</summary>
    public static int? Holder(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        if (lines.Length < 2 || !int.TryParse(lines[0], CultureInfo.InvariantCulture, out var pid) || !long.TryParse(lines[1], CultureInfo.InvariantCulture, out var ticks)) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && (ticks == 0 || StartTicks(process) is var started && (started == 0 || started == ticks)) ? pid : null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>A process's start time in UTC ticks, or 0 when the platform does not tell.</summary>
    public static long StartTicks(Process process)
    {
        try { return process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return 0; }
    }

    // A live (non-zombie) process naming the session in its arguments, outside the given shells' trees.
    private static int? Running(string sessionId, IReadOnlyCollection<int> ownShells)
    {
        var rows = Rows();
        if (rows.Count == 0) return null;
        var parents = rows.ToDictionary(row => row.Pid, row => row.Ppid);
        foreach (var row in rows)
        {
            if (row.Pid == Environment.ProcessId || !Names(row.Arguments, sessionId)) continue;
            var inside = false;
            for (int? pid = row.Pid, depth = 0; pid is { } current && depth < 64 && !inside; pid = parents.TryGetValue(current, out var parent) && parent != current ? parent : null, depth++)
                inside = ownShells.Contains(current);
            if (!inside) return row.Pid;
        }
        return null;
    }

    /// <summary>Whether a command line names the session: as a whole argument, or as a path segment or file-name stem (omp's session file).</summary>
    public static bool Names(string arguments, string sessionId) =>
        arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(argument =>
            argument == sessionId || argument.EndsWith("=" + sessionId, StringComparison.Ordinal) ||
            argument.Split('/', '\\').Any(segment => segment == sessionId || segment.StartsWith(sessionId + ".", StringComparison.Ordinal)));

    private sealed record Row(int Pid, int Ppid, string Arguments);

    // ps rows with their state: a zombie (Z) has exited and keeps its arguments only until its parent reaps it.
    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s+(\S+)\s+(.*\S)\s*$")]
    private static partial Regex StatedRow();

    [GeneratedRegex(@"^\s*(\d+)\s+(\d+)\s+(.*\S)\s*$")]
    private static partial Regex WindowsRow();

    private static List<Row> Rows()
    {
        var windows = OperatingSystem.IsWindows();
        var output = windows
            ? Run("powershell.exe", "-NoProfile", "-Command",
                "$ErrorActionPreference = 'Stop'; Get-CimInstance Win32_Process | ForEach-Object { \"$($_.ProcessId) $($_.ParentProcessId) $($_.CommandLine)\" }")
            : Run("ps", "-Aww", "-o", "pid=,ppid=,stat=,args=");
        var rows = new List<Row>();
        foreach (var line in (output ?? "").Split('\n'))
        {
            var match = windows ? WindowsRow().Match(line) : StatedRow().Match(line);
            if (!match.Success || !windows && match.Groups[3].Value.StartsWith('Z')) continue;
            if (int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var pid) && int.TryParse(match.Groups[2].Value, CultureInfo.InvariantCulture, out var ppid))
                rows.Add(new(pid, ppid, match.Groups[windows ? 3 : 4].Value));
        }
        return rows;
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

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string Safe(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value) builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return builder.Length is 0 or > 128 ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value))) : builder.ToString();
    }
}