using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace SharpRail.Host.Core.Language;

/// <summary>
/// The language-server processes this host started, recorded per host process under <c>language/processes</c> so a host that died
/// cannot leave servers behind: a watchdog kills them when the host process ends, and the next host kills any that still match.
/// </summary>
internal static class LanguageProcesses
{
    /// <summary>The environment variable naming the registry directory a launch records into; the service sets it on every start.</summary>
    internal const string Variable = "SHARPRAIL_LANGUAGE_REGISTRY";
    private static readonly ConcurrentDictionary<string, Registry> registries = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<int, Registry> owners = new();

    private sealed class Registry(string file)
    {
        internal readonly string File = file;
        internal readonly Dictionary<int, string> Lines = [];
        internal int Watched;
    }

    private static long Started(Process process) => new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();

    private static string HostFile(string directory, int pid, long start) => Path.Combine(directory, $"{pid}-{start}.pids");

    /// <summary>Records a server process started with <paramref name="start"/>; launches without a registry are not recorded.</summary>
    internal static void Started(Process process, ProcessStartInfo start)
    {
        if (!start.Environment.TryGetValue(Variable, out var directory) || string.IsNullOrEmpty(directory)) return;
        try
        {
            using var host = Process.GetCurrentProcess();
            var registry = registries.GetOrAdd(directory, _ => new(HostFile(directory, host.Id, Started(host))));
            lock (registry)
            {
                registry.Lines[process.Id] = string.Join('\t', process.Id.ToString(CultureInfo.InvariantCulture), Started(process).ToString(CultureInfo.InvariantCulture), process.ProcessName);
                Write(registry);
            }
            owners[process.Id] = registry;
            if (Interlocked.Exchange(ref registry.Watched, 1) == 0) Watch(registry.File, host.Id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>
    /// The live descendants of <paramref name="pid"/> (a language server's own helpers, such as tsserver), read before the server is
    /// killed because they are re-parented and untraceable afterwards. Unix only; empty when the process table cannot be read.
    /// </summary>
    internal static async Task<int[]> DescendantsAsync(int pid)
    {
        var table = await ProcessTableAsync();
        var found = new List<int>();
        for (var frontier = new List<int> { pid }; frontier.Count > 0;)
        {
            frontier = [.. table.Where(row => frontier.Contains(row.Parent)).Select(row => row.Pid)];
            found.AddRange(frontier);
        }
        return [.. found];
    }

    /// <summary>Waits until none of <paramref name="pids"/> is running any more (a zombie counts as gone), for at most <paramref name="limit"/>.</summary>
    internal static async Task WaitGoneAsync(int[] pids, TimeSpan limit)
    {
        if (pids.Length == 0) return;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < limit)
        {
            var table = await ProcessTableAsync();
            if (!table.Any(row => pids.Contains(row.Pid) && !row.Zombie)) return;
            await Task.Delay(20);
        }
    }

    private static async Task<List<(int Pid, int Parent, bool Zombie)>> ProcessTableAsync()
    {
        var rows = new List<(int, int, bool)>();
        if (OperatingSystem.IsWindows() || OperatingSystem.IsAndroid() || OperatingSystem.IsBrowser()) return rows;
        var start = new ProcessStartInfo("ps") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-axo");
        start.ArgumentList.Add("pid=,ppid=,stat=");
        try
        {
            using var ps = Process.Start(start);
            if (ps is null) return rows;
            var output = await ps.StandardOutput.ReadToEndAsync();
            await ps.WaitForExitAsync();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length >= 3 && int.TryParse(fields[0], CultureInfo.InvariantCulture, out var pid) && int.TryParse(fields[1], CultureInfo.InvariantCulture, out var parent))
                    rows.Add((pid, parent, fields[2].StartsWith('Z')));
            }
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
        return rows;
    }

    /// <summary>Forgets a server process that exited.</summary>
    internal static void Exited(int pid)
    {
        if (!owners.TryRemove(pid, out var registry)) return;
        lock (registry)
        {
            registry.Lines.Remove(pid);
            try { Write(registry); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Write(Registry registry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(registry.File)!);
        if (registry.Lines.Count == 0) { File.Delete(registry.File); return; }
        var temporary = registry.File + ".tmp";
        File.WriteAllLines(temporary, registry.Lines.Values);
        File.Move(temporary, registry.File, true);
    }

    /// <summary>
    /// Starts the watchdog that kills the recorded servers once <paramref name="host"/> exits, however it exits. Unix only: a
    /// Windows host relies on the servers ending with their closed stdin and on <see cref="Sweep"/> at the next start.
    /// </summary>
    internal static Process? Watch(string file, int host)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsAndroid() || OperatingSystem.IsBrowser()) return null;
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("while kill -0 \"$1\" 2>/dev/null; do sleep 2; done; " +
            "if [ -f \"$2\" ]; then while IFS=\"$(printf '\\t')\" read -r pid started name; do kill -KILL \"$pid\" 2>/dev/null; done < \"$2\"; rm -f \"$2\"; fi");
        start.ArgumentList.Add("sharprail-language-watchdog");
        start.ArgumentList.Add(host.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(file);
        try { return Process.Start(start); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Kills the servers that hosts which are no longer running recorded in <paramref name="directory"/>, when the process with that id
    /// still has the recorded name and start time; returns how many it killed. Records of running hosts (this one or another) are kept.
    /// </summary>
    internal static int Sweep(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        var killed = 0;
        using var self = Process.GetCurrentProcess();
        foreach (var file in Directory.EnumerateFiles(directory, "*.pids"))
        {
            var parts = Path.GetFileNameWithoutExtension(file).Split('-');
            if (parts.Length != 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var hostPid) || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var hostStart)) continue;
            if (hostPid == self.Id || Alive(hostPid, hostStart, null)) continue;
            try
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    var fields = line.Split('\t');
                    if (fields.Length != 3 || !int.TryParse(fields[0], CultureInfo.InvariantCulture, out var pid) || !long.TryParse(fields[1], CultureInfo.InvariantCulture, out var start)) continue;
                    if (!Alive(pid, start, fields[2])) continue;
                    try
                    {
                        using var orphan = Process.GetProcessById(pid);
                        orphan.Kill(true);
                        killed++;
                    }
                    catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                }
                File.Delete(file);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return killed;
    }

    /// <summary>Whether <paramref name="pid"/> runs and is the same process: started within two seconds of <paramref name="start"/>, with the same name when given.</summary>
    private static bool Alive(int pid, long start, string? name)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            if (name is not null && process.ProcessName != name) return false;
            return Math.Abs(Started(process) - start) <= 2000;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
    }
}