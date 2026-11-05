namespace SharpRail.Plugins.Acp;

/// <summary>One agent of the public ACP registry (https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json, schema version 1.0.0).</summary>
public sealed record AcpRegistryAgent(string Id, string Name, string Version, string Description)
{
    public string? Repository { get; init; }
    public string? Website { get; init; }
    public IReadOnlyList<string> Authors { get; init; } = [];
    public string? License { get; init; }
    public string? LicenseUrl { get; init; }
    public string? Icon { get; init; }
    /// <summary>The distribution kinds on offer: "npx", "uvx" and "binary".</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];
    /// <summary>Whether the agent can run on this machine: it has an npx or uvx entry, or a binary for this platform.</summary>
    public bool Installable { get; init; }
    /// <summary>Why using this adapter is discouraged (SharpRail supports the agent directly); null for a normal entry.</summary>
    public string? NotRecommended { get; init; }
}

/// <summary>The registry as the settings page lists it.</summary>
public sealed record AcpRegistryView(IReadOnlyList<AcpRegistryAgent> Agents, string Source)
{
    /// <summary>The registry's own schema version (<c>version</c>).</summary>
    public string? SchemaVersion { get; init; }
    /// <summary>When the copy shown was fetched (ISO 8601); null for the built-in seed.</summary>
    public string? FetchedAt { get; init; }
    /// <summary>Why the copy is not fresh (offline, the server failed); null when it is.</summary>
    public string? Note { get; init; }
}

public sealed record AcpRegistryParams(bool Refresh);

/// <summary>What installing an agent would download and run, shown for confirmation before anything is fetched or started.</summary>
public sealed record AcpInstallPlan(string AgentId, string Name, string Version, string Kind)
{
    /// <summary>The line the confirmation shows when <see cref="NotRecommended"/> is set.</summary>
    public const string NotRecommendedLine = "SharpRail supports this agent directly. Installing the ACP adapter is not recommended.";

    /// <summary>Where the code comes from: the package name or the archive URL.</summary>
    public string Source { get; init; } = "";
    /// <summary>The command line SharpRail will run, as it would be typed.</summary>
    public string CommandLine { get; init; } = "";
    /// <summary>The archive's SHA-256 when the registry gives one; a binary without it is installed unverified and the plan says so.</summary>
    public string? Sha256 { get; init; }
    public string? Repository { get; init; }
    public string? License { get; init; }
    public string? Note { get; init; }
    /// <summary>Set when SharpRail supports the agent directly; the confirmation says so, the install is still allowed.</summary>
    public string? NotRecommended { get; init; }
}

public sealed record AcpInstallPlanParams(string AgentId, string? Kind = null);

/// <summary>The user's confirmation of one plan: agent, kind and version must match the plan the registry currently gives, or nothing is installed.</summary>
public sealed record AcpInstallRequest(string AgentId, string Kind, string Version, bool Confirmed);