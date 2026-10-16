namespace SharpRail.Plugins.UI.Kit;

internal static class FileTypeIcons
{
    private static readonly Dictionary<string, byte[]?> Assets = [];

    internal static string Name(string path)
    {
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..].ToLowerInvariant();
        if (FileIconTable.ByName.TryGetValue(name, out var byName)) return byName;
        for (var dot = name.IndexOf('.'); dot >= 0; dot = name.IndexOf('.', dot + 1))
            if (FileIconTable.ByExtension.TryGetValue(name[(dot + 1)..], out var byExtension)) return byExtension;
        return FileIconTable.Fallback;
    }

    internal static byte[]? Read(string name)
    {
        lock (Assets)
        {
            if (Assets.TryGetValue(name, out var bytes)) return bytes;
            using var stream = typeof(FileTypeIcons).Assembly.GetManifestResourceStream("SharpRail.Plugins.UI.Kit.FileIcons." + name + ".svg");
            if (stream is null) return Assets[name] = null;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return Assets[name] = buffer.ToArray();
        }
    }
}