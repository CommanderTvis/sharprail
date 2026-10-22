using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpRail.Host.Core.Language;

/// <summary>One language server a workspace's files need, with how many files it owns in the bounded census.</summary>
internal sealed record ProjectLanguage(string Server, int Files);

/// <summary>
/// What the agent instructions say about a workspace's languages: its servers ranked by file count, and a prominent
/// symbol of the primary language (a type, a member of it and the file declaring them) for concrete examples. Empty
/// strings mean none was found.
/// </summary>
internal sealed record ProjectLanguageProfile(IReadOnlyList<ProjectLanguage> Languages, string Symbol = "", string Member = "", string SymbolPath = "")
{
    /// <summary>How many servers the instructions name.</summary>
    internal const int Shown = 4;

    /// <summary>Whether the instructions built from <paramref name="other"/> would differ: another top server set or example symbol. File counts alone do not count.</summary>
    internal bool SameAs(ProjectLanguageProfile other) =>
        Languages.Take(Shown).Select(item => item.Server).SequenceEqual(other.Languages.Take(Shown).Select(item => item.Server), StringComparer.Ordinal)
        && Symbol == other.Symbol && Member == other.Member && SymbolPath == other.SymbolPath;
}

/// <summary>
/// Reads a workspace's languages for the agent instructions (H20) from the same bounded census the language pane's
/// detection uses, and picks example symbols from the primary language's files without starting any server.
/// </summary>
internal static partial class ProjectLanguages
{
    /// <summary>The largest prefix of a file read for its declarations.</summary>
    private const int ReadLimit = 256 * 1024;

    // File stems that name entry points or grab-bags rather than a project's own concepts.
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "index", "main", "mod", "lib", "__init__", "__main__", "program", "assemblyinfo", "globalusings", "setup", "conftest", "utils", "util", "helpers",
        "types", "constants", "config", "test", "tests", "app", "module", "build", "vite", "webpack", "jest", "eslint", "babel"
    };

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "return", "catch", "using", "lock", "sizeof", "typeof", "nameof", "new", "else", "do", "case", "when", "defined", "main"
    };

    [GeneratedRegex(@"^[ \t]*(?:(?:export|public|internal|private|protected|sealed|abstract|partial|static|final|data|open|default|declare|readonly|unsafe|pub(?:\([^)]*\))?)\s+)*(?:class|interface|struct|record|enum|object|trait|protocol|module)\s+([A-Za-z_]\w*)", RegexOptions.Multiline)]
    private static partial Regex TypeDeclaration();

    [GeneratedRegex(@"^[ \t]*(?:(?:export|async|pub(?:\([^)]*\))?|public|private|protected|internal|static|override|suspend|inline)\s+)*(?:function|def|fn|func|fun)\s+(?:\([^)]*\)\s*)?([A-Za-z_]\w*)", RegexOptions.Multiline)]
    private static partial Regex KeywordFunction();

    [GeneratedRegex(@"^[ \t]+(?:(?:public|private|protected|internal|static|async|override|virtual|sealed|abstract|final|synchronized|extern|new)\s+)+[\w<>\[\],.?]+(?:\s+[\w<>\[\],.?]+)*?\s+([A-Za-z_]\w*)\s*(?:<[^>\n]*>)?\s*\(", RegexOptions.Multiline)]
    private static partial Regex Method();

    // A TypeScript, JavaScript or Dart method: a name, its parameters and an optional return type before the body.
    [GeneratedRegex(@"^[ \t]+(?:(?:public|private|protected|static|async|override|readonly|get|set)\s+)*([A-Za-z_]\w*)\s*\([^)\n]*\)\s*(?::\s*[^{=;\n]+)?\{\s*$", RegexOptions.Multiline)]
    private static partial Regex BareMethod();

    [GeneratedRegex(@"^(?:(?:static|inline|extern|const|unsigned|signed|struct)\s+)*[A-Za-z_]\w*[\s\*&]+(?:[A-Za-z_]\w*::)?([A-Za-z_]\w*)\s*\([^;{]*\)\s*(?:const\s*)?\{?\s*$", RegexOptions.Multiline)]
    private static partial Regex CFunction();

    /// <summary>
    /// The profile from a census of <paramref name="files"/> (workspace-relative path, owning server id): servers by
    /// descending file count, then the example symbol read from the primary server's most prominent file name.
    /// </summary>
    internal static ProjectLanguageProfile Profile(string workspace, IEnumerable<(string Path, string Server)> files, CancellationToken token = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var byServer = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (path, server) in files)
        {
            token.ThrowIfCancellationRequested();
            counts[server] = counts.GetValueOrDefault(server) + 1;
            if (!byServer.TryGetValue(server, out var list)) byServer[server] = list = [];
            if (list.Count < 20_000) list.Add(path);
        }
        var languages = counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ProjectLanguage(pair.Key, pair.Value)).ToArray();
        if (languages.Length == 0) return new([]);
        var (symbol, member, symbolPath) = Example(workspace, byServer[languages[0].Server], token);
        return new(languages, symbol, member, symbolPath);
    }

    /// <summary>
    /// A type and one of its members from the primary language. The most prominent name is the file stem most files
    /// share (partial classes, a type with its tests and views), then the one with the most bytes; its shortest file is read.
    /// </summary>
    private static (string Symbol, string Member, string Path) Example(string workspace, IReadOnlyList<string> paths, CancellationToken token)
    {
        long Size(string path)
        {
            try { return new FileInfo(System.IO.Path.Combine(workspace, path)).Length; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return 0; }
        }
        var groups = paths.Where(path => !path.Split('/', '\\').Any(segment => segment is "test" or "tests" or "spec" or "__tests__" or "examples" or "samples" or "fixtures" or "generated"))
            .GroupBy(path => Stem(path), StringComparer.Ordinal)
            .Where(group => group.Key.Length >= 3 && !Generic.Contains(group.Key) && !group.Key.EndsWith("Test", StringComparison.Ordinal) && !group.Key.EndsWith("Tests", StringComparison.Ordinal) && !group.Key.StartsWith("test_", StringComparison.Ordinal))
            .Select(group => (group.Key, Files: group.ToArray()))
            .OrderByDescending(group => group.Files.Length).ThenByDescending(group => group.Files.Sum(Size)).ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(8);
        foreach (var (stem, files) in groups)
        {
            token.ThrowIfCancellationRequested();
            foreach (var file in files.OrderBy(path => System.IO.Path.GetFileName(path).Length).ThenByDescending(Size).Take(3))
            {
                var text = Read(System.IO.Path.Combine(workspace, file));
                if (text.Length == 0) continue;
                var (symbol, member) = Declarations(text, stem);
                if (symbol.Length > 0 || member.Length > 0) return (symbol.Length > 0 ? symbol : member, symbol.Length > 0 ? member : "", file.Replace('\\', '/'));
            }
        }
        return ("", "", "");
    }

    private static string Stem(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var dot = name.IndexOf('.', 1);
        return dot > 0 ? name[..dot] : name;
    }

    private static string Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[(int)Math.Min(ReadLimit, stream.Length)];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>The declared type, preferring the one named like the file, and the first member after it that is not its constructor.</summary>
    internal static (string Symbol, string Member) Declarations(string text, string stem)
    {
        var types = TypeDeclaration().Matches(text).Where(match => !Keywords.Contains(match.Groups[1].Value)).ToArray();
        var type = types.FirstOrDefault(match => match.Groups[1].Value.Equals(stem, StringComparison.OrdinalIgnoreCase)) ?? types.FirstOrDefault();
        var start = type?.Index ?? 0;
        var name = type?.Groups[1].Value ?? "";
        var member = new[] { KeywordFunction(), Method(), BareMethod(), CFunction() }
            .SelectMany(pattern => pattern.Matches(text).Where(match => match.Index > start))
            .Where(match => match.Groups[1].Value is { Length: >= 3 } found && found != name && !Keywords.Contains(found) && !found.StartsWith("__", StringComparison.Ordinal))
            .OrderBy(match => match.Index).FirstOrDefault()?.Groups[1].Value ?? "";
        return (name, member);
    }
}

