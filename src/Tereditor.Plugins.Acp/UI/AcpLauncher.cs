namespace SharpRail.Plugins.Acp.UI;

/// <summary>What a left click on the New ACP chat button does.</summary>
/// <param name="Kind">"none" (no agent is configured), "start" (start <paramref name="Agent"/> now) or "ask" (show the picker).</param>
internal sealed record AcpLaunchChoice(string Kind, AcpAgentEntry? Agent);

/// <summary>The launcher's decisions, free of controls: which agent a click starts and what the right-click menu lists.</summary>
internal static class AcpLauncher
{
    /// <summary>
    /// The default agent when it is still configured. Otherwise, as before the setting existed: the only agent when there is exactly one,
    /// else the picker. A default that was removed is ignored here and left in the settings, so adding that agent back restores it.
    /// </summary>
    public static AcpLaunchChoice Resolve(string defaultId, IReadOnlyList<AcpAgentEntry> all)
    {
        var agents = Enabled(all);
        if (agents.Count == 0) return new("none", null);
        if (defaultId.Length > 0 && agents.FirstOrDefault(agent => agent.Id == defaultId) is { } chosen) return new("start", chosen);
        return agents.Count == 1 ? new("start", agents[0]) : new("ask", null);
    }

    /// <summary>Every configured agent for the right-click menu, in the configured order, the default marked.</summary>
    public static IReadOnlyList<(AcpAgentEntry Agent, bool IsDefault)> MenuEntries(string defaultId, IReadOnlyList<AcpAgentEntry> agents) =>
        [.. Enabled(agents).Select(agent => (agent, defaultId.Length > 0 && agent.Id == defaultId))];

    /// <summary>The agents the launcher offers: a turned-off agent stays configured but is not started from the button.</summary>
    public static IReadOnlyList<AcpAgentEntry> Enabled(IReadOnlyList<AcpAgentEntry> agents) => [.. agents.Where(agent => agent.Enabled)];

    /// <summary>True when a default is set that no configured agent has.</summary>
    public static bool DefaultIsMissing(string defaultId, IReadOnlyList<AcpAgentEntry> agents) =>
        defaultId.Length > 0 && agents.All(agent => agent.Id != defaultId);
}