using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.AirContext;

/// <summary>User-interface-only installation status, terms review or explicit consent followed by download. Never exposed to agent tools.</summary>
public sealed record ContextConsentRequest(string Operation = "review", string ArtifactId = "", string TermsSha256 = "");

/// <summary>Host-selected Context artifact, reviewed terms and persisted user acknowledgement.</summary>
public sealed record ContextConsentResult(string State, string Detail = "", string Version = "", string ArtifactId = "", string TermsSha256 = "", string Text = "", string AcceptedAtUtc = "", string Installation = "");

/// <summary>A workspace-scoped control from the user interface: status, check, enable, disable, login, logout, index, stop or logs. Search, status and index for agents are tools.</summary>
public sealed record ContextCall(string WorkspaceId, string Operation = "status");

// The snapshot method (workspace) and the channel it feeds share this shape: the channel is keyed by workspaceId.
/// <summary>A workspace's Context lifecycle state or bounded log output. <c>SignedIn</c> is whether the CLI accepted its stored login at the last probe, or <see langword="null"/> when unknown. No credentials are exposed.</summary>
public sealed record ContextOutcome(string WorkspaceId, string State, string Detail = "", bool Enabled = false, string Data = "", string Command = "", bool? SignedIn = null);

/// <summary>The persisted list of workspaces whose source the user allowed to be indexed.</summary>
public sealed record ContextWorkspaces(IReadOnlyList<string> Paths);

public static class AirContextContract
{
    public const string Id = "air-context";
    /// <summary>The state file naming the enabled workspaces.</summary>
    /// <summary>The "What is Air Context?" page the UI links to.</summary>
    public const string AboutUrl = "https://www.jetbrains.com/air/context/";

    public const string WorkspacesState = "workspaces";

    public static readonly PluginMethod<ContextConsentRequest, ContextConsentResult> Consent = new("consent");
    public static readonly PluginMethod<ContextCall, ContextOutcome> Workspace = new("workspace");
    public static readonly PluginChannel<ContextOutcome> Changed = PluginChannel<ContextOutcome>.State("changed", Workspace, "workspaceId");

    public static readonly PluginContract Contract = PluginContract.Create(Id, 1, [Consent, Workspace], [Changed]);

    public static readonly PluginManifest Manifest = new(Id, "Air Context", "asset:air.svg", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Semantic code search through JetBrains Air Context; indexing is cloud processing that you enable per workspace.",
        EnabledByDefault = false,
        Assets = "assets",
        Host = "SharpRail.Plugins.AirContext.Host.dll",
        Ui = "SharpRail.Plugins.AirContext.UI.dll"
    };
}