/// <summary>
/// Each workspace's last <see cref="ProjectLanguageProfile"/>, in memory and on disk, so a session launches with the
/// last known text at once while a background census refreshes it for the next one.
/// </summary>
internal sealed class ProjectLanguageCache(string directory, Func<string, CancellationToken, ProjectLanguageProfile> census)
{
    /// <summary>How long a census result is trusted before a launch starts another.</summary>
    internal static TimeSpan Fresh { get; set; } = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, ProjectLanguageProfile?> profiles = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> refreshed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> running = new(StringComparer.Ordinal);

    /// <summary>Called with the workspace when a census changed its profile materially.</summary>
    internal Action<string>? Changed { get; set; }

    private string FilePath(string workspace) =>
        Path.Combine(directory, "profiles", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace)))[..16] + ".json");

    /// <summary>The cached profile, or <see langword="null"/> before the first census; never waits for one, but starts one when the cache is stale.</summary>
    internal ProjectLanguageProfile? Get(string workspace)
    {
        if (!Path.IsPathFullyQualified(workspace)) return null;
        var profile = profiles.GetOrAdd(workspace, Load);
        Refresh(workspace);
        return profile;
    }

    /// <summary>The census in flight for a workspace, for checks.</summary>
    internal Task Pending(string workspace) => running.GetValueOrDefault(workspace) ?? Task.CompletedTask;

    private void Refresh(string workspace)
    {
        if (refreshed.TryGetValue(workspace, out var last) && DateTime.UtcNow - last < Fresh) return;
        refreshed[workspace] = DateTime.UtcNow;
        var key = workspace;
        var task = new Task(() =>
        {
            try
            {
                if (!Directory.Exists(key)) return;
                var next = census(key, CancellationToken.None);
                var before = profiles.GetValueOrDefault(key);
                if (before is not null && before.SameAs(next)) return;
                profiles[key] = next;
                Save(key, next);
                Changed?.Invoke(key);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException) { }
            finally { running.TryRemove(key, out _); }
        });
        if (running.TryAdd(key, task)) task.Start(TaskScheduler.Default);
    }

    private ProjectLanguageProfile? Load(string workspace)
    {
        try
        {
            var path = FilePath(workspace);
            return File.Exists(path) ? JsonSerializer.Deserialize<ProjectLanguageProfile>(File.ReadAllText(path)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    private void Save(string workspace, ProjectLanguageProfile profile)
    {
        var path = FilePath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(profile));
    }
}