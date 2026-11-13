using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using Tereditor.Host.Core.Language;

namespace Tereditor.Host.Core;

/// <summary>One row of the process table without its memory: <see cref="Group"/> is the session (Linux) or controlling terminal (macOS) it belongs to, 0 for none.</summary>
internal readonly record struct ProcessEntry(int Pid, int Ppid, long Group, string Name, long Started);

/// <summary>
/// The guard's real sampler and killer. They read the process table with system calls and /proc, never by launching <c>ps</c>: macOS libproc
/// (<c>proc_listallpids</c>, <c>proc_pidinfo</c>, <c>proc_pid_rusage</c>), Linux /proc, Windows a toolhelp snapshot and the process API.
/// </summary>
internal sealed class NativeGuardSampler : IGuardSampler, IGuardKiller
{
    private List<ProcessEntry> table = [];

    public long PhysicalBytes => LanguageMemory.PhysicalBytes;

    public bool UnderPressure() => LanguageMemory.UnderPressure();

    public void Refresh() => table = ProcessEntries.Read();

    public IReadOnlyList<GuardedProcess> Tree(int shellPid) =>
    [
        .. ProcessEntries.Members(table, shellPid, OperatingSystem.IsLinux()).Select(entry => (Entry: entry, Bytes: ProcessEntries.Bytes(entry.Pid)))
            .Where(item => item.Bytes is not null).Select(item => new GuardedProcess(item.Entry.Pid, item.Entry.Name, item.Bytes!.Value, item.Entry.Started,
                item.Entry.Ppid, TerminalMemoryGuard.AgentOf(item.Entry.Name, ProcessEntries.ExecutablePath(item.Entry.Pid))))
    ];

    public void Terminate(GuardedProcess process)
    {
        if (!Current(process)) return;
        if (!OperatingSystem.IsWindows()) { Posix.Kill(process.Pid, Posix.Sigterm); return; }
        // Windows has no SIGTERM for a console program: ask a windowed one to close, and the forced end follows after the grace.
        try { using var found = Process.GetProcessById(process.Pid); found.CloseMainWindow(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
    }

    public void Kill(GuardedProcess process)
    {
        if (!Current(process)) return;
        if (!OperatingSystem.IsWindows()) { Posix.Kill(process.Pid, Posix.Sigkill); return; }
        try { using var found = Process.GetProcessById(process.Pid); found.Kill(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
    }

    // A pid may have been reused since the sample: end it only if it is still the process that was sampled.
    private static bool Current(GuardedProcess process) =>
        ProcessEntries.Read().Any(entry => entry.Pid == process.Pid && entry.Started == process.Started && entry.Name == process.Name);
}

internal static class ProcessEntries
{
    private const string Libproc = "/usr/lib/libproc.dylib";

    /// <summary>
    /// The processes of one terminal: everything below its shell by parent links, plus every process that kept the shell's session or
    /// controlling terminal (a build server that was reparented to init when its parent exited). The shell is excluded.
    /// </summary>
    internal static List<ProcessEntry> Members(IReadOnlyList<ProcessEntry> table, int shell, bool sessionIds = false)
    {
        var children = table.ToLookup(entry => entry.Ppid);
        var found = new HashSet<int>();
        var queue = new Queue<int>([shell]);
        while (queue.Count > 0)
            foreach (var child in children[queue.Dequeue()])
                if (child.Pid != shell && found.Add(child.Pid)) queue.Enqueue(child.Pid);
        // Only a shell that leads its own session (session ids) or owns a terminal (terminal devices) makes the group a terminal's;
        // any other would pull in the host's own processes.
        var leader = table.FirstOrDefault(entry => entry.Pid == shell);
        if (leader.Pid == shell && leader.Group != 0 && (!sessionIds || leader.Group == shell))
            foreach (var entry in table)
                if (entry.Group == leader.Group && entry.Pid != shell) found.Add(entry.Pid);
        return [.. table.Where(entry => found.Contains(entry.Pid))];
    }

    internal static List<ProcessEntry> Read()
    {
        try
        {
            if (OperatingSystem.IsMacOS()) return ReadMac();
            if (OperatingSystem.IsLinux()) return ReadLinux();
            if (OperatingSystem.IsWindows()) return ReadWindows();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { }
        return [];
    }

    /// <summary>The memory a process costs (macOS physical footprint, elsewhere resident set), or null when it is gone.</summary>
    internal static long? Bytes(int pid)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) return LanguageMemory.Footprint(pid);
            if (OperatingSystem.IsLinux())
            {
                var fields = File.ReadAllText($"/proc/{pid}/statm").Split(' ');
                return long.Parse(fields[1], CultureInfo.InvariantCulture) * Environment.SystemPageSize;
            }
            using var process = Process.GetProcessById(pid);
            return process.WorkingSet64;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Win32Exception or FormatException or IndexOutOfRangeException) { return null; }
    }

    /// <summary>A process's executable (macOS <c>proc_pidpath</c>, Linux /proc exe link), or null where it cannot be read.</summary>
    internal static string? ExecutablePath(int pid)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var buffer = new byte[4096];
                var length = ProcessPath(pid, buffer, (uint)buffer.Length);
                return length > 0 ? Encoding.UTF8.GetString(buffer, 0, length) : null;
            }
            if (OperatingSystem.IsLinux()) return new FileInfo($"/proc/{pid}/exe").LinkTarget;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { }
        return null;
    }

