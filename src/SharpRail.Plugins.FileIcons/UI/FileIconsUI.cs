using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.FileIcons.UI;

public sealed class FileIconsUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        context.FileIconSlot(Resolve);
        return null;
    }

    /// <summary>The slot's answer: a file's icon as one of this plugin's assets; directories fall through to core's folder glyph.</summary>
    internal static string? Resolve(string path, FileIconKind kind) => kind == FileIconKind.File ? $"asset:file-icons/{Name(path)}.svg" : null;

    /// <summary>
    /// Which icon a path wears. The whole filename wins over an extension (vitest.config.ts is a Vitest file, not a
    /// TypeScript one) and a longer extension wins over a shorter one, so .d.ts is not .ts.
    /// </summary>
    internal static string Name(string path)
    {
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..].ToLowerInvariant();
        if (FileIconTable.ByName.TryGetValue(name, out var byName)) return byName;
        for (var dot = name.IndexOf('.'); dot >= 0; dot = name.IndexOf('.', dot + 1))
            if (FileIconTable.ByExtension.TryGetValue(name[(dot + 1)..], out var byExtension)) return byExtension;
        return FileIconTable.Fallback;
    }
}