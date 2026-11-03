using SharpRail.Plugins.Chat;
using SharpRail.Plugins.Chat.UI;

namespace SharpRail.Plugins.Pi.UI;

/// <summary>What the shared chat view needs to know about Pi: its words, document and thinking levels.</summary>
internal static class PiChat
{
    public static readonly ChatAgent Agent = new("Pi", "Pi", PiContract.DocumentScheme, PiManifest.Icon)
    {
        EffortLevels = PiThinking.Levels
    };
}