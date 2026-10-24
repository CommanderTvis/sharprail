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

    /// <summary>
    /// Whether a non-blank line of <paramref name="value"/>, other than line <paramref name="skip"/>, has never been
    /// resolved, so presenting it now would show it without underlines that may follow. Reads in-memory state only.
    /// </summary>
    internal bool Unknown(string value, int skip)
    {
        if (disposed || Resolve is null || value.Length > 16384) return false;
        var index = 0;
        foreach (var line in value.Split('\n'))
        {
            var key = line.TrimEnd(' ');
            if (index != skip && key.Length > 0 && Known(key, index == 0) is null) return true;
            index++;
        }
        return false;
    }

    // A line's links by its text. The first visible row may be the tail of a line whose start scrolled away: it keeps
    // the visible part of that line's links instead of losing them until the tail itself resolves.
    private TerminalLink[]? Known(string key, bool first)
    {
        if (lines.TryGetValue(key, out var links)) return links;
        // A short tail could end an unrelated line.
        if (!first || key.Length < 8) return null;
        foreach (var (whole, found) in lines)
        {
            if (whole.Length <= key.Length || !whole.EndsWith(key, StringComparison.Ordinal)) continue;
            var cut = whole.Length - key.Length;
            return [.. found.Where(link => link.Start + link.Length > cut)
                .Select(link => link.Start >= cut ? link with { Start = link.Start - cut }
                    : link with { Start = 0, Length = link.Start + link.Length - cut })];
        }
        return null;
    }

    internal IReadOnlyList<TerminalLink> Update(string value, bool immediate = false)
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
            _ = Find(value, call, immediate ? 0 : 100);
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
            // A line without links is remembered too, so it is known and need not wait for a resolution again.
            lines[line.TrimEnd(' ')] = found;
            start += line.Length + 1;
        }
    }

    private List<TerminalLink> Retained(string value)
    {
        var retained = new List<TerminalLink>();
        var start = 0;
        var first = true;
        foreach (var line in value.Split('\n'))
        {
            if (Known(line.TrimEnd(' '), first) is { } links)
                retained.AddRange(links.Select(link => link with { Start = start + link.Start }));
            start += line.Length + 1;
            first = false;
        }
        return retained;
    }

    private async Task Find(string value, CancellationTokenSource call, int delay)
    {
        try
        {
            if (delay > 0) await Task.Delay(delay, call.Token).ConfigureAwait(false);
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