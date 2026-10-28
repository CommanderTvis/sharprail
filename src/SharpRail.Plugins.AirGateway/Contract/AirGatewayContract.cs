using System.Text.RegularExpressions;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.AirGateway;

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
    public bool Omp { get; init; } = true;
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
/// <param name="Remaining">Credits left of the chosen central AI source's recurring quota.</param>
/// <param name="Total">The recurring quota.</param>
/// <param name="TopUp">Credits left of the top-up quota; zero when there is none.</param>
/// <param name="RefillNext">When the quota refills, Unix milliseconds, or <see langword="null"/> when Central did not say.</param>
/// <param name="ReadAt">When the host read them from Central, Unix milliseconds.</param>
public sealed record AirCredits(double Remaining, double Total, double TopUp = 0, long? RefillNext = null, long? ReadAt = null);

/// <param name="Credits">Only with Central chosen and signed in, and only when the last check could read them; otherwise <see langword="null"/>.</param>
/// <param name="PiWire">Central's Pi wire read from its installed Pi integration (<c>central add pi</c>) on the host, or <see langword="null"/> when it is missing or unreadable.</param>
public sealed record AirStatus(AirCentral Central, string Source, IReadOnlyList<AirAgent> Agents, bool Busy = false, string? Detail = null, AirCredits? Credits = null,
    string? PiWire = null);

public sealed record AirOutcome(bool Ok, string Message, AirStatus Status);

/// <param name="Name">Shown to the user so they can tell the sources apart; sanitized, never parsed.</param>
public sealed record AirAccessSource(string Id, string Name, bool Current);

public sealed record AirAccessList(IReadOnlyList<AirAccessSource> Sources, string? Detail = null);

/// <summary>
/// What the plugin contributes to the agents' launches. Governed launches run <c>central run &lt;agent&gt; -- ...</c>,
/// which starts the agent through JetBrains Air for that launch only and leaves the agent's own configuration alone.
/// </summary>
public static partial class AirLaunch
{
    /// <summary>The agent launcher ids the plugin knows, with the agent names <c>central run</c> takes.</summary>
    public static readonly IReadOnlyDictionary<string, string> CentralNames = new Dictionary<string, string> { ["claude"] = "claude-code", ["codex"] = "codex" };

    /// <summary>
    /// The omp launcher id. Central has no omp agent and omp does not apply the base URL Central's Pi extension
    /// registers, so a governed omp gets Central's Pi wire through its documented Anthropic environment variables.
    /// </summary>
    public const string OmpLauncher = "omp";

    /// <summary>What the user runs to install Central's Pi integration, which governed omp launches read their wire from.</summary>
    public const string PiInstallCommand = "central add pi";

    /// <summary>
    /// The executable a governed omp launch gets when Central's Pi wire is unknown: a command that cannot exist, so
    /// the launch fails in the shell, naming the fix, instead of starting an ungoverned omp.
    /// </summary>
    public const string PiMissingCommand = "air-gateway-needs-central-pi-integration--run-central-add-pi";

    /// <summary>The executable a governed omp launch gets on Windows, whose shells receive no contributed environment.</summary>
    public const string WindowsUnsupportedCommand = "air-gateway-cannot-govern-omp-on-windows";

    /// <summary>The API key Central's proxy expects in place of a vendor key, as its Pi integration passes it.</summary>
    public const string WireApiKey = "wire-proxy";

    /// <summary>
    /// Runs <c>central proxy start --ensure-updated --caller agent</c> (Central's path is <c>$0</c>), best effort as the
    /// Pi integration does on session start, then execs the agent with its arguments.
    /// </summary>
    public const string ProxyStartScript = "\"$0\" proxy start --ensure-updated --caller agent >/dev/null 2>&1; exec \"$@\"";

    /// <summary>Where <c>central add pi</c> installs the integration under a home directory.</summary>
    public static string PiExtensionPath(string home) => Path.Combine(home, ".pi", "agent", "extensions", "jetbrains-central.ts");

    [GeneratedRegex(@"registerProvider\(\s*""anthropic""\s*,\s*\{\s*baseUrl:\s*""(http://127\.0\.0\.1:[0-9]{1,5}/wire/[A-Za-z0-9]+/pi)/anthropic""")]
    private static partial Regex AnthropicWire();

