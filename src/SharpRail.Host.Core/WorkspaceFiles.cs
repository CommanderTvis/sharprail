using System.Runtime.CompilerServices;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    public async IAsyncEnumerable<WorkspaceFileChanges> WatchFilesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = root;
        var watchers = new List<FileSystemWatcher>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var gate = new Lock();
        var gitChanged = false;
        var rescan = false;
        var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        void Changed(string? path, bool git = false, bool lost = false)
        {
            lock (gate)
            {
                gitChanged |= git;
                rescan |= lost;
                if (path is not null && !rescan)
                {
                    paths.Add(Path.GetRelativePath(directory, path).Replace('\\', '/'));
                    if (paths.Count > 4096) rescan = true;
                }
                if (rescan) paths.Clear();
            }
            signal.Writer.TryWrite(true);
        }
        void Watch(string watched, bool recursive, Func<string, bool> relevant, bool git = false)
        {
            if (!Directory.Exists(watched)) return;
            var watcher = new FileSystemWatcher(watched) { IncludeSubdirectories = recursive, InternalBufferSize = 64 * 1024 };
            void OnPath(string path) { if (relevant(path)) Changed(git ? null : path, git); }
            watcher.Created += (_, args) => OnPath(args.FullPath);
            watcher.Changed += (_, args) => OnPath(args.FullPath);
            watcher.Deleted += (_, args) => OnPath(args.FullPath);
            watcher.Renamed += (_, args) => { OnPath(args.OldFullPath); OnPath(args.FullPath); };
            watcher.Error += (_, _) => Changed(null, git, lost: true);
            watchers.Add(watcher);
            watcher.EnableRaisingEvents = true;
        }
        try
        {
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
            Watch(directory, true, path =>
            {
                var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
                return relative != ".git" && !relative.StartsWith(".git/", StringComparison.Ordinal);
            });
            var (gitDirectory, commonDirectory) = GitDirectories(directory);
            if (gitDirectory is not null)
            {
                Watch(gitDirectory, false, path => Path.GetFileName(path) == "HEAD", git: true);
                Watch(Path.Combine(commonDirectory!, "refs"), true, _ => true, git: true);
            }
            yield return new([]);
            while (true)
            {
                await signal.Reader.ReadAsync(cancellationToken);
                await Task.Delay(50, cancellationToken);
                WorkspaceFileChanges next;
                lock (gate)
                {
                    while (signal.Reader.TryRead(out _)) { }
                    next = new(paths.ToArray(), gitChanged, rescan);
                    paths.Clear(); gitChanged = false; rescan = false;
                }
                yield return next;
            }
        }
        finally
        {
            foreach (var watcher in watchers) watcher.Dispose();
        }
    }

    private static (string? Git, string? Common) GitDirectories(string directory)
    {
        try
        {
            var marker = Path.Combine(directory, ".git");
            string? git = null;
            if (Directory.Exists(marker)) git = marker;
            else if (File.Exists(marker))
            {
                var text = File.ReadAllText(marker).Trim();
                if (!text.StartsWith("gitdir:", StringComparison.Ordinal)) return (null, null);
                git = Path.GetFullPath(text["gitdir:".Length..].Trim(), directory);
            }
            if (git is null || !Directory.Exists(git)) return (null, null);
            var common = Path.Combine(git, "commondir");
            return (git, File.Exists(common) ? Path.GetFullPath(File.ReadAllText(common).Trim(), git) : git);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return (null, null); }
    }
}