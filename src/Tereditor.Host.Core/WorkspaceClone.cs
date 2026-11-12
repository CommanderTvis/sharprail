using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Tomlyn;

namespace SharpRail.Host.Core;

/// <summary>
/// Warm-start for a created workspace: copy-on-write clones of the ignored paths the project lists in its
/// <c>.sharprail/workspace.toml</c> (dependencies, build caches), so the new worktree starts warm instead of cold. Best effort in every respect: no volume,
/// platform or filesystem that cannot clone, and no failure, ever reaches the caller.
/// </summary>
internal static class WorkspaceClone
{
    private const uint NoFollow = 1;

    [DllImport("libc", EntryPoint = "clonefile", SetLastError = true)]
    private static extern int CloneFile([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);

    /// <summary>Whether this platform can clone at all: APFS on macOS, and <c>cp --reflink=auto</c> on Linux, which shares extents where the filesystem can and copies otherwise.</summary>
    internal static bool Supported => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid();

    /// <summary>
    /// Clones one path (a directory clones its whole tree, symlinks stay symlinks) to a destination that must not exist.
    /// Returns false for any refusal: non-APFS, another volume, an existing destination, a missing source.
    /// </summary>
    internal static bool TryClone(string source, string destination)
    {
        if (!Supported) return false;
        if (OperatingSystem.IsLinux()) return CopyLinux(source, destination);
        try { return CloneFile(source, destination, NoFollow) == 0; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    // GNU cp keeps symlinks as links (-a) and shares extents on btrfs and XFS; elsewhere it is a plain copy. A failed copy leaves nothing behind.
    private static bool CopyLinux(string source, string destination)
    {
        try
        {
            var start = new ProcessStartInfo("cp") { UseShellExecute = false, RedirectStandardError = true, ArgumentList = { "-a", "--reflink=auto", "--", source, destination } };
            using var process = Process.Start(start);
            if (process is null) return false;
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 0) return true;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { return false; }
        try { if (Directory.Exists(destination)) Directory.Delete(destination, true); else File.Delete(destination); } catch (IOException) { }
        return false;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> Running = new(StringComparer.Ordinal);

    /// <summary>Runs <see cref="CloneConfiguredAsync"/> in the background and remembers it until it ends, so <see cref="WhenClonedAsync"/> can wait for it.</summary>
    /// <param name="primers">Language servers' <c>PrimeWorkspaceAsync</c>, run after the clone; a failing one is reported and skipped.</param>
    internal static Task StartConfigured(string source, string target, Action<string>? report = null,
        IReadOnlyList<Func<string, string, CancellationToken, Task>>? primers = null)
    {
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var clone = Task.Run(async () =>
        {
            await CloneConfiguredAsync(source, target, CancellationToken.None, report: report);
            foreach (var prime in primers ?? [])
                try { await prime(source, target, CancellationToken.None); }
                catch (Exception error) { report?.Invoke("Priming a language server index failed: " + error.Message); }
        });
        Running[target] = clone;
        _ = clone.ContinueWith(_ => Running.TryRemove(new(target, clone)), TaskScheduler.Default);
        return clone;
    }

    /// <summary>
    /// Waits, at most <paramref name="timeout"/>, for a clone into <paramref name="workspace"/> that is still running; true when none is. A language
    /// server started before the cloned restore and build outputs arrive would restore and design-time build the whole project graph itself.
    /// </summary>
    internal static async Task<bool> WhenClonedAsync(string workspace, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!Running.TryGetValue(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)), out var clone)) return true;
        try { await clone.WaitAsync(timeout, cancellationToken); }
        catch (TimeoutException) { return false; }
        catch (Exception error) when (error is not OperationCanceledException) { }
        return true;
    }

    /// <summary>Where a project states what its created workspaces start with, relative to the project root.</summary>
    internal const string ConfigPath = ".sharprail/workspace.toml";

    private const int ConfigLimit = ProjectToml.MaximumBytes;

    /// <summary>
    /// The project's clone rules, in TOML: <c>version = 1</c> and a <c>[clone]</c> table with <c>include</c> and <c>exclude</c>
    /// string arrays. Patterns are slash-separated, relative to the project root: <c>*</c> and <c>?</c> stay inside one path
    /// segment, <c>**</c> spans segments, a pattern without <c>/</c> matches that name at any depth and one with <c>/</c> is
    /// anchored at the root. A missing file means nothing is cloned; unknown keys are ignored. The host only reads this file.
    /// </summary>
    internal sealed record Rules(IReadOnlyList<Regex> Include, IReadOnlyList<Regex> Exclude)
    {
        internal static readonly Rules None = new([], []);

        internal bool Selects(string relative) => Include.Any(rule => rule.IsMatch(relative)) && !Exclude.Any(rule => rule.IsMatch(relative));
    }

    /// <summary>Reads the source checkout's rules; a malformed or newer-versioned file yields no rules and a diagnostic, never an exception.</summary>
    internal static Rules ReadRules(string source, Action<string>? report = null)
    {
        var path = Path.Combine(source, ".sharprail", "workspace.toml");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null) return Rules.None;
            if (info.Length > ConfigLimit) { report?.Invoke($"{ConfigPath} is larger than {ConfigLimit} bytes; ignored."); return Rules.None; }
            var root = ProjectToml.Parse(File.ReadAllText(path));
            if (ProjectToml.VersionOf(root) != 1)
            { report?.Invoke($"{ConfigPath} needs version = 1; ignored."); return Rules.None; }
            if (root["clone"] is not { } clone) return Rules.None;
            if (clone is not JsonObject table) { report?.Invoke($"{ConfigPath}: [clone] must be a table; ignored."); return Rules.None; }
            return new Rules(Patterns(table, "include", report), Patterns(table, "exclude", report));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TomlException or InvalidOperationException)
        {
            report?.Invoke($"{ConfigPath} is unreadable ({error.Message}); ignored.");
            return Rules.None;
        }
    }

    private static List<Regex> Patterns(JsonObject clone, string name, Action<string>? report)
    {
        var rules = new List<Regex>();
        if (!clone.TryGetPropertyValue(name, out var list)) return rules;
        if (list is not JsonArray array) { report?.Invoke($"{ConfigPath}: {name} must be an array; ignored."); return rules; }
        foreach (var item in array)
        {
            var pattern = item is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim().TrimEnd('/') : "";
            if (pattern.Length == 0 || pattern.StartsWith('/') || pattern.Contains('\\') || pattern.Split('/').Any(part => part is ".." or "." or "" or ".git"))
            { report?.Invoke($"{ConfigPath}: skipped invalid pattern {item}."); continue; }
            rules.Add(Glob(pattern));
        }
        return rules;
    }

    private static Regex Glob(string pattern)
    {
        var text = new StringBuilder(pattern.Contains('/') ? "^" : "^(?:.*/)?");
        for (var index = 0; index < pattern.Length; index++)
        {
            var character = pattern[index];
            if (character == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                index++;
                if (index + 1 < pattern.Length && pattern[index + 1] == '/') { index++; text.Append("(?:.*/)?"); }
                else text.Append(".*");
            }
            else if (character == '*') text.Append("[^/]*");
            else if (character == '?') text.Append("[^/]");
            else text.Append(Regex.Escape(character.ToString()));
        }
        return new Regex(text.Append('$').ToString(), RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Clones the paths the source project's <see cref="ConfigPath"/> includes into <paramref name="target"/> and returns the
    /// relative paths cloned; without that file nothing is. Discovery is one Git call that reports ignored paths without
    /// descending into them (so a <c>node_modules</c> is one entry, not a walk); only entries the rules select qualify, and
    /// Git lists a directory as a unit only when nothing in it is tracked, so tracked content is never cloned. Existing
    /// targets are never replaced.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> CloneConfiguredAsync(string source, string target, CancellationToken cancellationToken,
        Func<string, string, bool>? clone = null, Action<string>? report = null)
    {
        if (clone is null && !Supported) return [];
        clone ??= TryClone;
        var cloned = new List<string>();
        try
        {
            var rules = await Task.Run(() => ReadRules(source, report), cancellationToken);
            if (rules.Include.Count == 0) return cloned;
            var listing = await GitRepository.RunAsync(source, cancellationToken, "ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z");
            foreach (var entry in listing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = entry.TrimEnd('/');
                if (relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal) || !rules.Selects(relative)) continue;
                var from = Path.Combine(source, relative);
                var to = Path.Combine(target, relative);
                var parent = Path.GetDirectoryName(to)!;
                // The target's parent comes from the checkout; a path that already exists there (even a dangling link) is left alone.
                if (!Directory.Exists(parent) || Path.Exists(to) || new FileInfo(to).LinkTarget is not null) continue;
                var info = new FileInfo(from);
                if (info.LinkTarget is not null || !(entry.EndsWith('/') ? Directory.Exists(from) : info.Exists)) continue;
                if (clone(from, to)) cloned.Add(relative);
            }
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException) { }
        return cloned;
    }
}