using System.Security.Cryptography;
using System.Text;

namespace Tereditor.Plugins.Visualize.Host;

/// <summary>
/// The Markdown source of each shown Markdown drawing, kept as a real file in a private directory under the host's
/// state directory, so a selection in the pane can be reported as a selection in a file the agent can read. One stable
/// name per workspace and tab; written atomically and only when the text changed; removed with the drawing.
/// </summary>
internal sealed class VisualizationSourceFiles
{
    private readonly string directory;
    private readonly IReadOnlyCollection<string> previous;
    private readonly Action<IReadOnlyCollection<string>> remember;
    private readonly Lock gate = new();
    private readonly Dictionary<(string Workspace, string Tab), (string Path, string Digest)> written = [];

    /// <param name="directory">Where the files live.</param>
    /// <param name="previous">The paths an earlier run of this host wrote. Only those are ever swept: another host sharing the directory keeps its own files.</param>
    /// <param name="remember">Receives every path this host owns whenever that set changes, for the next run's sweep.</param>
    internal VisualizationSourceFiles(string directory, IReadOnlyCollection<string>? previous = null, Action<IReadOnlyCollection<string>>? remember = null)
    {
        this.directory = directory;
        this.previous = previous ?? [];
        this.remember = remember ?? (_ => { });
    }

    private void Remember() => remember([.. written.Values.Select(entry => entry.Path).Order(StringComparer.Ordinal)]);

    /// <summary>The host state directory's drawings folder, by the host's rule: <c>TEREDITOR_STATE_DIR</c>, else <c>~/.tereditor</c>.</summary>
    internal static string DefaultDirectory() => Path.Combine(
        Environment.GetEnvironmentVariable("TEREDITOR_STATE_DIR") is { Length: > 0 } configured ? configured
            : Path.Combine(Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tereditor"),
        "visualize");

    internal static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private string PathOf(string workspaceId, string tabKey) =>
        Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspaceId + "\0" + tabKey)))[..24].ToLowerInvariant() + ".md");

    /// <summary>Makes the file hold exactly the text and returns its absolute path; null when it cannot be written, so the drawing stays in memory only.</summary>
    internal string? Write(string workspaceId, string tabKey, string text)
    {
        lock (gate)
        {
            try
            {
                var path = PathOf(workspaceId, tabKey);
                var digest = Digest(text);
                if (written.TryGetValue((workspaceId, tabKey), out var last) && last.Path == path && last.Digest == digest && File.Exists(path)) return path;
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
                else
                {
                    // A folder left by an earlier run, or made with someone else's umask, is tightened too.
                    Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                var temporary = path + ".tmp";
                File.Delete(temporary);
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options))
                    stream.Write(new UTF8Encoding(false).GetBytes(text));
                File.Move(temporary, path, true);
                var isNew = !written.ContainsKey((workspaceId, tabKey));
                written[(workspaceId, tabKey)] = (path, digest);
                if (isNew) Remember();
                return path;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                written.Remove((workspaceId, tabKey));
                return null;
            }
        }
    }

    internal void Delete(string workspaceId, string tabKey)
    {
        lock (gate)
        {
            if (written.Remove((workspaceId, tabKey))) Remember();
            TryDelete(PathOf(workspaceId, tabKey));
        }
    }

    internal void DeleteWorkspace(string workspaceId)
    {
        lock (gate)
            foreach (var key in written.Keys.Where(key => key.Workspace == workspaceId).ToArray())
            {
                TryDelete(written[key].Path);
                written.Remove(key);
                Remember();
            }
    }

    /// <summary>Removes the files an earlier run of this host wrote and no live drawing owns now: what it left behind when it ended without cleaning up.</summary>
    internal void Sweep()
    {
        lock (gate)
        {
            var owned = written.Values.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var file in previous)
                if (!owned.Contains(file)) TryDelete(file);
            Remember();
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
    }
}