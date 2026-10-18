namespace SharpRail.Host.Abstractions;

/// <summary>User-interface-only installation status, terms review or explicit consent followed by download. Never exposed to agent tools.</summary>
public sealed record ContextConsentRequest(string Operation = "review", string ArtifactId = "", string TermsSha256 = "");

/// <summary>Host-selected Context artifact, reviewed terms and persisted user acknowledgement.</summary>
public sealed record ContextConsentResult(string State, string Detail = "", string Version = "", string ArtifactId = "", string TermsSha256 = "", string Text = "", string AcceptedAtUtc = "", string Installation = "");

public partial interface IProjectServices
{
    /// <summary>Reviews Context terms or records an explicit user acceptance and provisions the CLI without running it.</summary>
    ValueTask<ContextConsentResult> ContextConsentAsync(ContextConsentRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ContextConsentResult("unavailable", "This host does not support Context provisioning."));
}

/// <summary>A workspace-scoped Context operation. Enable, disable and login are user-interface controls only.</summary>
public sealed record ContextRequest(string Workspace, string Operation = "status", string Query = "", string Path = "", int Limit = 20);

/// <summary>Context lifecycle or bounded search output. No credentials are exposed.</summary>
public sealed record ContextResult(string State, string Detail = "", bool Enabled = false, string Data = "", string Command = "");

public partial interface IProjectServices
{
    /// <summary>Queries or controls this workspace's optional Context integration on its host.</summary>
    ValueTask<ContextResult> ContextAsync(ContextRequest request, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new ContextResult("unavailable", "This host does not support Context search."));
}