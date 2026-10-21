using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Epub.Host;

/// <summary>
/// Reads and converts books on the host, so a remote client never touches the file: <c>open</c> returns the book as
/// Markdown and <c>resource</c> returns one image's bytes. Converted books are cached by path, size and write time.
/// </summary>
public sealed class EpubHost : PluginHostModule
{
    private const long MaximumBytes = 256L * 1024 * 1024;
    private const int Cached = 4;

    public override PluginContract Contract => EpubContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var cache = new List<(string File, long Length, DateTime Written, ConvertedBook Book)>();
        var gate = new object();

        async ValueTask<string> Locate(string workspaceId, string path, CancellationToken ct)
        {
            var workspace = await context.WorkspaceAsync(workspaceId, ct) ?? throw new InvalidOperationException($"Unknown workspace: {workspaceId}");
            var root = Path.GetFullPath(workspace.Path);
            var file = Path.GetFullPath(Path.Combine(root, path));
            if (!file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidOperationException("The book is outside the workspace.");
            return file;
        }

        ConvertedBook Load(string file, CancellationToken ct)
        {
            var info = new FileInfo(file);
            if (!info.Exists) throw new FileNotFoundException("The book no longer exists.", file);
            lock (gate)
            {
                var hit = cache.FindIndex(entry => entry.File == file && entry.Length == info.Length && entry.Written == info.LastWriteTimeUtc);
                if (hit >= 0) return cache[hit].Book;
            }
            if (info.Length > MaximumBytes) throw new InvalidDataException("The book is larger than 256 MB.");
            var book = EpubConversion.Convert(File.ReadAllBytes(file), ct);
            lock (gate)
            {
                cache.RemoveAll(entry => entry.File == file);
                cache.Insert(0, (file, info.Length, info.LastWriteTimeUtc, book));
                if (cache.Count > Cached) cache.RemoveAt(cache.Count - 1);
            }
            return book;
        }

        context.Method(EpubContract.Open, async (parameters, _, ct) =>
        {
            var file = await Locate(parameters.WorkspaceId, parameters.Path, ct);
            return await Task.Run(() =>
            {
                try { return Load(file, ct).Book; }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    context.Log.Warn($"Could not read {parameters.Path}: {error.Message}");
                    return new EpubBook("", "", "", [], []) { Error = error.Message };
                }
            }, ct);
        });
        context.Method(EpubContract.Resource, async (parameters, _, ct) =>
        {
            var file = await Locate(parameters.WorkspaceId, parameters.Path, ct);
            return await Task.Run(() => new EpubResource(Load(file, ct).Resources.GetValueOrDefault(parameters.Href) ?? []), ct);
        });
        return ValueTask.FromResult<PluginDisposer?>(() =>
        {
            lock (gate) cache.Clear();
            return ValueTask.CompletedTask;
        });
    }
}