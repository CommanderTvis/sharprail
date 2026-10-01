using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SharpRail.Plugins.Api;

namespace SharpRail.UI.Plugins;

/// <summary>
/// Resolves a plugin's icon name: a Remix Icon name maps to the kit's bundled glyph, <c>asset:&lt;path&gt;</c> draws an SVG
/// from the plugin's assets, and anything else is the puzzle glyph, so an icon never admits arbitrary drawing.
/// </summary>
public static class PluginIcons
{
    public const string Fallback = "puzzle";
    private const string AssetPrefix = "asset:";
    private static readonly Dictionary<(string Plugin, string Path), Task<byte[]?>> Assets = [];

    private static readonly Dictionary<string, string> Remix = new()
    {
        ["add-line"] = "add",
        ["error-warning-line"] = "alertWarning",
        ["information-line"] = "alertInfo",
        ["arrow-down-s-line"] = "arrowDown",
        ["arrow-go-back-line"] = "arrowGoBack",
        ["arrow-right-s-line"] = "arrowRight",
        ["book-line"] = "book",
        ["book-fill"] = "bookFill",
        ["book-open-line"] = "book",
        ["box-3-line"] = "box",
        ["chat-new-line"] = "chatNew",
        ["check-line"] = "check",
        ["close-line"] = "close",
        ["discuss-line"] = "discuss",
        ["file-line"] = "file",
        ["file-diff-line"] = "fileDiff",
        ["file-text-line"] = "fileText",
        ["file-list-line"] = "fileText",
        ["folder-line"] = "folder",
        ["folder-fill"] = "folderFill",
        ["folder-open-line"] = "folderOpen",
        ["folder-2-line"] = "folderTab",
        ["fullscreen-line"] = "fullscreen",
        ["git-branch-line"] = "gitBranch",
        ["home-fill"] = "homeFill",
        ["layout-line"] = "layout",
        ["layout-left-line"] = "layoutLeft",
        ["layout-right-line"] = "layoutRight",
        ["list-unordered"] = "list",
        ["list-check"] = "list",
        ["more-line"] = "more",
        ["more-2-line"] = "moreHorizontal",
        ["node-tree"] = "network",
        ["palette-line"] = "palette",
        ["refresh-line"] = "refresh",
        ["search-line"] = "search",
        ["settings-3-line"] = "settings",
        ["settings-line"] = "settings",
        ["shield-line"] = "shield",
        ["stack-line"] = "stack",
        ["subtract-line"] = "subtract",
        ["terminal-box-line"] = "terminal",
        ["terminal-line"] = "terminal",
        ["delete-bin-line"] = "trash",
        ["delete-bin-6-line"] = "trash",
        ["puzzle-2-line"] = Fallback,
        ["puzzle-line"] = Fallback
    };

    /// <summary>Reads a plugin asset; the loader binds it to the host's plugin file read.</summary>
    public static Func<string, string, CancellationToken, Task<byte[]?>>? ReadAsset { get; set; }

    /// <summary>The bundled glyph a Remix name maps to, or the puzzle glyph.</summary>
    public static string Glyph(string? name) => name is not null && Remix.TryGetValue(name, out var glyph) ? glyph : Fallback;

    /// <summary>A control for the icon; an asset is drawn once read, over the puzzle glyph until then.</summary>
    public static Control Resolve(string? name, PluginRosterEntry? entry, IBrush? color = null, double size = 16)
    {
        if (name is null || !name.StartsWith(AssetPrefix, StringComparison.Ordinal) || entry?.Assets is not { } assets || ReadAsset is not { } read)
            return Ui.Icon(Glyph(name), color, size);
        var host = new ContentControl { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, Content = Ui.Icon(Fallback, color, size) };
        var key = (entry.Id, assets.TrimEnd('/') + "/" + name[AssetPrefix.Length..].TrimStart('/'));
        if (!Assets.TryGetValue(key, out var pending)) Assets[key] = pending = read(key.Item1, key.Item2, CancellationToken.None);
        _ = Show();
        return host;

        async Task Show()
        {
            byte[]? bytes;
            try { bytes = await pending; }
            catch (Exception error) when (error is not OperationCanceledException) { Console.Error.WriteLine($"Plugin {entry.Id} icon {name}: {error.Message}"); return; }
            if (bytes is not null) Dispatcher.UIThread.Post(() => host.Content = new SvgAsset(bytes, size));
        }
    }
}
