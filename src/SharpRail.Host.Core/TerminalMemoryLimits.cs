using System.Text.Json.Nodes;

namespace SharpRail.Host.Core;

/// <summary>
/// The memory guard's limits (host setting <c>terminal-memory</c>, JSON, see Terminals SPEC "Memory guard"): <see cref="PerTerminalMb"/> caps
/// one terminal's process tree (0 derives it from physical memory), <see cref="TotalMb"/> caps all terminals together (0 derives it), a
/// process gets <see cref="GraceSeconds"/> to end after its stop request, and <see cref="Protect"/> names programs that are warned about, never ended.
/// </summary>
internal sealed record TerminalMemoryLimits(bool Enabled, int PerTerminalMb, int TotalMb, int GraceSeconds, IReadOnlyList<string> Protect)
{
    internal const int DefaultGraceSeconds = 10, MaxMb = 1_048_576, MinMb = 64;
    internal static readonly TerminalMemoryLimits Default = new(true, 0, 0, DefaultGraceSeconds, []);

    /// <summary>A terminal's limit in bytes: the setting, or a quarter of physical memory clamped to 2–32 GiB.</summary>
    internal long PerTerminalBytes(long physical) => PerTerminalMb > 0 ? PerTerminalMb * 1024L * 1024 : AutomaticPerTerminal(physical);

    /// <summary>All terminals' limit in bytes: the setting, or 60% of physical memory.</summary>
    internal long TotalBytes(long physical) => TotalMb > 0 ? TotalMb * 1024L * 1024 : AutomaticTotal(physical);

    internal static long AutomaticPerTerminal(long physical) => Math.Clamp(physical / 4, 2L << 30, 32L << 30);

    internal static long AutomaticTotal(long physical) => physical * 6 / 10;

    internal bool Protects(string name) => Protect.Any(entry => string.Equals(entry, name, StringComparison.OrdinalIgnoreCase));

    internal static TerminalMemoryLimits Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try { return ParseObject(json); }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            throw new ArgumentException("Terminal memory limits must be JSON with boolean enabled, integer perTerminalMb, totalMb and graceSeconds, and a protect array of names.", error);
        }
    }

    private static TerminalMemoryLimits ParseObject(string json)
    {
        var node = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("Terminal memory limits must be a JSON object.");
        var enabled = node["enabled"]?.GetValue<bool>() ?? true;
        var perTerminal = node["perTerminalMb"]?.GetValue<int>() ?? 0;
        var total = node["totalMb"]?.GetValue<int>() ?? 0;
        var grace = node["graceSeconds"]?.GetValue<int>() ?? DefaultGraceSeconds;
        if (perTerminal != 0 && perTerminal is < MinMb or > MaxMb) throw new ArgumentException($"The terminal limit is 0 (automatic) or {MinMb} to {MaxMb} MiB.");
        if (total != 0 && total is < MinMb or > MaxMb) throw new ArgumentException($"The total limit is 0 (automatic) or {MinMb} to {MaxMb} MiB.");
        if (grace is < 1 or > 600) throw new ArgumentException("The grace period is 1 to 600 seconds.");
        var protect = new List<string>();
        foreach (var entry in node["protect"] as JsonArray ?? [])
        {
            var name = entry?.GetValue<string>().Trim() ?? "";
            if (name.Length is 0 or > 64 || name.Contains('\0') || name.Contains('/') || name.Contains('\\')) throw new ArgumentException("Each protected program is a name of 1 to 64 characters, without a path.");
            if (!protect.Contains(name, StringComparer.OrdinalIgnoreCase)) protect.Add(name);
        }
        if (protect.Count > 64) throw new ArgumentException("At most 64 programs can be protected.");
        return new(enabled, perTerminal, total, grace, protect);
    }

    internal string Serialize() => new JsonObject
    {
        ["enabled"] = Enabled,
        ["perTerminalMb"] = PerTerminalMb,
        ["totalMb"] = TotalMb,
        ["graceSeconds"] = GraceSeconds,
        ["protect"] = new JsonArray(Protect.Select(name => (JsonNode?)name).ToArray())
    }.ToJsonString();
}
