using Avalonia.Threading;

namespace Ghostty.Avalonia;

internal sealed class TerminalLinkHighlights(Action changed) : IDisposable
{
    internal Func<string, CancellationToken, Task<IReadOnlyList<TerminalLink>>>? Resolve;
    // A failed resolution is cached without links (null) so it is retried later and never clears a line's links.
    private readonly Dictionary<string, (DateTime Until, IReadOnlyList<TerminalLink>? Links)> cache = [];
    private CancellationTokenSource? pending;
    private string? text;
    private bool disposed;
    private string resolvedText = "";
    private IReadOnlyList<TerminalLink> resolvedLinks = [];

    // The visible text last offered and the links resolved for exactly that text, for checks.
    internal IReadOnlyList<(string Visible, TerminalLink Link)> Underlined =>
        text is null || text != resolvedText ? [] : [.. resolvedLinks.Select(link => (text.Substring(link.Start, link.Length), link))];

    // Links attached to the text of each logical line they were resolved on, like IntelliJ's terminal keeps filter results
    // on the output model: a line keeps its links while its text is unchanged, wherever it scrolls or however often the
    // view rebuilds, and only a resolution covering that line replaces them. Unresolved lines fill in asynchronously.
    private readonly Dictionary<string, TerminalLink[]> lines = new(StringComparer.Ordinal);

    /// <summary>Whether the last <see cref="Update"/> returned a resolution of exactly that text, so a line it shows
    /// without links has none; otherwise the result only carries lines resolved earlier.</summary>
    internal bool Settled { get; private set; }

    internal IReadOnlyList<TerminalLink> Update(string value)
    {
        Settled = false;
        if (disposed || Resolve is null || value.Length > 16384) return [];
        if (text != value) { pending?.Cancel(); pending = null; text = value; }
        if (cache.TryGetValue(value, out var entry) && DateTime.UtcNow < entry.Until)
        {
            if (entry.Links is null) return Retained(value);
            resolvedText = value; resolvedLinks = entry.Links;
            Remember(value, entry.Links);
            Settled = true;
            return entry.Links;
        }
        if (pending is null)
        {
            var call = pending = new CancellationTokenSource();
            _ = Find(value, call);
        }
        return Retained(value);
    }

    private void Remember(string value, IReadOnlyList<TerminalLink> links)
    {
        if (lines.Count > 4096) lines.Clear();
        var start = 0;
        foreach (var line in value.Split('\n'))
        {
            var found = links.Where(link => link.Start >= start && link.Start + link.Length <= start + line.Length)
                .Select(link => link with { Start = link.Start - start }).ToArray();
            // Trailing blanks are the grid's width, not the line's text: a resize keeps the line's links.
            if (found.Length > 0) lines[line.TrimEnd(' ')] = found;
            else lines.Remove(line.TrimEnd(' '));
            start += line.Length + 1;
        }
    }

    private List<TerminalLink> Retained(string value)
    {
        var retained = new List<TerminalLink>();
        var start = 0;
        foreach (var line in value.Split('\n'))
        {
            if (lines.TryGetValue(line.TrimEnd(' '), out var links))
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
                Remember(value, links);
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
                    cache[value] = (DateTime.UtcNow.AddSeconds(1), null);
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
        lines.Clear();
    }

    public void Dispose()
    {
        disposed = true;
        Clear();
    }
}