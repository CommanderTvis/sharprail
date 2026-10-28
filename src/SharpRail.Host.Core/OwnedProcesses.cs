using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;

namespace SharpRail.Host.Core;

/// <summary>
/// The long-running processes agents reported as their own (<c>owned_process_report</c>), keyed by the reporting terminal,
/// and the rules for stopping them (Terminals SPEC, Owned processes). Only reported processes are ever stopped. A record is
/// its process only while a live process with that pid started at the recorded time, so a reused pid is never signalled.
/// Every member that reads the process table or waits runs off the UI thread.
/// </summary>
public sealed class OwnedProcesses
{
    /// <summary>One reported process; <see cref="Started"/> is the process's own start time (UTC).</summary>
    public sealed record Entry(TerminalRef Terminal, int Pid, string Command, string Description, DateTime Started);

    private sealed class Stored
    {
        public int HostPid { get; set; }
        public DateTime HostStarted { get; set; }
        public List<Entry> Entries { get; set; } = [];
    }

    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock gate = new();
    private readonly List<Entry> entries = [];
    private readonly string? directory;
    private readonly string? file;

    /// <param name="directory">Where <c>owned-processes-&lt;host pid&gt;.json</c> lives; null keeps the registry in memory.</param>
    public OwnedProcesses(string? directory)
    {
        this.directory = directory;
        file = directory is null ? null : Path.Combine(directory, $"owned-processes-{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.json");
    }

    /// <summary>How long SIGTERM gets before SIGKILL; three seconds unless a check shortens it.</summary>
    public TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Raised, on the thread that changed the registry, after the reported processes changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<OwnedProcessInfo> List()
    {
        lock (gate) return [.. entries.Select(entry => new OwnedProcessInfo(entry.Terminal, entry.Pid, entry.Description))];
    }

    /// <summary>
    /// Records a live process as the terminal's. <paramref name="refused"/> are pids that may never be owned (the terminals' shells).
    /// Throws <see cref="ArgumentException"/> naming the reason when the process may not be owned. Reads the process table.
    /// </summary>
    public string Report(TerminalRef terminal, int pid, string? description, IReadOnlyCollection<int> refused)
    {
        if (pid <= 1) throw new ArgumentException("pid must be a process of yours; pid 1 and below are never accepted.");
        if (pid == Environment.ProcessId || refused.Contains(pid)) throw new ArgumentException($"pid {pid} is SharpRail itself or a terminal's shell, which is never accepted.");
        var row = ProcessTable.Read().FirstOrDefault(known => known.Pid == pid) ?? throw new ArgumentException($"No live process has pid {pid}.");
        // kill(pid, 0) succeeds only for a process this user may signal.
        if (!OperatingSystem.IsWindows() && Posix.Kill(pid, 0) != 0) throw new ArgumentException($"pid {pid} belongs to another user, so it cannot be stopped for you.");
        var started = StartOf(pid) ?? throw new ArgumentException($"No live process has pid {pid}.");
        var entry = new Entry(terminal, pid, row.Arguments, (description ?? "").Trim(), started);
        lock (gate)
        {
            var known = entries.FirstOrDefault(other => other.Pid == pid && Near(other.Started, started));
            if (known is not null && known.Terminal != terminal) throw new ArgumentException($"pid {pid} is already reported by another terminal.");
            entries.RemoveAll(other => other.Pid == pid);
            entries.Add(entry);
            Save();
        }
        Changed?.Invoke();
        return $"Reported pid {pid}. SharpRail stops it when this terminal closes or SharpRail quits; call owned_process_release when you stop it yourself.";
    }

    /// <summary>Forgets the terminal's record of a pid without touching the process; false when it had none.</summary>
    public bool Release(TerminalRef terminal, int pid)
    {
        bool removed;
        lock (gate)
        {
            removed = entries.RemoveAll(entry => entry.Pid == pid && entry.Terminal == terminal) > 0;
            if (removed) Save();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>Stops the terminal's reported processes and forgets them.</summary>
    public Task StopTerminalAsync(TerminalRef terminal) => StopSelectedAsync(entry => entry.Terminal == terminal);

    /// <summary>Stops every reported process and forgets them, for a quit.</summary>
    public Task StopAllAsync() => StopSelectedAsync(_ => true);

    private async Task StopSelectedAsync(Func<Entry, bool> selected)
    {
        Entry[] chosen;
        lock (gate) chosen = [.. entries.Where(selected)];
        if (chosen.Length == 0) return;
        await Task.WhenAll(chosen.Select(entry => Task.Run(() => StopAsync(entry))));
        lock (gate)
        {
            entries.RemoveAll(entry => chosen.Contains(entry));
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Start-up sweep: the records of hosts that are no longer running (the clean ones left none) are stopped where their pid and
    /// start time still match a live process, then dropped. A running host's file is left alone.
    /// </summary>
    public async Task SweepAsync()
    {
        if (directory is null || !Directory.Exists(directory)) return;
        string[] files;
        try { files = Directory.GetFiles(directory, "owned-processes-*.json"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        foreach (var path in files)
        {
            try
            {
                var stored = await Task.Run(() => JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json));
                if (stored is not null && StartOf(stored.HostPid) is { } host && Near(host, stored.HostStarted)) continue;
                foreach (var entry in stored?.Entries ?? []) await StopAsync(entry);
                File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Trace.WriteLine($"Owned-process sweep of {path} failed: {error.Message}");
            }
        }
    }

    /// <summary>Ends one process the way the SPEC says: SIGTERM, then SIGKILL after <see cref="Grace"/>, to its group when it leads one.</summary>
    internal async Task StopAsync(Entry entry)
    {
        if (!Matches(entry)) return;
        if (OperatingSystem.IsWindows())
        {
            await ProcessTable.EndAsync(new Dictionary<int, string> { [entry.Pid] = entry.Command }, Grace);
            return;
        }
        foreach (var signal in new[] { Posix.Sigterm, Posix.Sigkill })
        {
            if (!Matches(entry)) return;
            var group = Posix.GetPgid(entry.Pid) == entry.Pid;
            Posix.Kill(group ? -entry.Pid : entry.Pid, signal);
            var deadline = Stopwatch.GetTimestamp() + (long)((signal == Posix.Sigkill ? TimeSpan.FromSeconds(2) : Grace).TotalSeconds * Stopwatch.Frequency);
            do
            {
                await Task.Delay(50);
                if (!Matches(entry)) return;
            } while (Stopwatch.GetTimestamp() < deadline);
        }
    }

    // Whether the entry's pid still names the process that was reported: same start time, and not an unreaped zombie.
    private static bool Matches(Entry entry) =>
        StartOf(entry.Pid) is { } started && Near(started, entry.Started) &&
        (OperatingSystem.IsWindows() || ProcessTable.Read().Any(row => row.Pid == entry.Pid));

    private static bool Near(DateTime first, DateTime second) => (first - second).Duration() <= StartTolerance;

    private static DateTime? StartOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : process.StartTime.ToUniversalTime();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }

    // Called under the gate. An unwritable directory costs only the sweep after a crash.
    private void Save()
    {
        if (file is null) return;
        try
        {
            if (entries.Count == 0) { File.Delete(file); return; }
            Directory.CreateDirectory(directory!);
            var stored = new Stored { HostPid = Environment.ProcessId, HostStarted = StartOf(Environment.ProcessId) ?? DateTime.UtcNow, Entries = entries };
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(stored, Json));
            File.Move(temp, file, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.WriteLine("Owned-process file not saved: " + error.Message); }
    }
}