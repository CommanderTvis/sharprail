using Tereditor.Plugins.Chat;
using Tereditor.Plugins.Chat.UI;

namespace Tereditor.Plugins.Pi.UI;

/// <summary>What the shared chat view needs to know about Pi: its words, document and thinking levels.</summary>
internal static class PiChat
{
    public static readonly ChatAgent Agent = new("Pi", "Pi", PiContract.DocumentScheme, PiManifest.Icon)
    {
        EffortLevels = PiThinking.Levels
    };
}