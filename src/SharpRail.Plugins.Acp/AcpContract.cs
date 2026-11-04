using SharpRail.Plugins.Api;
using SharpRail.Plugins.Chat;

namespace SharpRail.Plugins.Acp;

public sealed record AcpSettings
{
    /// <summary>The agent a new chat starts, by entry id; blank asks the user.</summary>
    public string DefaultAgent { get; init; } = "";
}

/// <summary>An agent the user can chat with: how to start it. Registry installs fill <see cref="Version"/> and <see cref="Source"/>; custom agents leave them blank.</summary>
public sealed record AcpAgentEntry(string Id, string Name, string Command)
{
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
    /// <summary>The directory the agent runs in; blank is the workspace.</summary>
    public string Cwd { get; init; } = "";
    /// <summary>"custom" or "registry".</summary>
    public string Origin { get; init; } = "custom";
    public string? Version { get; init; }
    public string? Description { get; init; }
}

public sealed record AcpNoParams;

public sealed record AcpAgentList(IReadOnlyList<AcpAgentEntry> Agents);

/// <summary>Chooses which agent a chat talks to; sent before the chat is opened.</summary>
public sealed record AcpChooseParams(string WorkspaceId, string ChatId, string AgentId);

public static class AcpContract
{
    public const int WireVersion = 1;

    public const string DocumentScheme = "acp-chat";

    public static readonly PluginMethod<AcpNoParams, AcpAgentList> Agents = new("agents");
    public static readonly PluginMethod<AcpAgentEntry, AcpAgentList> AddAgent = new("addAgent");
    public static readonly PluginMethod<AcpChooseParams, Ack> Choose = new("chooseAgent");

    /// <summary>The chat contract is shared with the other chat plugins: ACP serves its methods and its updates channel.</summary>
    public static readonly PluginContract Contract = PluginContract.Create<AcpSettings>(AcpManifest.Id, WireVersion,
        [.. ChatContract.Methods, Agents, AddAgent, Choose], [ChatContract.Updates]);
}