    // proc_bsdinfo: pid at 12, ppid at 16, comm[16] at 48, name[32] at 64, e_tdev at 108, start seconds at 120; 136 bytes.
    private const int BsdInfo = 3, BsdInfoSize = 136, NoDevice = -1;

    private static List<ProcessEntry> ReadMac()
    {
        var pids = new int[32768];
        var count = ListAllPids(pids, pids.Length * sizeof(int));
        var rows = new List<ProcessEntry>(Math.Max(count, 0));
        var info = new byte[BsdInfoSize];
        for (var i = 0; i < count; i++)
        {
            if (pids[i] <= 0 || ProcessInfo(pids[i], BsdInfo, 0, info, info.Length) != info.Length) continue;
            rows.Add(MacEntry(pids[i], info));
        }
        return rows;
    }

    /// <summary>Reads a <c>proc_bsdinfo</c> record.</summary>
    internal static ProcessEntry MacEntry(int pid, byte[] info)
    {
        var device = BitConverter.ToInt32(info, 108);
        var name = CString(info, 64, 32);
        if (name.Length == 0) name = CString(info, 48, 16);
        return new(pid, BitConverter.ToInt32(info, 16), device == NoDevice ? 0 : device, name, BitConverter.ToInt64(info, 120));
    }

    private static string CString(byte[] buffer, int offset, int length)
    {
        var end = Array.IndexOf(buffer, (byte)0, offset, length);
        return Encoding.UTF8.GetString(buffer, offset, (end < 0 ? offset + length : end) - offset);
    }

    private static List<ProcessEntry> ReadLinux()
    {
        var rows = new List<ProcessEntry>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), CultureInfo.InvariantCulture, out var pid)) continue;
            try { if (ParseLinuxStat(pid, File.ReadAllText(directory + "/stat")) is { } entry) rows.Add(entry); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return rows;
    }

    /// <summary>Reads a <c>/proc/&lt;pid&gt;/stat</c> line: the name sits in parentheses and may itself contain spaces and parentheses.</summary>
    internal static ProcessEntry? ParseLinuxStat(int pid, string stat)
    {
        var open = stat.IndexOf('(');
        var close = stat.LastIndexOf(')');
        if (open < 0 || close < open) return null;
        // After the name: state, ppid, pgrp, session, tty_nr, tpgid, flags, minflt, cminflt, majflt, cmajflt, utime, stime, cutime, cstime, priority, nice, threads, itrealvalue, starttime.
        var rest = stat[(close + 2)..].Split(' ');
        if (rest.Length < 20 || !int.TryParse(rest[1], CultureInfo.InvariantCulture, out var ppid) || !long.TryParse(rest[3], CultureInfo.InvariantCulture, out var session) ||
            !long.TryParse(rest[19], CultureInfo.InvariantCulture, out var started)) return null;
        // A session id is the pid of its leader; only a shell that leads its own session claims it.
        return new(pid, ppid, session, stat[(open + 1)..close], started);
    }

    private static List<ProcessEntry> ReadWindows()
    {
        var rows = new List<ProcessEntry>();
        foreach (var (pid, parent) in WindowsProcesses.Parents())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                rows.Add(new(pid, parent, 0, process.ProcessName, process.StartTime.Ticks));
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { }
        }
        return rows;
    }

    [DllImport(Libproc, EntryPoint = "proc_listallpids")]
    private static extern int ListAllPids([Out] int[] buffer, int size);

    [DllImport(Libproc, EntryPoint = "proc_pidpath")]
    private static extern int ProcessPath(int pid, [Out] byte[] buffer, uint size);

    [DllImport(Libproc, EntryPoint = "proc_pidinfo")]
    private static extern int ProcessInfo(int pid, int flavor, ulong argument, [Out] byte[] buffer, int size);
}