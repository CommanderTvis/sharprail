using SharpRail.Plugins.Chat.UI;

namespace SharpRail.Plugins.Acp.UI;

/// <summary>What the shared chat view needs to know about an ACP agent: its words and document.</summary>
internal static class AcpChat
{
    public static readonly ChatAgent Agent = new("ACP agent", "agent", AcpContract.DocumentScheme, AcpManifest.Manifest.Icon)
    {
        UnavailableHint = ". Turn on the ACP plugin and choose an agent in Settings."
    };
}