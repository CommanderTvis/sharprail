using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SharpRail.Host.Core.Language;

/// <summary>
/// The limits on running language servers (host setting <c>language-limits</c>, JSON): stop after <see cref="IdleMinutes"/> without
/// use (0 never), keep the total memory of all servers under <see cref="MemoryBudgetMb"/> (0 derives it from physical memory) and run at
/// most <see cref="Cap"/> servers of one kind.
/// </summary>
internal sealed record LanguageServerLimits(int IdleMinutes, int MemoryBudgetMb, IReadOnlyDictionary<string, int> Caps)
{
    internal const int DefaultIdleMinutes = 10;
    internal const int DefaultCap = 6;
    /// <summary>Count caps of the heavy servers: Roslyn per loaded solution, and the JVM servers.</summary>
    internal static readonly IReadOnlyDictionary<string, int> DefaultCaps = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["csharp"] = 4,
        ["intellij"] = 2,
        ["kotlin"] = 2,
        ["scala"] = 2
    };
    internal static readonly LanguageServerLimits Default = new(DefaultIdleMinutes, 0, new Dictionary<string, int>(StringComparer.Ordinal));

    /// <summary>The count cap of a server kind: its own setting, its default, else the <c>*</c> setting for every other kind.</summary>
    internal int Cap(string server) => Caps.TryGetValue(server, out var cap) ? cap : DefaultCaps.TryGetValue(server, out var preset) ? preset : Caps.GetValueOrDefault("*", DefaultCap);

    internal TimeSpan? Idle => IdleMinutes > 0 ? TimeSpan.FromMinutes(IdleMinutes) : null;

    /// <summary>The memory budget in bytes: the setting, or 30% of physical memory clamped to 3–24 GiB.</summary>
    internal long BudgetBytes => MemoryBudgetMb > 0 ? MemoryBudgetMb * 1024L * 1024 : AutomaticBudget(LanguageMemory.PhysicalBytes);

    internal static long AutomaticBudget(long physical) => Math.Clamp(physical * 3 / 10, 3L << 30, 24L << 30);

    internal static LanguageServerLimits Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try { return ParseObject(json); }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            throw new ArgumentException("Language server limits must be JSON with integer idleMinutes, memoryBudgetMb and caps.", error);
        }
    }

    private static LanguageServerLimits ParseObject(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("Language server limits must be a JSON object.");
        var idle = node["idleMinutes"]?.GetValue<int>() ?? DefaultIdleMinutes;
        var budget = node["memoryBudgetMb"]?.GetValue<int>() ?? 0;
        if (idle is < 0 or > 10_080) throw new ArgumentException("The idle timeout is 0 (never) to 10080 minutes.");
        if (budget is < 0 or > 1_048_576) throw new ArgumentException("The memory budget is 0 (automatic) to 1048576 MiB.");
        var caps = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, value) in node["caps"] as JsonObject ?? [])
        {
            var cap = value?.GetValue<int>() ?? 0;
            if (id.Length is 0 or > 64 || cap is < 1 or > 64) throw new ArgumentException("Each server cap is 1 to 64.");
            caps[id] = cap;
        }
        return new(idle, budget, caps);
    }

    internal string Serialize() => new JsonObject
    {
        ["idleMinutes"] = IdleMinutes,
        ["memoryBudgetMb"] = MemoryBudgetMb,
        ["caps"] = new JsonObject(Caps.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => KeyValuePair.Create(pair.Key, (JsonNode?)pair.Value)))
    }.ToJsonString();
}

/// <summary>Cheap host memory probes for the language-server limits; each is a system call or a small /proc read, never a process launch.</summary>
internal static class LanguageMemory
{
    /// <summary>Physical memory (or the container limit) as the runtime sees it.</summary>
    internal static long PhysicalBytes => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>
    /// The memory a process costs the system: on macOS its physical footprint (what Activity Monitor shows; resident size undercounts
    /// compressed memory), elsewhere its resident set.
    /// </summary>
    internal static long Footprint(Process process)
    {
        if (OperatingSystem.IsMacOS())
        {
            // rusage_info_v0: a 16-byte uuid, then unsigned 64-bit fields; ri_phys_footprint is the eighth.
            var buffer = new byte[96];
            if (proc_pid_rusage(process.Id, 0, buffer) == 0) return BitConverter.ToInt64(buffer, 72);
        }
        process.Refresh();
        return process.WorkingSet64;
    }

    /// <summary>
    /// Whether the system is short of memory: macOS reports a warning or critical pressure level, Linux has under 10% available, Windows
    /// is past the runtime's high memory load threshold.
    /// </summary>
    internal static bool UnderPressure()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var level = 0; var size = (nint)sizeof(int);
                return sysctlbyname("kern.memorystatus_vm_pressure_level", ref level, ref size, 0, 0) == 0 && level >= 2;
            }
            if (OperatingSystem.IsLinux())
            {
                long total = 0, available = -1;
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = Kilobytes(line);
                    else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) available = Kilobytes(line);
                }
                return total > 0 && available >= 0 && available * 10 < total;
            }
            var info = GC.GetGCMemoryInfo();
            return info.HighMemoryLoadThresholdBytes > 0 && info.MemoryLoadBytes >= info.HighMemoryLoadThresholdBytes;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    private static long Kilobytes(string line) => long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture);

    [DllImport("libc")] private static extern int proc_pid_rusage(int pid, int flavor, byte[] buffer);
    [DllImport("libc", EntryPoint = "sysctlbyname")] private static extern int sysctlbyname([MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref int value, ref nint size, nint newValue, nint newSize);
}