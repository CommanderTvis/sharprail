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

    /// <summary>Every configured agent, registry installs and custom ones together, by name. <paramref name="view"/> (when loaded) supplies the Not recommended and Update available tags.</summary>
    public static IReadOnlyList<AcpAgentRowModel> Agents(IReadOnlyList<AcpAgentEntry> entries, AcpRegistryView? view, IReadOnlyDictionary<string, string> problems, string defaultId) =>
        [.. entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).Select(entry =>
        {
            var registry = RegistryAgent(entry, view);
            var tags = new List<string> { entry.Origin == "registry" ? "Registry" : "Custom" };
            if (registry?.NotRecommended is not null) tags.Add("Not recommended");
            if (registry is not null && registry.Version != entry.Version) tags.Add("Update available");
            if (!entry.Enabled) tags.Add("Off");
            if (defaultId.Length > 0 && entry.Id == defaultId) tags.Add("Default");
            return new AcpAgentRowModel(entry, tags, problems.GetValueOrDefault(entry.Id));
        })];

    /// <summary>The registry's own entry for an installed registry agent, when the registry list is loaded.</summary>
    public static AcpRegistryAgent? RegistryAgent(AcpAgentEntry entry, AcpRegistryView? view) =>
        entry.Origin == "registry" ? view?.Agents.FirstOrDefault(agent => agent.Id == entry.Id) : null;
}

/// <summary>One row of the left list: an agent, its small tags and a problem when something is wrong (a status dot).</summary>
internal sealed record AcpAgentRowModel(AcpAgentEntry Entry, IReadOnlyList<string> Tags, string? Problem)
{
    public string Subtitle => string.Join(" · ", Tags);
    public bool HasDot => Problem is not null;
}

/// <summary>What the detail pane shows for one agent, read-only parts first.</summary>
internal sealed record AcpAgentDetail(AcpAgentEntry Entry, AcpRegistryAgent? Registry, string? Problem)
{
    public bool IsRegistry => Entry.Origin == "registry";
    public string Description => Entry.Description ?? Registry?.Description ?? "";
    public string? Website => Entry.Website ?? Registry?.Website;
    public string? Repository => Entry.Repository ?? Registry?.Repository;
    public string? License => Entry.License ?? Registry?.License;
    public string Version => IsRegistry ? "Version " + (Entry.Version ?? "unknown") : "Custom agent: you provide the program";
    public string SourceLine => IsRegistry ? "From the ACP registry: " + Entry.Id + (Entry.Source is { } source ? "\nSource: " + source : "") : "Added by you";
    /// <summary>The newer version the registry offers, when there is one.</summary>
    public string? UpdateTo => Registry is { } registry && registry.Version != Entry.Version ? registry.Version : null;
    public bool CanUpdate => IsRegistry && UpdateTo is not null && Registry!.Installable;
    public bool CanReinstall => IsRegistry && UpdateTo is null && Registry is { Installable: true };
    public string InstallStatus => !IsRegistry ? "Custom" : Problem is { } problem ? problem : UpdateTo is { } newer ? $"Installed {Entry.Version}. Version {newer} is available." : "Installed";
    /// <summary>Where the program is: the managed file, or the runner (npx, uvx) that fetches the pinned package.</summary>
    public string Location => Path.IsPathRooted(Entry.LaunchCommand()) ? Entry.LaunchCommand() : Entry.LaunchCommand() + " runs the package named above";
    /// <summary>What was checked when it was installed; an install from before this was recorded says so.</summary>
    public string Verification => Entry.Verification ?? "Not recorded: this agent was installed before checks were recorded. Reinstall it to see what is verified.";
    public string CommandLine => AcpEntryText.CommandLine(Entry);
}