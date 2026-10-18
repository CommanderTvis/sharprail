using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.AirGovernance;

/// <summary>The AI sources the plugin switches between. <see cref="Direct"/> leaves the agents as the user configured them.</summary>
public static class AirSources
{
    /// <summary>Each agent signs in with its own account; the plugin changes nothing.</summary>
    public const string Direct = "direct";

    /// <summary>Each enabled agent is started through JetBrains Air by the user's own <c>central run</c>, for that launch only.</summary>
    public const string Central = "central";

    public static bool IsKnown(string value) => value is Direct or Central;
}

/// <summary>The plugin's settings namespace: which source is chosen, and for which agents.</summary>
public sealed record AirSettings
{
    public string Source { get; init; } = AirSources.Direct;
    public bool ClaudeCode { get; init; } = true;
    public bool Codex { get; init; } = true;
}

/// <param name="Probe">Asks <c>central status</c> for the sign-in and proxy rows; without it only the binary and its version are read.</param>
public sealed record AirStatusParams(bool Probe = false);

public sealed record AirActionParams;

public sealed record AirSwitchParams(string SourceId);

/// <param name="State">absent, outdated, supported, malformed-version or probe-failed.</param>
/// <param name="Auth">connected, signed-out or unknown.</param>
/// <param name="Proxy">running, stopped or unknown.</param>
/// <param name="InstallCommand">What the user runs themselves to install Central on this host's platform.</param>
public sealed record AirCentral(string State, string Auth, string Proxy, string InstallCommand, string? Path = null, string? Version = null);

/// <param name="Launch">The exact command shape prefixed to this agent's launches, or <see langword="null"/> when its launches are unchanged.</param>
public sealed record AirAgent(string Id, string Label, bool Enabled, string? Launch = null);

/// <param name="Source">The source in effect for new launches: the chosen one.</param>
public sealed record AirStatus(AirCentral Central, string Source, IReadOnlyList<AirAgent> Agents, bool Busy = false, string? Detail = null);

public sealed record AirOutcome(bool Ok, string Message, AirStatus Status);

/// <param name="Name">Shown to the user so they can tell the sources apart; sanitized, never parsed.</param>
public sealed record AirAccessSource(string Id, string Name, bool Current);

public sealed record AirAccessList(IReadOnlyList<AirAccessSource> Sources, string? Detail = null);

/// <summary>
/// What the plugin contributes to the agents' launches. Governed launches run <c>central run &lt;agent&gt; -- ...</c>,
/// which starts the agent through JetBrains Air for that launch only and leaves the agent's own configuration alone.
/// </summary>
public static class AirLaunch
{
    /// <summary>The agent launcher ids the plugin knows, with the agent names <c>central run</c> takes.</summary>
    public static readonly IReadOnlyDictionary<string, string> CentralNames = new Dictionary<string, string> { ["claude"] = "claude-code", ["codex"] = "codex" };

    public static bool Enabled(AirSettings settings, string launcherId) =>
        settings.Source == AirSources.Central && CentralNames.ContainsKey(launcherId) && (launcherId == "claude" ? settings.ClaudeCode : settings.Codex);

    /// <summary>
    /// The contribution for a launcher, or <see langword="null"/> when the source is direct, the agent is switched off or
    /// the launcher is unknown. With Central chosen it always wraps, using the bare name when Central has not been found:
    /// a missing CLI then fails the launch in the shell instead of silently starting an ungoverned agent.
    /// </summary>
    public static AgentLaunchContribution? Contribution(AirSettings settings, string launcherId, string? centralPath) =>
        Enabled(settings, launcherId)
            ? new() { Wrapper = [centralPath is { Length: > 0 } ? centralPath : "central", "run", CentralNames[launcherId], "--"], ReplacesExecutable = true }
            : null;

    /// <summary>The command shape shown to the user: the wrapper followed by the agent's usual arguments.</summary>
    public static string? Shape(AirSettings settings, string launcherId, string? centralPath) =>
        Contribution(settings, launcherId, centralPath) is { } contribution ? string.Join(' ', contribution.Wrapper) + " <" + launcherId + " arguments>" : null;
}

public static class AirGovernanceContract
{
    public const string Id = "air-governance";

    public static readonly PluginMethod<AirStatusParams, AirStatus> Status = new("status");
    public static readonly PluginMethod<AirActionParams, AirOutcome> SignIn = new("sign-in");
    public static readonly PluginMethod<AirActionParams, AirOutcome> SignOut = new("sign-out");
    public static readonly PluginMethod<AirActionParams, AirOutcome> StartProxy = new("start-proxy");
    public static readonly PluginMethod<AirActionParams, AirOutcome> Update = new("update");
    public static readonly PluginMethod<AirActionParams, AirAccessList> AccessSources = new("access-sources");
    public static readonly PluginMethod<AirSwitchParams, AirOutcome> SwitchAccess = new("switch-access");
    public static readonly PluginChannel<AirStatus> Changed = PluginChannel<AirStatus>.State("status", Status);

    public static readonly PluginContract Contract = PluginContract.Create<AirSettings>(Id, 1,
        [Status, SignIn, SignOut, StartProxy, Update, AccessSources, SwitchAccess], [Changed]);

    public static readonly PluginManifest Manifest = new(Id, "Air Governance", "asset:air-governance.svg", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Starts Codex and Claude Code through JetBrains Air governance with the Central CLI, per launch, or leaves them on their own accounts.",
        EnabledByDefault = false,
        Assets = "assets",
        Host = "SharpRail.Plugins.AirGovernance.Host.dll",
        Ui = "SharpRail.Plugins.AirGovernance.UI.dll"
    };
}
