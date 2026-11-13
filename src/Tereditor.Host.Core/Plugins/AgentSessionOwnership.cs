using System.Diagnostics;
using System.Globalization;
using System.Text;

using Tereditor.Plugins.Api;

namespace Tereditor.Host.Core.Plugins;

/// <summary>
/// Who has an agent session open on this machine, so one session is never resumed by two processes. Two signals count:
/// a live process whose command line names the session id (a Claude, Codex or omp started by any tereditor, or by hand),
/// and a lock file per session in a machine-wide directory every tereditor process of the user shares, whatever its
/// state directory (<c>TEREDITOR_STATE_DIR</c> separates profiles and dev builds, which still share the agents' homes).
/// A lock names the host process that holds it (pid and start time); one whose process is gone is stale and ignored.
/// Every member does file or process I/O: call it off the UI thread.
/// </summary>
public sealed partial class AgentSessionOwnership(string? directory = null)
{
    /// <summary>Overrides the lock directory, for checks and for users whose local data directory is unsuitable.</summary>
    public const string DirectoryVariable = "TEREDITOR_AGENT_SESSIONS_DIR";

    private readonly Lock gate = new();
    private readonly Dictionary<TerminalRef, string> held = [];

    /// <summary>The lock directory: the override, else <c>tereditor/agent-sessions</c> under the user's local application data.</summary>
    public string Directory { get; } = directory ?? (Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 } configured ? configured
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "tereditor", "agent-sessions"));

    /// <summary>The lock file for one agent's session.</summary>
    public string PathFor(string agent, string sessionId) => Path.Combine(Directory, $"{Safe(agent)}-{Safe(sessionId)}.lock");

    /// <summary>
    /// Who else has the session open, or null when nobody does: a live lock held by another process, or a running process
    /// that names the id, outside the process trees of <paramref name="ownShells"/>. <paramref name="recorded"/> is the agent
    /// process the asking tab's record says ran the session (pid and start time in UTC ticks, from an earlier run): while
    /// that same process still runs it counts as the session's, and it is tereditor's own (a stray) unless a live tereditor
    /// is above it. A pid whose start time differs (reused) or is unknown is not that process.
    /// </summary>
    public SessionOwner? Inspect(string agent, string sessionId, IReadOnlyCollection<int> ownShells, (int Pid, long Started)? recorded = null)
    {
        if (sessionId.Length == 0) return null;
        var path = PathFor(agent, sessionId);
        var rows = Table();
        var byPid = rows.ToDictionary(row => row.Pid);
        if (Holder(path) is { } holder && holder != Environment.ProcessId)
        {
            byPid.TryGetValue(holder, out var held);
            return new(SessionOwnerKind.Instance, holder, Started(held), held?.Tty ?? "", Command(held), LockTab(path), false);
        }
        var candidates = Running(rows, sessionId, ownShells);
        var ours = recorded is { Pid: > 1 and var recordedPid, Started: not 0 } && recordedPid != Environment.ProcessId && byPid.TryGetValue(recordedPid, out var recordedRow) &&
            !Inside(rows, recordedPid, ownShells) && StartOf(recordedPid) == recorded.Value.Started ? recordedRow : null;
        if (ours is not null && !candidates.Contains(ours)) candidates.Insert(0, ours);
        if (candidates.Count == 0) return null;
        var parents = rows.ToDictionary(row => row.Pid, row => row.Ppid);
        var instances = LiveHolders();
        instances.Add(Environment.ProcessId);
        var protectedPids = new HashSet<int>(Ancestry(parents, Environment.ProcessId));
        foreach (var shell in ownShells) protectedPids.UnionWith(Ancestry(parents, shell));
        // Every process that has the session open is weighed and the one most tereditor's own is named: a live
        // instance's, then this tab's earlier agent, then one left behind, then a foreign one.
        SessionOwner? best = null;
        foreach (var running in candidates)
        {
            var owner = Classify(running, running == ours);
            if (best is null || Rank(owner) < Rank(best)) best = owner;
        }
        return best;

        SessionOwner Classify(ProcessRow running, bool isRecorded)
        {
            var chain = Ancestry(parents, running.Pid);
            var viaInstance = chain.FirstOrDefault(pid => instances.Contains(pid) || byPid.TryGetValue(pid, out var row) && IsTereditor(row.Arguments));
            if (viaInstance != 0) return new(SessionOwnerKind.Instance, running.Pid, Started(running), running.Tty, Command(running), LockTab(path), false);
            // No live tereditor above it: tereditor's own if it is the agent this tab recorded, or left behind when its
            // instance stopped if a dead holder's lock remains, or the topmost ancestor is a shell that was orphaned to init
            // with a terminal.
            var top = byPid.GetValueOrDefault(chain[^1]);
            var orphanedShell = top is not null && top.Ppid <= 1 && top.Tty is not ("" or "?" or "??") && IsShell(top.Arguments);
            var leftBehind = isRecorded || File.Exists(path) || orphanedShell;
            var tree = ProcessTable.Subtree(rows, running.Pid);
            var quittable = leftBehind && !tree.Overlaps(protectedPids);
            return new(leftBehind ? SessionOwnerKind.Stray : SessionOwnerKind.Foreign, running.Pid, Started(running), running.Tty, Command(running), "", quittable) { Recorded = isRecorded };
        }

        static int Rank(SessionOwner owner) => owner.Kind switch
        {
            SessionOwnerKind.Instance => 0,
            SessionOwnerKind.Stray => owner.Recorded ? 1 : 2,
            _ => 3
        };
    }

    /// <summary>
    /// Ends a stray owner found by <see cref="Inspect"/>: its process tree gets SIGTERM, then SIGHUP, then SIGKILL, each after
    /// <see cref="QuitGrace"/>, and the stale lock is dropped once nothing is left. Re-inspects first, so a process that is
    /// no longer a stray owner of that session (it exited, its pid was reused) is never signalled. Returns null on success, else why not.
    /// </summary>
    public async Task<string?> QuitAsync(string agent, string sessionId, int pid, IReadOnlyCollection<int> ownShells, (int Pid, long Started)? recorded = null)
    {
        var owner = await Task.Run(() => Inspect(agent, sessionId, ownShells, recorded));
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

    /// <summary>The process table <see cref="Inspect"/> reads; checks replace it with a fake one.</summary>
    internal Func<List<ProcessRow>> Table { get; init; } = ProcessTable.Read;

    /// <summary>A live process's start time in UTC ticks, 0 when it is gone or unknown; checks replace it with a fake one.</summary>
    internal Func<int, long> StartOf { get; init; } = StartTicks;

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
                if (Running(rows, id, []) is [var row, ..]) found.Add((file, row.Pid));
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

    /// <summary>The start time of the live process <paramref name="pid"/> in UTC ticks, or 0 when it is gone or the platform does not tell.</summary>
    public static long StartTicks(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? 0 : StartTicks(process);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
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

    // A tereditor host or UI process: its executable (or the dll it runs under dotnet) is named tereditor*.
    private static bool IsTereditor(string arguments) =>
        arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(2).Any(word => Path.GetFileName(word).StartsWith("tereditor", StringComparison.OrdinalIgnoreCase));

    private static bool IsShell(string arguments) =>
        Path.GetFileName(arguments.Split((char[]?)null, 2)[0]).TrimStart('-') is "sh" or "bash" or "zsh" or "fish" or "dash" or "ksh" or "nu" or "pwsh";

    // The live (non-zombie) processes naming the session in their arguments, outside the given shells' trees.
    private static List<ProcessRow> Running(List<ProcessRow> rows, string sessionId, IReadOnlyCollection<int> ownShells) =>
        [.. rows.Where(row => row.Pid != Environment.ProcessId && Names(row.Arguments, sessionId) && !Inside(rows, row.Pid, ownShells))];

    // Whether the process or one of its ancestors is one of the given shells.
    private static bool Inside(List<ProcessRow> rows, int pid, IReadOnlyCollection<int> shells)
    {
        if (shells.Count == 0) return false;
        var parents = rows.ToDictionary(row => row.Pid, row => row.Ppid);
        for (int? current = pid, depth = 0; current is { } at && depth < 64; current = parents.TryGetValue(at, out var parent) && parent != at ? parent : null, depth++)
            if (shells.Contains(at)) return true;
        return false;
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

/// <summary>How the process holding an agent session relates to tereditor.</summary>
public enum SessionOwnerKind
{
    /// <summary>A live tereditor instance holds the session (its lock, or an ancestor of the process).</summary>
    Instance,
    /// <summary>Left running by a tereditor instance that is no longer running.</summary>
    Stray,
    /// <summary>A process with no tereditor link, such as an agent started by hand.</summary>
    Foreign
}

/// <summary>The process that has an agent session open; <paramref name="CanQuit"/> only for a stray that is safe to end.</summary>
public sealed record SessionOwner(SessionOwnerKind Kind, int Pid, string Started, string Tty, string Command, string Tab, bool CanQuit)
{
    /// <summary>Whether this is the agent process the asking tab's record names (same pid and start time), not just one that names the session.</summary>
    public bool Recorded { get; init; }

    /// <summary>The refusal shown in the resume offer, after the "Claude session xxxx" lead.</summary>
    public string Describe()
    {
        var detail = string.Join(", ", new[] { Started.Length > 0 ? "started " + Started + " ago" : "", Tty is "" or "?" or "??" ? "" : Tty }.Where(part => part.Length > 0));
        var process = $"pid {Pid}" + (detail.Length > 0 ? $" ({detail})" : "");
        return Kind switch
        {
            SessionOwnerKind.Instance => $"is open in another tereditor window/instance (pid {Pid})" + (Tab.Length > 0 ? $", tab {Tab}" : "") + ".",
            SessionOwnerKind.Stray => $"is still running in an earlier tereditor terminal, as {process}. The tereditor run that started it has ended.",
            _ => $"is open in {process}, which tereditor did not start."
        };
    }

    /// <summary>The confirmation text for ending the process; names its pid, command and start time.</summary>
    public string QuitConfirmation() =>
        $"End pid {Pid}{(Started.Length > 0 ? $" (started {Started} ago)" : "")} and everything it started?\n\n{Command}\n\nIt gets SIGTERM, then SIGHUP, then SIGKILL until it is gone, and the session resumes in this tab.";
}