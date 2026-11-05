namespace SharpRail.Plugins.Acp.UI;

/// <summary>One registry entry as the settings list shows it.</summary>
/// <param name="Agent">The registry entry.</param>
/// <param name="Status">"installed" (this version is configured), "update" (another version is), "available" or "unavailable" (no distribution for this machine).</param>
internal sealed record AcpRegistryRowModel(AcpRegistryAgent Agent, string Status)
{
    public string ActionLabel => Status switch { "installed" => "Installed", "update" => "Update ▷", "unavailable" => "Not on this machine", _ => "Install ▷" };
    public bool CanInstall => Status is "available" or "update";
    public string StatusText => Status switch { "installed" => "Installed", "update" => "Another version is installed", "unavailable" => "Not available on this machine", _ => "Not installed" };
    public string Title => $"{Agent.Name}  v{Agent.Version}";
}

/// <summary>The rows of the settings lists: what the registry browser shows for a search, and the matches with what is already configured.</summary>
internal static class AcpRows
{
    /// <summary>The registry entries matching <paramref name="query"/> (name, description or id; blank matches all), in the registry's order, each with its install status.</summary>
    public static IReadOnlyList<AcpRegistryRowModel> Registry(AcpRegistryView view, IReadOnlyList<AcpAgentEntry> configured, string? query)
    {
        var text = query?.Trim() ?? "";
        return [.. view.Agents
            .Where(agent => text.Length == 0 || agent.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || agent.Description.Contains(text, StringComparison.OrdinalIgnoreCase) || agent.Id.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Select(agent =>
            {
                var have = configured.FirstOrDefault(entry => entry.Origin == "registry" && entry.Id == agent.Id);
                return new AcpRegistryRowModel(agent, !agent.Installable ? "unavailable" : have is null ? "available" : have.Version == agent.Version ? "installed" : "update");
            })];
    }
}