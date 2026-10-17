using Avalonia.Threading;

namespace Ghostty.Avalonia;

internal sealed class TerminalLinkHighlights(Action changed) : IDisposable
{
    internal Func<string, CancellationToken, Task<IReadOnlyList<TerminalLink>>>? Resolve;
    private readonly Dictionary<string, (DateTime Until, IReadOnlyList<TerminalLink> Links)> cache = [];
    private CancellationTokenSource? pending;
    private string? text;
    private bool disposed;

    internal IReadOnlyList<TerminalLink> Update(string value)
    {
        if (disposed || Resolve is null || value.Length > 16384) return [];
        if (text != value) { pending?.Cancel(); pending = null; text = value; }
        if (cache.TryGetValue(value, out var entry) && DateTime.UtcNow < entry.Until) return entry.Links;
        if (pending is null)
        {
            var call = pending = new CancellationTokenSource();
            _ = Find(value, call);
        }
        return [];
    }

    private async Task Find(string value, CancellationTokenSource call)
    {
        try
        {
            await Task.Delay(100, call.Token).ConfigureAwait(false);
            var links = await Task.Run(() => Resolve!(value, call.Token), call.Token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (disposed || call.IsCancellationRequested || pending != call || text != value) return;
                if (cache.Count >= 32) cache.Clear();
                cache[value] = (DateTime.UtcNow.AddSeconds(links.Count > 0 ? 30 : 1), links);
                pending = null;
                changed();
            });
        }
        catch (Exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (pending != call) return;
                pending = null;
                if (!disposed)
                {
                    if (cache.Count >= 32) cache.Clear();
                    cache[value] = (DateTime.UtcNow.AddSeconds(1), []);
                }
            });
        }
        finally { call.Dispose(); }
    }

    internal void Clear()
    {
        pending?.Cancel(); pending = null;
        cache.Clear(); text = null;
    }

    public void Dispose()
    {
        disposed = true;
        Clear();
    }
}