using Avalonia.Media;

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

    /// <summary>The icon key for a path or folder in a style: the bare name for plain, else <c>colored/</c> or <c>material/</c> before it.</summary>
    internal static string Key(string style, string path, bool directory = false) => (style, directory) switch
    {
        ("material", true) => "material/folder",
        (_, true) => "",
        ("colored", _) => "colored/" + Name(path),
        ("material", _) => "material/" + Name(path),
        _ => Name(path)
    };

    /// <summary>The artwork and tint for a key from <see cref="Key"/>. A null tint means the artwork keeps its own colours.</summary>
    internal static (byte[]? Bytes, IBrush? Tint) Resolve(string key)
    {
        if (key.StartsWith("material/", StringComparison.Ordinal))
        {
            var name = key["material/".Length..];
            return Read("material." + name) is { } original ? (original, null) : (Read(name), Tint(name));
        }
        if (key.StartsWith("colored/", StringComparison.Ordinal))
        {
            var name = key["colored/".Length..];
            return (Read(name), Tint(name));
        }
        return (Read(key), null);
    }

    /// <summary>The design role a Material icon name is tinted with, or null for the neutral text colour.</summary>
    internal static IBrush? Tint(string name) => Category(name) switch
    {
        FileIconCategory.Code => Ui.FileIconCode,
        FileIconCategory.Markup => Ui.FileIconMarkup,
        FileIconCategory.Data => Ui.FileIconData,
        FileIconCategory.Script => Ui.FileIconScript,
        FileIconCategory.Media => Ui.FileIconMedia,
        _ => null
    };

    internal static FileIconCategory Category(string name)
    {
        if (Has(name, "image", "video", "audio", "font", "pdf", "zip", "archive", "3d", "lock", "certificate", "key")) return FileIconCategory.Media;
        if (Has(name, "console", "powershell", "docker", "makefile", "gradle", "npm", "yarn", "pnpm", "git", "bazel", "cmake", "terraform", "ansible", "just", "nix", "shell")) return FileIconCategory.Script;
        if (Has(name, "json", "yaml", "toml", "settings", "database", "sql", "csv", "xml", "ini", "env", "config", "log", "table", "proto", "graphql", "prisma")) return FileIconCategory.Data;
        if (Has(name, "html", "css", "sass", "less", "stylus", "markdown", "readme", "svg", "vue", "svelte", "astro", "tex", "document", "word", "mdx", "pug", "handlebars", "razor")) return FileIconCategory.Markup;
        if (Has(name, "typescript", "javascript", "react", "python", "java", "kotlin", "csharp", "cpp", "go", "rust", "swift", "php", "ruby", "dart", "lua", "haskell", "scala", "elixir", "erlang", "clojure", "lisp", "r", "perl", "zig", "nim", "ocaml", "fsharp", "julia", "c", "h", "assembly", "coffee", "groovy", "vb", "test", "todo")) return FileIconCategory.Code;
        return FileIconCategory.Other;
    }

    // A name matches a keyword as a whole hyphen-separated part or by prefix for the long ones (typescript-def is typescript).
    private static bool Has(string name, params string[] keywords)
    {
        foreach (var part in name.Split('-'))
            foreach (var keyword in keywords)
                if (part == keyword || (keyword.Length > 3 && part.StartsWith(keyword, StringComparison.Ordinal))) return true;
        return false;
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

internal enum FileIconCategory { Other, Code, Markup, Data, Script, Media }