using System.Diagnostics;
using System.Globalization;
using System.Text;

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
    /// Who else has the session open, or null when nobody does: a live lock held by another process, or a running process
    /// that names the id, outside the process trees of <paramref name="ownShells"/>.
    /// </summary>
    public SessionOwner? Inspect(string agent, string sessionId, IReadOnlyCollection<int> ownShells)
    {
        if (sessionId.Length == 0) return null;
        var path = PathFor(agent, sessionId);
        var rows = ProcessTable.Read();
        var byPid = rows.ToDictionary(row => row.Pid);
        if (Holder(path) is { } holder && holder != Environment.ProcessId)
        {
            byPid.TryGetValue(holder, out var held);
            return new(SessionOwnerKind.Instance, holder, Started(held), held?.Tty ?? "", Command(held), LockTab(path), false);
        }
        var running = Running(rows, sessionId, ownShells);
        if (running is null) return null;
        var parents = rows.ToDictionary(row => row.Pid, row => row.Ppid);
        var chain = Ancestry(parents, running.Pid);
        var instances = LiveHolders();
        instances.Add(Environment.ProcessId);
        var viaInstance = chain.FirstOrDefault(pid => instances.Contains(pid) || byPid.TryGetValue(pid, out var row) && IsSharpRail(row.Arguments));
        if (viaInstance != 0) return new(SessionOwnerKind.Instance, running.Pid, Started(running), running.Tty, Command(running), LockTab(path), false);
        // No live SharpRail above it: left behind when its instance stopped if a dead holder's lock remains, or the topmost
        // ancestor is a shell that was orphaned to init with a terminal.
        var top = byPid.GetValueOrDefault(chain[^1]);
        var orphanedShell = top is not null && top.Ppid <= 1 && top.Tty is not ("" or "?" or "??") && IsShell(top.Arguments);
        var leftBehind = File.Exists(path) || orphanedShell;
        var protectedPids = new HashSet<int>(Ancestry(parents, Environment.ProcessId));
        foreach (var shell in ownShells) protectedPids.UnionWith(Ancestry(parents, shell));
        var tree = ProcessTable.Subtree(rows, running.Pid);
        var quittable = leftBehind && !tree.Overlaps(protectedPids);
        return new(leftBehind ? SessionOwnerKind.Stray : SessionOwnerKind.Foreign, running.Pid, Started(running), running.Tty, Command(running), "", quittable);
    }

    /// <summary>
    /// Ends a stray owner found by <see cref="Inspect"/>: its process tree gets SIGTERM, then SIGHUP, then SIGKILL, each after
    /// <see cref="QuitGrace"/>, and the stale lock is dropped once nothing is left. Re-inspects first, so a process that is
    /// no longer a stray owner of that session (it exited, its pid was reused) is never signalled. Returns null on success, else why not.
    /// </summary>
    public async Task<string?> QuitAsync(string agent, string sessionId, int pid, IReadOnlyCollection<int> ownShells)
    {
        var owner = await Task.Run(() => Inspect(agent, sessionId, ownShells));
        if (owner is null) return null;
        if (owner is not { Kind: SessionOwnerKind.Stray, CanQuit: true } || owner.Pid != pid) return $"process {pid} is no longer a stray owner of the session; refresh the offer.";
        var captured = await Task.Run(() =>
        {
            var rows = ProcessTable.Read();
            var tree = ProcessTable.Subtree(rows, pid);
            return rows.Where(row => tree.Contains(row.Pid)).ToDictionary(row => row.Pid, row => row.Arguments);
        });
        if (!await ProcessTable.EndAsync(captured, QuitGrace, Posix.Sigterm, Posix.Sighup))
            return $"process {pid} survived SIGKILL.";
        var path = PathFor(agent, sessionId);
        if (Holder(path) is null) Delete(path);
        return null;
    }

    /// <summary>How long each signal of <see cref="QuitAsync"/> waits before the next, three seconds unless a check shortens it.</summary>
    public TimeSpan QuitGrace { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Session ids that a lock file of a process that no longer runs still names, for the start-up sweep (log only).</summary>
    public IReadOnlyList<(string File, int Pid)> StrayCandidates()
    {
        var found = new List<(string, int)>();
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return found;
            var rows = ProcessTable.Read();
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.lock"))
            {
                if (Holder(file) is not null) continue;
                var id = Path.GetFileNameWithoutExtension(file);
                id = id[(id.IndexOf('-') + 1)..];
                if (Running(rows, id, []) is { } row) found.Add((file, row.Pid));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return found;
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

    private static string Started(ProcessRow? row) => row?.Elapsed is { } elapsed ? Ago(elapsed) : "";

    private static string Ago(TimeSpan span) => span.TotalDays >= 1 ? Plural((int)span.TotalDays, "day") : span.TotalHours >= 1 ? Plural((int)span.TotalHours, "hour")
        : span.TotalMinutes >= 1 ? Plural((int)span.TotalMinutes, "minute") : "moments";

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")}";

    private static string Command(ProcessRow? row) => row is null ? "" : row.Arguments.Length > 120 ? row.Arguments[..120] + "..." : row.Arguments;

    // The holder's tab as the lock file names it (workspace and tab key), or empty.
    private static string LockTab(string path)
    {
        try { var lines = File.ReadAllLines(path); return lines.Length >= 4 ? lines[3] : ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
    }

    // The pid, then each parent up to the top (the last entry is the topmost ancestor below init).
    private static List<int> Ancestry(Dictionary<int, int> parents, int pid)
    {
        var chain = new List<int> { pid };
        while (chain.Count < 64 && parents.TryGetValue(chain[^1], out var parent) && parent > 1 && parent != chain[^1] && !chain.Contains(parent)) chain.Add(parent);
        return chain;
    }

    private HashSet<int> LiveHolders()
    {
        var pids = new HashSet<int>();
        try
        {
            if (System.IO.Directory.Exists(Directory))
                foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.lock"))
                    if (Holder(file) is { } holder) pids.Add(holder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return pids;
    }

    // A SharpRail host or UI process: its executable (or the dll it runs under dotnet) is named SharpRail*.
    private static bool IsSharpRail(string arguments) =>
        arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(2).Any(word => Path.GetFileName(word).StartsWith("SharpRail", StringComparison.OrdinalIgnoreCase));

    private static bool IsShell(string arguments) =>
        Path.GetFileName(arguments.Split((char[]?)null, 2)[0]).TrimStart('-') is "sh" or "bash" or "zsh" or "fish" or "dash" or "ksh" or "nu" or "pwsh";

    // A live (non-zombie) process naming the session in its arguments, outside the given shells' trees.
    private static ProcessRow? Running(List<ProcessRow> rows, string sessionId, IReadOnlyCollection<int> ownShells)
    {
        if (rows.Count == 0) return null;
        var parents = rows.ToDictionary(row => row.Pid, row => row.Ppid);
        foreach (var row in rows)
        {
            if (row.Pid == Environment.ProcessId || !Names(row.Arguments, sessionId)) continue;
            var inside = false;
            for (int? pid = row.Pid, depth = 0; pid is { } current && depth < 64 && !inside; pid = parents.TryGetValue(current, out var parent) && parent != current ? parent : null, depth++)
                inside = ownShells.Contains(current);
            if (!inside) return row;
        }
        return null;
    }

    /// <summary>Whether a command line names the session: as a whole argument, or as a path segment or file-name stem (omp's session file).</summary>
    public static bool Names(string arguments, string sessionId) =>
        arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(argument =>
            argument == sessionId || argument.EndsWith("=" + sessionId, StringComparison.Ordinal) ||
            argument.Split('/', '\\').Any(segment => segment == sessionId || segment.StartsWith(sessionId + ".", StringComparison.Ordinal)));

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

/// <summary>How the process holding an agent session relates to SharpRail.</summary>
public enum SessionOwnerKind
{
    /// <summary>A live SharpRail instance holds the session (its lock, or an ancestor of the process).</summary>
    Instance,
    /// <summary>Left running by a SharpRail instance that is no longer running.</summary>
    Stray,
    /// <summary>A process with no SharpRail link, such as an agent started by hand.</summary>
    Foreign
}

/// <summary>The process that has an agent session open; <paramref name="CanQuit"/> only for a stray that is safe to end.</summary>
public sealed record SessionOwner(SessionOwnerKind Kind, int Pid, string Started, string Tty, string Command, string Tab, bool CanQuit)
{
    /// <summary>The refusal shown in the resume offer, after the "Claude session xxxx" lead.</summary>
    public string Describe()
    {
        var detail = string.Join(", ", new[] { Started.Length > 0 ? "started " + Started + " ago" : "", Tty is "" or "?" or "??" ? "" : Tty }.Where(part => part.Length > 0));
        var process = $"pid {Pid}" + (detail.Length > 0 ? $" ({detail})" : "");
        return Kind switch
        {
            SessionOwnerKind.Instance => $"is open in another SharpRail window/instance (pid {Pid})" + (Tab.Length > 0 ? $", tab {Tab}" : "") + ".",
            SessionOwnerKind.Stray => $"is open in {process}. The SharpRail instance that started it is no longer running.",
            _ => $"is open in {process}, which SharpRail did not start."
        };
    }

    /// <summary>The confirmation text for ending the process; names its pid, command and start time.</summary>
    public string QuitConfirmation() =>
        $"End pid {Pid}{(Started.Length > 0 ? $" (started {Started} ago)" : "")} and everything it started?\n\n{Command}\n\nIt gets SIGTERM, then SIGHUP, then SIGKILL until it is gone, and the session resumes in this tab.";
}