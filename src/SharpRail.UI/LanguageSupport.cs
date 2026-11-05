using SharpRail.Plugins.Api;

namespace SharpRail.UI;

/// <summary>
/// Whether the host this UI talks to can offer language servers at all. A host that cannot run them (a browser tab) refuses
/// every language-server plugin, so the roster is the one place to read it; the UI then leaves out everything that exists
/// only to reach a server: the Code Intelligence tool and settings, the editor's language chords and menu items, and the
/// language keymap actions. A roster without a language-server plugin says nothing, so the answer stays yes.
/// </summary>
public static class LanguageSupport
{
    /// <summary>The id prefix every language-server plugin uses.</summary>
    internal const string PluginPrefix = "lsp-";

    /// <summary>False when the roster lists language-server plugins and the host refused all of them.</summary>
    public static bool Available { get; private set; } = true;

    /// <summary>Raised when <see cref="Available"/> changes.</summary>
    public static event Action? Changed;

    internal static void Update(IReadOnlyList<PluginRosterEntry> roster)
    {
        var servers = roster.Where(entry => entry.Id.StartsWith(PluginPrefix, StringComparison.Ordinal)).ToArray();
        var available = servers.Length == 0 || servers.Any(entry => entry.Status != PluginStatus.Refused);
        if (available == Available) return;
        Available = available;
        Changed?.Invoke();
    }
}