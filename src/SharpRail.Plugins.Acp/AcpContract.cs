using SharpRail.Plugins.Api;
using SharpRail.Plugins.Chat;

namespace SharpRail.Plugins.Acp;

public sealed record AcpSettings
{
    /// <summary>The agent a new chat starts, by entry id; blank asks the user.</summary>
    public string DefaultAgent { get; init; } = "";
}

public static class AcpContract
{
    public const int WireVersion = 1;

    public const string DocumentScheme = "acp-chat";

    /// <summary>The chat contract is shared with the other chat plugins: ACP serves its methods and its updates channel.</summary>
    public static readonly PluginContract Contract = PluginContract.Create<AcpSettings>(AcpManifest.Id, WireVersion,
        [.. ChatContract.Methods], [ChatContract.Updates]);
}