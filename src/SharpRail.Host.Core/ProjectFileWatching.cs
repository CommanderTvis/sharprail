using System.Runtime.CompilerServices;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    public async IAsyncEnumerable<FileChange> WatchFilesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var directory = root;
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"Directory does not exist: {directory}");
        var signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var gate = new object();
        var rescan = false;
        var lastChange = DateTime.UtcNow;
        void Changed(string? path)
        {
            lock (gate)
            {
                lastChange = DateTime.UtcNow;
                if (path is null || paths.Count >= 100) rescan = true;
                else paths.Add(path);
            }
            signals.Writer.TryWrite(true);
        }
        // Subscriptions to one workspace share its watchers; each keeps its own coalescing window.
        var subscription = WorkspaceWatches.Subscribe(directory, Changed);
        try
        {
            // Re-read after registration to cover changes between the initial listing and subscription.
            yield return new([], true);
            while (await signals.Reader.WaitToReadAsync(cancellationToken))
            {
                var started = DateTime.UtcNow;
                while (true)
                {
                    await Task.Delay(50, cancellationToken);
                    DateTime last;
                    lock (gate) last = lastChange;
                    if (DateTime.UtcNow - last >= TimeSpan.FromMilliseconds(250) || DateTime.UtcNow - started >= TimeSpan.FromSeconds(1)) break;
                }
                FileChange change;
                lock (gate)
                {
                    while (signals.Reader.TryRead(out _)) { }
                    change = new(paths.ToArray(), rescan);
                    paths.Clear(); rescan = false;
                }
                yield return change;
            }
        }
        finally { subscription.Dispose(); }
    }

    internal static (string? GitDirectory, string? CommonDirectory) ResolveGitDirectories(string directory)
    {
        try
        {
            var marker = Path.Combine(directory, ".git");
            if (Directory.Exists(marker)) return (marker, marker);
            if (!File.Exists(marker)) return (null, null);
            var text = File.ReadAllText(marker).Trim();
            if (!text.StartsWith("gitdir:", StringComparison.Ordinal)) return (null, null);
            var gitDirectory = Path.GetFullPath(text["gitdir:".Length..].Trim(), directory);
            var common = Path.Combine(gitDirectory, "commondir");
            var commonDirectory = File.Exists(common) ? Path.GetFullPath(File.ReadAllText(common).Trim(), gitDirectory) : gitDirectory;
            return (gitDirectory, commonDirectory);
        }
        catch (IOException) { return (null, null); }
        catch (UnauthorizedAccessException) { return (null, null); }
    }

    public ValueTask PrewarmWorkspaceAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceWatches.Prewarm(ProjectPaths.Resolve(path));
        return ValueTask.CompletedTask;
    }
}