using Avalonia.Threading;

namespace Ghostty.Avalonia;

internal sealed class TerminalLinkHighlights(Action changed) : IDisposable
{
    internal Func<string, CancellationToken, Task<IReadOnlyList<TerminalLink>>>? Resolve;
    private readonly Dictionary<string, (DateTime Until, IReadOnlyList<TerminalLink> Links)> cache = [];
    private CancellationTokenSource? pending;
    private string? text;
    private bool disposed;
    private string resolvedText = "";
    private IReadOnlyList<TerminalLink> resolvedLinks = [];

    // The visible text last offered and the links resolved for exactly that text, for checks.
    internal IReadOnlyList<(string Visible, TerminalLink Link)> Underlined =>
        text is null || text != resolvedText ? [] : [.. resolvedLinks.Select(link => (text.Substring(link.Start, link.Length), link))];

    internal IReadOnlyList<TerminalLink> Update(string value)
    {
        if (disposed || Resolve is null || value.Length > 16384) return [];
        if (text != value) { pending?.Cancel(); pending = null; text = value; }
        if (cache.TryGetValue(value, out var entry) && DateTime.UtcNow < entry.Until) { resolvedText = value; resolvedLinks = entry.Links; return entry.Links; }
        if (pending is null)
        {
            var call = pending = new CancellationTokenSource();
            _ = Find(value, call);
        }
        return Retained(value);
    }

    private IReadOnlyList<TerminalLink> Retained(string value)
    {
        // Only reuse whole unchanged lines: offsets may move when output scrolls.
        var lines = new Dictionary<string, List<TerminalLink>>(StringComparer.Ordinal);
        var start = 0;
        foreach (var line in resolvedText.Split('\n'))
        {
            var links = resolvedLinks.Where(link => link.Start >= start && link.Start + link.Length <= start + line.Length)
                .Select(link => link with { Start = link.Start - start }).ToList();
            if (links.Count > 0) lines.TryAdd(line, links);
            start += line.Length + 1;
        }
        var retained = new List<TerminalLink>();
        start = 0;
        foreach (var line in value.Split('\n'))
        {
            if (lines.TryGetValue(line, out var links))
                retained.AddRange(links.Select(link => link with { Start = start + link.Start }));
            start += line.Length + 1;
        }
        return retained;
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
                resolvedText = value; resolvedLinks = links;
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
        resolvedText = ""; resolvedLinks = [];
    }

    public void Dispose()
    {
        disposed = true;
        Clear();
    }
}