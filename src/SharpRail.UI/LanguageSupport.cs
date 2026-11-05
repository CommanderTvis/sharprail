using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api;

namespace SharpRail.UI;

/// <summary>
/// Which language servers the host this UI talks to can run. A host that cannot run some of them (a browser tab) refuses
/// their plugins, so the roster is the one place to read it. The UI then leaves out what only reaches a server: Code
/// Intelligence and the language keymap group when no server is available, and, for a file no available server owns,
/// the editor's chords, menu items, quick fixes and status polling. A roster without a language-server plugin says
/// nothing, so every file counts as served.
/// </summary>
public static class LanguageSupport
{
    /// <summary>The id prefix every language-server plugin uses.</summary>
    internal const string PluginPrefix = "lsp-";

    private static readonly Lazy<(string Id, string[] Extensions, string[] Names)[]> Owners = new(() =>
        [.. BuiltinPlugins.LanguageServers.Select(plugin => (plugin.Contract.Id, plugin.Spec.Extensions.ToArray(), plugin.Spec.FileNames.ToArray()))]);

    private static HashSet<string>? available;

    /// <summary>False when the roster lists language-server plugins and the host refused all of them.</summary>
    public static bool Available { get; private set; } = true;

    /// <summary>Raised when <see cref="Available"/> changes.</summary>
    public static event Action? Changed;

    /// <summary>Whether an available server owns this file by extension or exact name; true while the roster says nothing.</summary>
    public static bool Serves(string path)
    {
        if (available is not { } ids) return true;
        var extension = Path.GetExtension(path);
        var name = Path.GetFileName(path);
        return Owners.Value.Any(owner => ids.Contains(owner.Id) && (owner.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase) || owner.Names.Contains(name, StringComparer.Ordinal)));
    }

    internal static void Update(IReadOnlyList<PluginRosterEntry> roster)
    {
        var servers = roster.Where(entry => entry.Id.StartsWith(PluginPrefix, StringComparison.Ordinal)).ToArray();
        available = servers.Length == 0 ? null : [.. servers.Where(entry => entry.Status != PluginStatus.Refused).Select(entry => entry.Id)];
        var now = available is null || available.Count > 0;
        if (now == Available) return;
        Available = now;
        Changed?.Invoke();
    }
}