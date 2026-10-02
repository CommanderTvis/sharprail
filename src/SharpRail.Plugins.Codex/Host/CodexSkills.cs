using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharpRail.Plugins.Codex.Host;

/// <summary>
/// SharpRail's workflow skills for Codex, which discovers a user's skills as <c>$CODEX_HOME/skills/&lt;name&gt;/SKILL.md</c>.
/// Activation copies the shipped ones there and disabling takes them away again. One stamp file in SharpRail's state
/// directory holds the SHA-256 of each text SharpRail last wrote: a file still matching its stamp is SharpRail's to
/// refresh or remove, an edited one is the user's, and a skill of the same name SharpRail never wrote is left alone.
/// </summary>
public static class CodexSkills
{
    public static string Root() => Path.Combine(CodexConfig.Home(), "skills");

    /// <summary>The skills this build ships, staged with the plugin's assets.</summary>
    public static string ShippedRoot(string? assetsDirectory) =>
        Path.Combine(assetsDirectory ?? Path.Combine(AppContext.BaseDirectory, "plugins", CodexManifest.Id, "assets"), "skills");

    private static string StampsPath() => Path.Combine(CodexSystemPrompt.StateDirectory(), CodexManifest.Id, "skills.sharprail-default");

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static Dictionary<string, string> ReadStamps()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StampsPath())) ?? []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private static void WriteStamps(Dictionary<string, string> stamps)
    {
        if (stamps.Count == 0)
        {
            if (File.Exists(StampsPath())) File.Delete(StampsPath());
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(StampsPath())!);
        File.WriteAllText(StampsPath(), JsonSerializer.Serialize(stamps.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary()));
    }

    private static string Installed(string relative) => Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar));

    private static string? Read(string relative) => File.Exists(Installed(relative)) ? File.ReadAllText(Installed(relative)) : null;

    // Takes the file away while it is still the text SharpRail wrote, with the directories that leaves empty.
    private static void Forget(string relative, Dictionary<string, string> stamps)
    {
        var current = Read(relative);
        if (current is not null && Hash(current) != stamps[relative]) return;
        stamps.Remove(relative);
        if (current is not null) File.Delete(Installed(relative));
        for (var directory = Path.GetDirectoryName(Installed(relative))!;
             directory != Root() && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any();
             directory = Path.GetDirectoryName(directory)!)
            Directory.Delete(directory);
    }

    /// <summary>Brings Codex's copies to the shipped text, keeping every file the user edited or owns.</summary>
    public static void Install(string shippedRoot)
    {
        if (!Directory.Exists(shippedRoot)) return;
        var shipped = Directory.EnumerateFiles(shippedRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetRelativePath(shippedRoot, file).Replace(Path.DirectorySeparatorChar, '/'), File.ReadAllText);
        var stamps = ReadStamps();
        // An edited file SharpRail no longer ships stays, and stops being tracked.
        foreach (var dropped in stamps.Keys.Except(shipped.Keys).ToArray())
        {
            Forget(dropped, stamps);
            stamps.Remove(dropped);
        }
        foreach (var skill in shipped.Keys.GroupBy(path => path.Split('/')[0]))
        {
            var entry = skill.Key + "/SKILL.md";
            if (!shipped.TryGetValue(entry, out var entryText)) continue;
            if (!stamps.ContainsKey(entry) && Read(entry) is { } theirs && theirs != entryText) continue;
            foreach (var path in skill)
            {
                var current = Read(path);
                var ours = current is null || (stamps.TryGetValue(path, out var stamp) && Hash(current) == stamp);
                if (!ours && current != shipped[path]) continue;
                if (current != shipped[path])
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Installed(path))!);
                    File.WriteAllText(Installed(path), shipped[path]);
                }
                stamps[path] = Hash(shipped[path]);
            }
        }
        WriteStamps(stamps);
    }

    /// <summary>Removes the files SharpRail installed; an edited one stays, still known as SharpRail's skill with the user's text.</summary>
    public static void Remove()
    {
        var stamps = ReadStamps();
        foreach (var path in stamps.Keys.ToArray()) Forget(path, stamps);
        WriteStamps(stamps);
    }
}