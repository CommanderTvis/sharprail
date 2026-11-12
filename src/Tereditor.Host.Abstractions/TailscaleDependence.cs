using System.Text.Json.Nodes;

using SharpRail.Plugins.Api;

namespace SharpRail.Host.Abstractions;

/// <summary>What a client connected through Tailscale may not turn off, since doing so would cut its own connection.</summary>
public static class TailscaleDependence
{
    public const string PluginId = "tailscale";

    public const string Refusal = "This client is connected through Tailscale; turning it off here would disconnect you. Do it from the host machine or a client connected another way.";

    /// <summary>The Tailscale plugin and every plugin it depends on, directly or transitively, by the roster.</summary>
    public static IReadOnlySet<string> Protected(IReadOnlyList<PluginRosterEntry> roster)
    {
        var protectedIds = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([PluginId]);
        while (pending.TryPop(out var id))
        {
            if (!protectedIds.Add(id)) continue;
            foreach (var dependency in roster.FirstOrDefault(entry => entry.Id == id)?.DependsOn ?? [])
                pending.Push(dependency);
        }
        return protectedIds;
    }

    /// <summary>Whether <paramref name="change"/> turns off (or resets) a plugin the Tailscale plugin needs.</summary>
    public static bool TurnsOff(HostStateChange change, IReadOnlyList<PluginRosterEntry> roster)
    {
        if (change.Kind != "plugin-settings" || !Protected(roster).Contains(change.Key)) return false;
        if (change.Value.Length == 0) return true;
        try { return JsonNode.Parse(change.Value)?["enabled"] is JsonValue flag && flag.TryGetValue<bool>(out var enabled) && !enabled; }
        catch (System.Text.Json.JsonException) { return false; }
    }
}