    /// <summary>
    /// Central's Pi wire (<c>http://127.0.0.1:&lt;port&gt;/wire/&lt;id&gt;/pi</c>) read from the text of the installed Pi
    /// integration, or <see langword="null"/> when it names no loopback Anthropic wire.
    /// </summary>
    public static string? PiWire(string extensionText) => AnthropicWire().Match(extensionText) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>Whether Central is the chosen source and governs at least one agent's launches.</summary>
    public static bool Wired(AirSettings settings) => CentralNames.Keys.Append(OmpLauncher).Any(launcherId => Enabled(settings, launcherId));

    public static bool Enabled(AirSettings settings, string launcherId) =>
        settings.Source == AirSources.Central && launcherId switch { "claude" => settings.ClaudeCode, "codex" => settings.Codex, OmpLauncher => settings.Omp, _ => false };

    /// <summary>
    /// The contribution for a launcher, or <see langword="null"/> when the source is direct, the agent is switched off or
    /// the launcher is unknown. With Central chosen it always governs, failing closed: Claude Code and Codex are wrapped
    /// with the bare name when Central has not been found, so a missing CLI fails the launch in the shell. omp, which
    /// <c>central run</c> cannot start, gets the equivalent of <c>central run pi</c>: the proxy is started first and
    /// <c>ANTHROPIC_BASE_URL</c> and <c>ANTHROPIC_API_KEY</c> point it at Central's Pi wire (<paramref name="piWire"/>);
    /// without a known wire, or on Windows, the launch runs a command that cannot exist.
    /// </summary>
    public static AgentLaunchContribution? Contribution(AirSettings settings, string launcherId, string? centralPath, string? piWire = null, bool windows = false)
    {
        if (!Enabled(settings, launcherId)) return null;
        var central = centralPath is { Length: > 0 } ? centralPath : "central";
        if (launcherId != OmpLauncher) return new() { Wrapper = [central, "run", CentralNames[launcherId], "--"], ReplacesExecutable = true };
        if (windows) return new() { Wrapper = [WindowsUnsupportedCommand], ReplacesExecutable = true };
        if (piWire is not { Length: > 0 }) return new() { Wrapper = [PiMissingCommand], ReplacesExecutable = true };
        return new()
        {
            Wrapper = ["sh", "-c", ProxyStartScript, central],
            Environment = new Dictionary<string, string> { ["ANTHROPIC_BASE_URL"] = piWire + "/anthropic", ["ANTHROPIC_API_KEY"] = WireApiKey }
        };
    }

    /// <summary>The command shape shown to the user: the wrapper followed by the agent's usual arguments.</summary>
    public static string? Shape(AirSettings settings, string launcherId, string? centralPath, string? piWire = null, bool windows = false) =>
        Contribution(settings, launcherId, centralPath, piWire, windows) switch
        {
            null => null,
            { Wrapper: [PiMissingCommand] } => "blocked: Air Gateway needs Central's Pi integration: run `" + PiInstallCommand + "`",
            { Wrapper: [WindowsUnsupportedCommand] } => "blocked: Air Gateway cannot govern omp on Windows",
            { ReplacesExecutable: false } contribution => string.Join(' ', contribution.Environment.Select(pair => pair.Key + "=" + pair.Value)) + " "
                + contribution.Wrapper[^1] + " proxy start, then " + launcherId + " <" + launcherId + " arguments>",
            var contribution => string.Join(' ', contribution.Wrapper) + " <" + launcherId + " arguments>"
        };
}

public static class AirGatewayContract
{
    public const string Id = "air-gateway";

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

    public static readonly PluginManifest Manifest = new(Id, "Air Gateway", "", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Agents,
        Description = "Starts Codex and Claude Code through JetBrains Air Gateway with the Central CLI, per launch, or leaves them on their own accounts.",
        EnabledByDefault = false,
        Host = "SharpRail.Plugins.AirGateway.Host.dll",
        Ui = "SharpRail.Plugins.AirGateway.UI.dll"
    };
}